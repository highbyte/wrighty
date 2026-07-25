using Highbyte.Wrighty.Claims;

namespace Highbyte.Wrighty.UnitTests.Claims;

public sealed class ClaimMarkerTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-07-16T10:00:00Z");

    [Fact]
    public void V3_marker_round_trips_all_authoritative_fields()
    {
        var claim = Event("acquired", "token-2", null);
        var body = ClaimMarker.Format(claim);

        Assert.Contains(ClaimMarker.Prefix, body);
        Assert.DoesNotContain("claimToken</", body);
        Assert.True(ClaimMarker.TryParse(body, out var parsed));
        Assert.Equal(claim, parsed);
    }

    [Fact]
    public void Transition_requires_previous_token()
    {
        var invalid = ClaimMarker.Format(Event("takenOver", "token-2", null));
        Assert.False(ClaimMarker.TryParse(invalid, out _));
    }

    [Fact]
    public void Requeued_transition_round_trips_and_is_described_as_queued()
    {
        var claim = Event("requeued", "token-2", "token-1");
        var body = ClaimMarker.Format(claim);

        Assert.Contains("agent session queued", body);
        Assert.True(ClaimMarker.TryParse(body, out var parsed));
        Assert.Equal("requeued", parsed.EventType);
    }

    [Fact]
    public void Legacy_markers_are_detected_but_not_parsed_as_v3()
    {
        foreach (var version in new[] { 1, 2 })
        {
            var body = $"<!-- wrighty-claim:v{version}\n{{\"version\":{version}}}\n-->";
            Assert.True(ClaimMarker.HasLegacyMarker(body));
            Assert.False(ClaimMarker.TryParse(body, out _));
        }
    }

    private static ClaimRecord Event(string type, string token, string? previous) =>
        new(3, "event-1", "worker-1", Now, Now.AddHours(1), type,
            "codex:session-1", token, previous, "codex", "session-1", "agent");
}
