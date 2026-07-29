using Highbyte.Wrighty.Models;

namespace Highbyte.Wrighty.Workers;

/// <summary>
/// Why authoritative worker policy admitted or refused one work item. The reason is typed rather
/// than a bare boolean so the candidate scan, the launch preflight, and operator diagnostics all
/// describe the same refusal instead of each inventing wording.
/// </summary>
public enum WorkerPolicyReason
{
    Eligible,

    /// <summary>The item carries a dispatch state, so it belongs to the paused/queued path.</summary>
    PausedOrQueued,

    /// <summary>Authoritative Project policy does not allow unattended execution.</summary>
    ExecutionNotAutomatic,

    /// <summary>An operator <c>--filter</c> does not match.</summary>
    FilteredOut,

    /// <summary>No supported agent resolves from the item, options, or configuration.</summary>
    UnresolvedAgent
}

public sealed record WorkerPolicyDecision(
    WorkerPolicyReason Reason,
    string? Agent = null,
    string? AgentSource = null)
{
    public bool Eligible => Reason == WorkerPolicyReason.Eligible;

    public static WorkerPolicyDecision Ineligible(WorkerPolicyReason reason) => new(reason);
}

/// <summary>
/// The single authoritative worker-policy evaluation (plan 029). Both the pre-claim candidate scan
/// and the post-claim launch preflight call this, so an item can never be admitted by one path
/// under rules the other would refuse.
/// </summary>
public static class WorkerPolicyGate
{
    public static WorkerPolicyDecision Evaluate(
        WorkItemDetail detail,
        WorkerOptions options,
        string? configuredAgent,
        Func<string, bool> agentIsSupported)
    {
        if (detail.DispatchState is not null)
            return WorkerPolicyDecision.Ineligible(WorkerPolicyReason.PausedOrQueued);
        if (!detail.AutomaticExecutionAllowed)
            return WorkerPolicyDecision.Ineligible(WorkerPolicyReason.ExecutionNotAutomatic);
        if (!MatchesFilters(detail, options.Filters))
            return WorkerPolicyDecision.Ineligible(WorkerPolicyReason.FilteredOut);

        var resolved = ResolveAgent(options.Agent, detail.AgentPolicy, configuredAgent);
        return resolved.Agent is null || !agentIsSupported(resolved.Agent)
            ? WorkerPolicyDecision.Ineligible(WorkerPolicyReason.UnresolvedAgent)
            : new WorkerPolicyDecision(
                WorkerPolicyReason.Eligible,
                resolved.Agent,
                resolved.Source);
    }

    /// <summary>
    /// The operator-facing explanation for a refusal, without leaking item content. Returned as a
    /// lowercase sentence fragment so callers can compose it after their own lead-in.
    /// </summary>
    public static string Describe(WorkerPolicyReason reason) => reason switch
    {
        WorkerPolicyReason.Eligible => "authoritative worker policy allows this run",
        WorkerPolicyReason.PausedOrQueued =>
            "the item carries a dispatch state and is handled by the paused/queued path",
        WorkerPolicyReason.ExecutionNotAutomatic =>
            "authoritative Project policy no longer allows unattended execution",
        WorkerPolicyReason.FilteredOut => "an operator filter no longer matches the item",
        WorkerPolicyReason.UnresolvedAgent =>
            "no supported agent resolves from the item, options, or configuration",
        _ => "authoritative worker policy refused this run"
    };

    /// <summary>Whether every operator <c>--filter</c> still matches the item.</summary>
    public static bool MatchesFilters(
        WorkItemDetail detail,
        IReadOnlyDictionary<string, string> filters)
    {
        foreach (var filter in filters)
        {
            var actual = filter.Key.ToLowerInvariant() switch
            {
                "status" => detail.Status,
                "priority" => detail.Priority,
                "agent" => detail.AgentPolicy,
                "label" => detail.Labels?.FirstOrDefault(label =>
                    string.Equals(label, filter.Value, StringComparison.OrdinalIgnoreCase)),
                _ => detail.EffectiveFields.TryGetValue(filter.Key, out var value)
                    ? Scalar(value)
                    : null
            };
            if (!string.Equals(actual, filter.Value, StringComparison.OrdinalIgnoreCase))
                return false;
        }
        return true;
    }

    private static string? Scalar(System.Text.Json.JsonElement value) => value.ValueKind switch
    {
        System.Text.Json.JsonValueKind.String => value.GetString(),
        System.Text.Json.JsonValueKind.True => "true",
        System.Text.Json.JsonValueKind.False => "false",
        System.Text.Json.JsonValueKind.Number => value.GetRawText(),
        _ => null
    };

    private static (string? Agent, string? Source) ResolveAgent(
        string? option,
        string? item,
        string? configured)
    {
        if (Normalize(option) is { } optionAgent)
            return (optionAgent, "option");
        if (Normalize(item) is { } itemAgent)
            return (itemAgent, "item");
        if (Normalize(configured) is { } configuredAgent)
            return (configuredAgent, "config");
        return (null, null);
    }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToLowerInvariant();
}
