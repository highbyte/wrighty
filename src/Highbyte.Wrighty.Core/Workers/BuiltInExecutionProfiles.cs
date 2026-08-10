using Highbyte.Wrighty.Settings;

namespace Highbyte.Wrighty.Workers;

/// <summary>
/// The profile mappings Wrighty ships, so profiles work with no setup at all.
///
/// They carry an effort level and **no model**, which is the whole design. Model identifiers are
/// vendor product names that retire on the vendor's schedule — <c>gpt-5.6-luna</c> names one family
/// generation, and <c>haiku</c> is not among the aliases Claude Code's own help documents. Shipping
/// a model catalogue would mean shipping something that breaks without Wrighty changing, and for
/// codex a stale model is not even caught locally: the session starts and fails at the API, having
/// already spent a request.
///
/// Effort levels do not have that problem. A capability query on 2026-08-08 confirmed that every
/// codex model offers <c>low</c>, <c>medium</c> and <c>high</c> — from <c>gpt-5.3-codex-spark</c>
/// through the GPT-5.6 family — and claude and copilot document the same three. They are the one
/// axis that is both meaningful and stable enough to ship an opinion about.
///
/// The consequence an operator must understand: these tiers change how hard the agent thinks, not
/// which model runs. <c>economy</c> on a machine whose vendor default is a flagship still runs the
/// flagship, just with less reasoning. Naming a cheaper model is a deliberate local override,
/// because only the operator knows what their account is entitled to.
/// </summary>
public static class BuiltInExecutionProfiles
{
    public const string Economy = "economy";
    public const string Balanced = "balanced";
    public const string Deep = "deep";

    private static readonly Dictionary<string, ExecutionEffort> Efforts =
        new(StringComparer.OrdinalIgnoreCase)
        {
            [Economy] = ExecutionEffort.Low,
            [Balanced] = ExecutionEffort.Medium,
            [Deep] = ExecutionEffort.High
        };

    /// <summary>
    /// The vocabulary a repository gets when it configures none of its own. Ordered from least to
    /// most effort so listings read as a ladder.
    /// </summary>
    public static IReadOnlyList<string> Names { get; } = [Economy, Balanced, Deep];

    public static bool IsBuiltIn(string? profile) =>
        profile is not null && Efforts.ContainsKey(profile);

    /// <summary>
    /// The shipped mapping for a profile, or null when the name is not one Wrighty ships. The agent
    /// is taken into account only to refuse a level the vendor could not accept — no such case
    /// exists today, and asserting it here means a future vendor that drops <c>medium</c> surfaces
    /// as an honest "no mapping" rather than a launch failure.
    /// </summary>
    public static ExecutionProfileMapping? Find(string profile, AgentExecutionCapability capability) =>
        Efforts.TryGetValue(profile, out var effort) && capability.Supports(effort)
            ? new ExecutionProfileMapping { Effort = effort }
            : null;
}
