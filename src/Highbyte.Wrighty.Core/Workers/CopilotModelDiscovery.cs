using System.Text.Json;
using Highbyte.Wrighty.Processes;

namespace Highbyte.Wrighty.Workers;

/// <summary>
/// Asks GitHub Copilot CLI what models this account can run, over its Agent Client Protocol server.
///
/// Two steps, and they must be sequenced: copilot answers <c>initialize</c>, but silently ignores a
/// <c>session/new</c> written before that answer is read. Measured, not assumed.
///
/// Copilot is the degraded case, and shapes the contract more than the other two:
///
/// - It reports reasoning effort for the session's **current** model only, through a
///   <c>reasoning_effort</c> config option. Every other model's effort support is genuinely
///   unknown, and is recorded as unknown rather than guessed from the current one.
/// - It publishes a **relative cost** per model, which no other vendor does.
/// - <c>session/new</c> returns account enablement, so this probe needs authentication and almost
///   certainly the network. Failing is expected on a laptop offline, and costs only the picker.
/// </summary>
public sealed class CopilotModelDiscovery(
    IAgentModelProbe probe, Func<DateTimeOffset>? clock = null) : IAgentModelDiscovery
{
    public string Agent => "copilot";

    public CopilotModelDiscovery(IExecutableResolver executables)
        : this(new AgentModelProbe(executables))
    {
    }

    public async Task<AgentModelCatalog> DiscoverAsync(CancellationToken cancellationToken)
    {
        var now = (clock ?? (() => DateTimeOffset.UtcNow))();
        var (answer, failure) = await probe.ExchangeAsync(
            "copilot",
            ["--acp"],
            [
                new ProbeTurn(
                    ["""
                     {"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":1,"clientCapabilities":{"fs":{"readTextFile":false,"writeTextFile":false}}}}
                     """],
                    Reply(1)),
                new ProbeTurn(
                    // The working directory is required by the protocol but irrelevant here: no
                    // prompt is sent, so nothing in it is read.
                    ["""{"jsonrpc":"2.0","id":2,"method":"session/new","params":{"cwd":".","mcpServers":[]}}"""],
                    Reply(2))
            ],
            cancellationToken);

        if (answer is not { } reply)
        {
            return AgentModelCatalog.Unavailable(Agent, failure, now);
        }

        if (reply.TryGetProperty("error", out var error))
        {
            // Copilot reports an unauthenticated client as an ordinary JSON-RPC error. Separating it
            // matters: "sign in" is actionable, "unavailable" is not.
            return AgentModelCatalog.Unavailable(
                Agent,
                MentionsAuthentication(error) ? ModelDiscoveryFailure.NotAuthenticated
                    : ModelDiscoveryFailure.Unrecognized,
                now);
        }

        if (!reply.TryGetProperty("result", out var result) ||
            !result.TryGetProperty("models", out var models) ||
            !models.TryGetProperty("availableModels", out var available) ||
            available.ValueKind != JsonValueKind.Array)
        {
            return AgentModelCatalog.Unavailable(Agent, ModelDiscoveryFailure.Unrecognized, now);
        }

        var current = Text(models, "currentModelId");
        var currentEfforts = ReadCurrentModelEfforts(result);
        var discovered = new List<AgentModel>();
        foreach (var entry in available.EnumerateArray())
        {
            if (Read(entry, current, currentEfforts) is { } model)
            {
                discovered.Add(model);
            }
        }

        return new AgentModelCatalog(Agent, discovered, ModelDiscoveryFailure.None, current, now);
    }

    private static AgentModel? Read(
        JsonElement entry, string? currentModelId, IReadOnlyList<string>? currentEfforts)
    {
        if (entry.ValueKind != JsonValueKind.Object || Text(entry, "modelId") is not { } identifier)
        {
            return null;
        }

        // Effort is known only for the model this session happens to be on. Copying its levels onto
        // the others would be exactly the per-vendor generalisation this plan exists to stop —
        // 'minimal' and 'max' appear in Wrighty's declared copilot set and in no model's real one.
        var isCurrent = currentModelId is not null &&
            string.Equals(identifier, currentModelId, StringComparison.OrdinalIgnoreCase);
        var support = isCurrent && currentEfforts is { Count: > 0 }
            ? EffortSupport.Yes
            : EffortSupport.Unknown;

        return new AgentModel(
            identifier,
            Text(entry, "name"),
            Effort: support,
            SupportedEfforts: support == EffortSupport.Yes ? currentEfforts : [],
            // Reported for the operator to weigh, never for Wrighty to rank by. Kept as the vendor's
            // own label ("6x") rather than parsed: it is not a quantity Wrighty may compute with.
            RelativeCost: Meta(entry, "copilotUsage"));
    }

    /// <summary>
    /// Pulls the effort levels out of the session's <c>reasoning_effort</c> select. These describe
    /// the current model alone, which is why the caller applies them to nothing else.
    /// </summary>
    private static IReadOnlyList<string>? ReadCurrentModelEfforts(JsonElement result)
    {
        if (!result.TryGetProperty("configOptions", out var options) ||
            options.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var option in options.EnumerateArray())
        {
            if (option.ValueKind != JsonValueKind.Object ||
                Text(option, "id") != "reasoning_effort" ||
                !option.TryGetProperty("options", out var values) ||
                values.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            var efforts = new List<string>();
            foreach (var value in values.EnumerateArray())
            {
                if (value.ValueKind == JsonValueKind.Object && Text(value, "value") is { } level)
                {
                    efforts.Add(level);
                }
            }

            return efforts;
        }

        return null;
    }

    private static bool MentionsAuthentication(JsonElement error) =>
        Text(error, "message") is { } message &&
        (message.Contains("log in", StringComparison.OrdinalIgnoreCase) ||
         message.Contains("login", StringComparison.OrdinalIgnoreCase) ||
         message.Contains("authenticat", StringComparison.OrdinalIgnoreCase));

    private static string? Meta(JsonElement entry, string property) =>
        entry.TryGetProperty("_meta", out var meta) && meta.ValueKind == JsonValueKind.Object
            ? Text(meta, property)
            : null;

    private static Func<JsonElement, bool> Reply(int id) =>
        element => element.TryGetProperty("id", out var value) &&
                   value.ValueKind == JsonValueKind.Number &&
                   value.GetInt32() == id;

    private static string? Text(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.String &&
        value.GetString() is { Length: > 0 } text
            ? text
            : null;
}
