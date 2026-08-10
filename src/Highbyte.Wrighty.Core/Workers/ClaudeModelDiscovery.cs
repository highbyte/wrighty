using System.Text.Json;
using Highbyte.Wrighty.Processes;

namespace Highbyte.Wrighty.Workers;

/// <summary>
/// Asks Claude Code what models this installation can run.
///
/// The transport is the one Wrighty already uses to launch claude — stream-json in and out — with a
/// single <c>control_request</c> of subtype <c>initialize</c>. That request is answered from local
/// state before any turn begins, so nothing is spent.
///
/// This is the adapter the 033 amendment believed impossible without a .NET Agent SDK. No SDK is
/// involved: the CLI answers the question directly, and the SDK's own <c>supportedModels()</c>
/// reaches the same place over the same channel.
///
/// Claude states effort support per model, but only positively: a model that takes none omits the
/// field rather than denying it, so this adapter reports unknown and leaves the run-time fallback to
/// catch it. See the note on that mapping below.
/// </summary>
public sealed class ClaudeModelDiscovery(
    IAgentModelProbe probe, Func<DateTimeOffset>? clock = null) : IAgentModelDiscovery
{
    public string Agent => "claude";

    public ClaudeModelDiscovery(IExecutableResolver executables)
        : this(new AgentModelProbe(executables))
    {
    }

    public async Task<AgentModelCatalog> DiscoverAsync(CancellationToken cancellationToken)
    {
        var now = (clock ?? (() => DateTimeOffset.UtcNow))();
        var (answer, failure) = await probe.ExchangeAsync(
            "claude",
            [
                "--print",
                "--output-format", "stream-json",
                "--input-format", "stream-json",
                "--verbose"
            ],
            [
                new ProbeTurn(
                    ["""{"type":"control_request","request_id":"wrighty-models","request":{"subtype":"initialize"}}"""],
                    // Matched on type and request id: the same channel carries hook events and
                    // system messages, any of which can arrive first.
                    element => Text(element, "type") == "control_response" &&
                               element.TryGetProperty("response", out var response) &&
                               Text(response, "request_id") == "wrighty-models")
            ],
            cancellationToken);

        if (answer is not { } reply)
        {
            return AgentModelCatalog.Unavailable(Agent, failure, now);
        }

        if (!reply.TryGetProperty("response", out var outer) ||
            Text(outer, "subtype") == "error")
        {
            return AgentModelCatalog.Unavailable(Agent, ModelDiscoveryFailure.Unrecognized, now);
        }

        // Deliberately reaching only for "models". The same payload carries account.email,
        // account.organization and subscriptionType; none of it is read, so none of it can be
        // logged, cached, or written to a settings file by accident.
        if (!outer.TryGetProperty("response", out var inner) ||
            !inner.TryGetProperty("models", out var listed) ||
            listed.ValueKind != JsonValueKind.Array)
        {
            return AgentModelCatalog.Unavailable(Agent, ModelDiscoveryFailure.Unrecognized, now);
        }

        var models = new List<AgentModel>();
        string? current = null;
        foreach (var entry in listed.EnumerateArray())
        {
            if (Read(entry) is not { } model)
            {
                continue;
            }

            models.Add(model);
            // Claude names its default selection "default" rather than flagging it. Reported as
            // that identifier and not as the model behind it: this names a *row* in the list, and
            // callers match it against one. Reporting `claude-opus-5[1m]` here looked more
            // informative and matched nothing, because no entry carries it as its own id — the
            // row already shows what it resolves to.
            if (string.Equals(model.Id, "default", StringComparison.Ordinal))
            {
                current = model.Id;
            }
        }

        return new AgentModelCatalog(Agent, models, ModelDiscoveryFailure.None, current, now);
    }

    private static AgentModel? Read(JsonElement entry)
    {
        if (entry.ValueKind != JsonValueKind.Object || Text(entry, "value") is not { } identifier)
        {
            return null;
        }

        // Measured 2026-08-10 against Claude Code 2.1.226: five of six models carry this key
        // explicitly true, and `haiku` — the one model that takes no effort — **omits it entirely**
        // rather than reporting false. So absence is claude's way of saying "no effort", but it is
        // also what a changed payload would look like, and the two are indistinguishable here.
        //
        // Unknown is therefore the honest reading, and the safe one: a refusal inferred from a
        // missing field would block a mapping the moment claude reorganised its response. The
        // run-time relaunch fallback still catches the case, at the cost of one cheap launch.
        var support = entry.TryGetProperty("supportsEffort", out var supports)
            ? supports.ValueKind switch
            {
                JsonValueKind.True => EffortSupport.Yes,
                JsonValueKind.False => EffortSupport.No,
                _ => EffortSupport.Unknown
            }
            : EffortSupport.Unknown;

        return new AgentModel(
            identifier,
            Text(entry, "displayName"),
            // Claude's aliases are the point of its model list: 'opus' and 'default' can resolve to
            // the same concrete model, and an operator comparing two profiles needs to see that.
            Text(entry, "resolvedModel"),
            support,
            support == EffortSupport.Yes ? ReadEfforts(entry) : []);
    }

    private static IReadOnlyList<string> ReadEfforts(JsonElement entry)
    {
        if (!entry.TryGetProperty("supportedEffortLevels", out var levels) ||
            levels.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var efforts = new List<string>();
        foreach (var level in levels.EnumerateArray())
        {
            if (level.ValueKind == JsonValueKind.String &&
                level.GetString() is { Length: > 0 } text)
            {
                efforts.Add(text);
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
