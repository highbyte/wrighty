namespace Highbyte.Wrighty.Workers;

/// <summary>
/// Wrighty's closed vocabulary for reasoning effort: the union of what the supported vendors
/// accept. It is a closed enumeration rather than an opaque string because the value reaches a
/// vendor command line, and configured text must never be interpolated into an argument — the
/// adapter builds the whole token itself from a value that could only have come from this type.
///
/// The union is not uniform, and vendor-level is not the real granularity: a codex capability query
/// on 2026-08-08 showed effort support varies **per model** — <c>gpt-5.6-sol</c> accepts
/// <c>ultra</c> while <c>gpt-5.4</c> stops at <c>xhigh</c>. Wrighty cannot enumerate models yet, so
/// <see cref="AgentExecutionCapability.SupportedEfforts"/> is a permissive gate rather than a
/// guarantee: it catches values the vendor could never accept, and lets the vendor reject the rest.
/// </summary>
public enum ExecutionEffort
{
    None,
    Minimal,
    Low,
    Medium,
    High,
    XHigh,
    Max,
    Ultra
}

public static class ExecutionEfforts
{
    /// <summary>
    /// The exact token each level is written as on a vendor command line. Spelled out rather than
    /// lower-casing the enum name so that <c>XHigh</c> cannot silently become <c>xhigh</c> in one
    /// place and <c>x-high</c> in another, and so a future vendor spelling is a one-line change
    /// here instead of a search across adapters.
    /// </summary>
    private static readonly IReadOnlyDictionary<ExecutionEffort, string> Tokens =
        new Dictionary<ExecutionEffort, string>
        {
            [ExecutionEffort.None] = "none",
            [ExecutionEffort.Minimal] = "minimal",
            [ExecutionEffort.Low] = "low",
            [ExecutionEffort.Medium] = "medium",
            [ExecutionEffort.High] = "high",
            [ExecutionEffort.XHigh] = "xhigh",
            [ExecutionEffort.Max] = "max",
            [ExecutionEffort.Ultra] = "ultra"
        };

    public static string ToToken(this ExecutionEffort effort) => Tokens[effort];

    /// <summary>
    /// Parses a stored or operator-supplied level. Case-insensitive because settings files are
    /// hand-editable, but otherwise exact: no prefix matching, no nearest-neighbour guessing.
    /// </summary>
    public static bool TryParse(string? value, out ExecutionEffort effort)
    {
        foreach (var (level, token) in Tokens)
        {
            if (string.Equals(value, token, StringComparison.OrdinalIgnoreCase))
            {
                effort = level;
                return true;
            }
        }

        effort = default;
        return false;
    }

    /// <summary>Every level Wrighty knows, in ascending order, for error messages and help text.</summary>
    public static IReadOnlyList<string> All { get; } = Tokens
        .OrderBy(entry => entry.Key)
        .Select(entry => entry.Value)
        .ToArray();
}

/// <summary>
/// What one vendor's CLI can be told about model and reasoning effort on a fresh launch.
///
/// This is a declaration of the vendor's argument surface, not of the operator's entitlement:
/// Wrighty can know that <c>claude</c> accepts <c>--model</c>, but never that this account may use
/// a particular model. Entitlement failures surface from the vendor at launch.
/// </summary>
/// <param name="Agent">Normalized agent name.</param>
/// <param name="SupportsModel">Whether a fresh launch can carry an explicit model selector.</param>
/// <param name="SupportedEfforts">
/// The effort levels Wrighty will pass to this vendor without further checking. Deliberately a
/// permissive gate, not a guarantee: real support is a property of the chosen *model*, which
/// Wrighty cannot enumerate yet. A level outside this set could never work and is rejected early;
/// a level inside it may still be refused by the vendor for the specific model. Empty means the
/// vendor exposes no effort control at all, and configuring one is invalid rather than ignored.
/// </param>
public sealed record AgentExecutionCapability(
    string Agent,
    bool SupportsModel,
    IReadOnlySet<ExecutionEffort> SupportedEfforts)
{
    public bool SupportsEffort => SupportedEfforts.Count > 0;

    public bool Supports(ExecutionEffort effort) => SupportedEfforts.Contains(effort);
}

/// <summary>
/// Resolves an agent name to its capability without needing an adapter instance in hand, for
/// callers such as the config commands that validate a mapping long before any launch. Mirrors
/// <see cref="AgentSessionExporters.ForAgent"/>; the adapters remain the single source of truth.
/// </summary>
public static class AgentExecutionCapabilities
{
    public static AgentExecutionCapability? ForAgent(string? agent) =>
        agent?.Trim().ToLowerInvariant() switch
        {
            "claude" => new ClaudeAgentAdapter().DescribeExecutionCapability(),
            "codex" => new CodexAgentAdapter().DescribeExecutionCapability(),
            "copilot" => new CopilotAgentAdapter().DescribeExecutionCapability(),
            _ => null
        };
}

/// <summary>
/// Builds the command-line fragment that carries a resolved selection.
///
/// The configured model reaches the process as its own argv element, never spliced into another
/// argument, so it cannot introduce a second option however it is spelled. The effort never
/// contributes configured text at all — only a token derived from <see cref="ExecutionEffort"/>.
/// </summary>
public static class ExecutionSelectionArguments
{
    /// <summary>Vendors that expose model and effort as ordinary flags: claude and copilot.</summary>
    public static IReadOnlyList<string> ForFlags(
        ExecutionSelection? selection, string effortFlag = "--effort")
    {
        if (selection is null)
        {
            return [];
        }

        var arguments = new List<string>(4);
        if (selection.Model is { } model)
        {
            arguments.Add("--model");
            arguments.Add(model);
        }

        if (selection.Effort is { } effort)
        {
            arguments.Add(effortFlag);
            arguments.Add(effort.ToToken());
        }

        return arguments;
    }

    /// <summary>
    /// Codex, whose effort has no flag and rides the general <c>-c key=value</c> config channel.
    /// The whole token is assembled here from a literal key and an enum-derived value, so settings
    /// text can never become a config override of the operator's choosing — that channel would
    /// otherwise reach sandbox and approval settings, not just the model.
    /// </summary>
    public static IReadOnlyList<string> ForCodex(ExecutionSelection? selection)
    {
        if (selection is null)
        {
            return [];
        }

        var arguments = new List<string>(4);
        if (selection.Model is { } model)
        {
            arguments.Add("--model");
            arguments.Add(model);
        }

        if (selection.Effort is { } effort)
        {
            arguments.Add("-c");
            arguments.Add($"model_reasoning_effort={effort.ToToken()}");
        }

        return arguments;
    }
}
