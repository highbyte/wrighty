using Highbyte.Wrighty.Workers;

namespace Highbyte.Wrighty.Actions;

/// <summary>A snapshot of advice, never a claim or executable authority.</summary>
public sealed record OperationalAction(
    string Name,
    string Title,
    string Description,
    string Availability,
    string? UnavailableCode,
    string? UnavailableReason,
    string Kind,
    string Confirmation,
    bool RequiresTty,
    bool StartsProcess,
    IReadOnlyList<string> Commands,
    string? Url = null,
    string? AgentPrompt = null,
    bool Recommended = false)
{
    // The foundation deliberately has no executors, including for otherwise available actions.
    public string Execution => "manual-only";

    public static OperationalAction FromGuidance(
        WorkerOperatorAction guidance,
        ActionAvailability availability,
        string kind = "wrighty-operation",
        string confirmation = "none",
        bool requiresTty = false,
        bool startsProcess = false) => new(
            guidance.Name ?? throw new ArgumentException("Action guidance needs a stable name."),
            guidance.Scenario, guidance.Description,
            availability.Code is null ? "available" : "unavailable",
            availability.Code, availability.Reason,
            kind, confirmation, requiresTty, startsProcess,
            guidance.Commands, guidance.Url, guidance.AgentPrompt);
}

public sealed record ActionAvailability(string? Code = null, string? Reason = null)
{
    public static ActionAvailability Available { get; } = new();
    public static ActionAvailability Unverified { get; } = new(
        "ACTION_STATE_UNVERIFIED", "The required local state could not be verified.");
}

public sealed record OperationalActionDiscovery(
    string ItemId,
    DateTimeOffset StateObservedAt,
    string? RecommendedAction,
    IReadOnlyList<OperationalAction> Actions);
