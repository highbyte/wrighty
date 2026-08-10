using System.Text.Json;
using Highbyte.Wrighty.Processes;

namespace Highbyte.Wrighty.Workers;

/// <summary>
/// Asks <c>codex app-server</c> what models this installation can run.
///
/// Chosen as the first adapter because it answers most completely: every model carries its
/// supported reasoning efforts and its default, so it exercises the whole result shape rather than
/// the degraded parts of it.
///
/// The transport is JSON-RPC 2.0 over stdio: <c>initialize</c>, the <c>initialized</c>
/// notification, then <c>model/list</c>. No inference turn is started and no session is created.
///
/// <c>app-server</c> is marked <c>[experimental]</c> by codex itself, so this exchange may stop
/// working on a codex release. That is why a failure here is a value rather than an exception:
/// when it breaks, an operator loses a picker, not the ability to configure a profile.
/// </summary>
public sealed class CodexModelDiscovery(
    IAgentModelProbe probe, Func<DateTimeOffset>? clock = null) : IAgentModelDiscovery
{
    public string Agent => "codex";

    public CodexModelDiscovery(IExecutableResolver executables)
        : this(new AgentModelProbe(executables))
    {
    }

    public async Task<AgentModelCatalog> DiscoverAsync(CancellationToken cancellationToken)
    {
        var now = (clock ?? (() => DateTimeOffset.UtcNow))();
        var (answer, failure) = await probe.ExchangeAsync(
            "codex",
            ["app-server"],
            [
                // The client identity codex echoes into its user-agent. Named plainly: it reaches a
                // vendor's telemetry, so it should say what it is rather than impersonate an editor.
                new ProbeTurn(
                    ["""
                     {"jsonrpc":"2.0","id":1,"method":"initialize","params":{"clientInfo":{"name":"wrighty","title":"Wrighty","version":"1"}}}
                     """],
                    Reply(1)),
                new ProbeTurn(["""{"jsonrpc":"2.0","method":"initialized","params":{}}"""]),
                new ProbeTurn(["""{"jsonrpc":"2.0","id":2,"method":"model/list","params":{}}"""], Reply(2))
            ],
            cancellationToken);

        if (answer is not { } reply)
        {
            return AgentModelCatalog.Unavailable(Agent, failure, now);
        }

        if (!reply.TryGetProperty("result", out var result) ||
            !result.TryGetProperty("data", out var data) ||
            data.ValueKind != JsonValueKind.Array)
        {
            // A well-formed JSON-RPC error reply also lands here. Both mean the same thing to a
            // caller: codex did not produce a list.
            return AgentModelCatalog.Unavailable(Agent, ModelDiscoveryFailure.Unrecognized, now);
        }

        var models = new List<AgentModel>();
        string? current = null;
        foreach (var entry in data.EnumerateArray())
        {
            if (Read(entry) is not { } model)
            {
                continue;
            }

            models.Add(model);
            if (entry.TryGetProperty("isDefault", out var isDefault) &&
                isDefault.ValueKind == JsonValueKind.True)
            {
                current = model.Id;
            }
        }

        return new AgentModelCatalog(Agent, models, ModelDiscoveryFailure.None, current, now);
    }

    /// <summary>
    /// Matches a JSON-RPC reply by request id. Codex interleaves unsolicited notifications such as
    /// <c>remoteControl/status/changed</c> with its replies, so taking the next line would take
    /// whichever arrived first.
    /// </summary>
    private static Func<JsonElement, bool> Reply(int id) =>
        element => element.TryGetProperty("id", out var value) &&
                   value.ValueKind == JsonValueKind.Number &&
                   value.GetInt32() == id;

    private static AgentModel? Read(JsonElement entry)
    {
        if (entry.ValueKind != JsonValueKind.Object ||
            !entry.TryGetProperty("id", out var id) ||
            id.GetString() is not { Length: > 0 } identifier)
        {
            return null;
        }

        // Codex marks retired or internal models hidden. Offering one would let an operator pin a
        // model their own CLI declines to show them.
        if (entry.TryGetProperty("hidden", out var hidden) && hidden.ValueKind == JsonValueKind.True)
        {
            return null;
        }

        var efforts = ReadEfforts(entry);
        return new AgentModel(
            identifier,
            Text(entry, "displayName"),
            // Codex repeats the identifier in "model" rather than resolving an alias, so it is only
            // worth recording when it actually differs.
            Text(entry, "model") is { } resolved && !string.Equals(
                resolved, identifier, StringComparison.OrdinalIgnoreCase) ? resolved : null,
            // Codex enumerates efforts for every model, so an empty list here is the vendor saying
            // this model takes none — not that the answer is unavailable.
            EffortSupport.Yes,
            efforts,
            Text(entry, "defaultReasoningEffort"));
    }

    private static List<string> ReadEfforts(JsonElement entry)
    {
        if (!entry.TryGetProperty("supportedReasoningEfforts", out var listed) ||
            listed.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var efforts = new List<string>();
        foreach (var effort in listed.EnumerateArray())
        {
            if (effort.ValueKind == JsonValueKind.Object &&
                Text(effort, "reasoningEffort") is { } level)
            {
                efforts.Add(level);
            }
        }

        return efforts;
    }

    private static string? Text(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.String &&
        value.GetString() is { Length: > 0 } text
            ? text
            : null;
}
