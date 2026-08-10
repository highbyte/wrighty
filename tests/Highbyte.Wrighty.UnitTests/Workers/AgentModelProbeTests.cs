using System.Text.Json;
using Highbyte.Wrighty.Workers;

namespace Highbyte.Wrighty.UnitTests.Workers;

/// <summary>
/// The protocol half of the probe, over a pair of streams rather than a real vendor.
///
/// This is where the per-vendor bugs actually live — ordering and matching — and where a test needs
/// no CLI installed to reach them. Two of the three adapters depend on behaviour pinned here:
/// copilot silently ignores a request written before the previous reply is read, and a vendor that
/// dies mid-handshake must be told apart from one that answered in an unfamiliar shape.
/// </summary>
public sealed class AgentModelProbeTests
{
    private static Func<JsonElement, bool> Id(int id) =>
        element => element.TryGetProperty("id", out var value) &&
                   value.ValueKind == JsonValueKind.Number && value.GetInt32() == id;

    private static async Task<(JsonElement? Answer, ModelDiscoveryFailure Failure, string Written)>
        ConductAsync(string vendorOutput, params ProbeTurn[] turns)
    {
        var written = new StringWriter();
        var result = await AgentModelProbe.ConductAsync(
            written, new StringReader(vendorOutput), turns, CancellationToken.None);
        return (result.Answer, result.Failure, written.ToString());
    }

    [Fact]
    public async Task A_reply_is_matched_by_identity_not_by_arrival_order()
    {
        // Every vendor interleaves unsolicited events with its replies, so taking the next line
        // would take whichever arrived first.
        var (answer, failure, _) = await ConductAsync(
            """
            {"method":"remoteControl/status/changed","params":{}}
            {"id":99,"result":{"wrong":true}}
            {"id":2,"result":{"right":true}}
            """,
            new ProbeTurn(["""{"id":2,"method":"model/list"}"""], Id(2)));

        Assert.Equal(ModelDiscoveryFailure.None, failure);
        Assert.True(answer!.Value.GetProperty("result").GetProperty("right").GetBoolean());
    }

    [Fact]
    public async Task A_later_request_is_written_only_after_the_previous_reply_arrives()
    {
        // Copilot's ACP server answers initialize but silently drops a session/new written before
        // that answer is read. An adapter that pipelined both would pass a naive test and hang
        // against the real CLI, so the ordering is asserted rather than assumed.
        var order = new List<string>();
        var reader = new RecordingReader(
            ["""{"id":1,"result":{}}""", """{"id":2,"result":{}}"""], order);
        var writer = new RecordingWriter(order);

        await AgentModelProbe.ConductAsync(
            writer,
            reader,
            [
                new ProbeTurn(["first"], Id(1)),
                new ProbeTurn(["second"], Id(2))
            ],
            CancellationToken.None);

        Assert.Equal(["wrote:first", "read", "wrote:second", "read"], order);
    }

    [Fact]
    public async Task A_notification_turn_waits_for_nothing()
    {
        // A JSON-RPC notification is never answered. Waiting for one would hang until the timeout,
        // which is how codex's three-step handshake would have stalled.
        var (answer, failure, written) = await ConductAsync(
            """{"id":2,"result":{"ok":true}}""",
            new ProbeTurn(["""{"id":1,"method":"initialize"}"""]),
            new ProbeTurn(["""{"method":"initialized"}"""]),
            new ProbeTurn(["""{"id":2,"method":"model/list"}"""], Id(2)));

        Assert.Equal(ModelDiscoveryFailure.None, failure);
        Assert.NotNull(answer);
        Assert.Contains("initialized", written);
    }

    [Fact]
    public async Task A_vendor_that_says_nothing_is_unavailable_not_unrecognized()
    {
        // The distinction an operator reads: "could not be asked" versus "answered in a form this
        // Wrighty does not understand". Reporting the second for a process that never spoke sends
        // them looking at the wrong thing.
        var (answer, failure, _) = await ConductAsync(
            string.Empty, new ProbeTurn(["""{"id":1}"""], Id(1)));

        Assert.Null(answer);
        Assert.Equal(ModelDiscoveryFailure.Unavailable, failure);
    }

    [Fact]
    public async Task A_vendor_that_answers_something_else_is_unrecognized()
    {
        var (answer, failure, _) = await ConductAsync(
            """{"id":7,"result":{"unexpected":true}}""",
            new ProbeTurn(["""{"id":1}"""], Id(1)));

        Assert.Null(answer);
        Assert.Equal(ModelDiscoveryFailure.Unrecognized, failure);
    }

    [Fact]
    public async Task Human_readable_banners_on_the_protocol_channel_are_skipped()
    {
        // Vendors print startup notices onto the same stream. A non-JSON line is noise, not a
        // protocol violation, and must not end the exchange.
        var (answer, failure, _) = await ConductAsync(
            """
            Starting up, please wait...
            not json either
            {"id":1,"result":{"ok":true}}
            """,
            new ProbeTurn(["""{"id":1}"""], Id(1)));

        Assert.Equal(ModelDiscoveryFailure.None, failure);
        Assert.NotNull(answer);
    }

    [Fact]
    public async Task A_handshake_that_stalls_partway_does_not_run_its_remaining_steps()
    {
        // The second turn's request must not be written when the first was never answered:
        // continuing would leave a request on a stream nobody is reading.
        var (_, failure, written) = await ConductAsync(
            string.Empty,
            new ProbeTurn(["first"], Id(1)),
            new ProbeTurn(["second"], Id(2)));

        Assert.Equal(ModelDiscoveryFailure.Unavailable, failure);
        Assert.DoesNotContain("second", written);
    }

    private sealed class RecordingWriter(List<string> order) : StringWriter
    {
        public override Task WriteLineAsync(ReadOnlyMemory<char> buffer, CancellationToken token)
        {
            order.Add($"wrote:{buffer}");
            return base.WriteLineAsync(buffer, token);
        }
    }

    private sealed class RecordingReader(IReadOnlyList<string> lines, List<string> order) : TextReader
    {
        private int next;

        public override ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
        {
            if (next >= lines.Count)
            {
                return ValueTask.FromResult<string?>(null);
            }

            order.Add("read");
            return ValueTask.FromResult<string?>(lines[next++]);
        }
    }
}
