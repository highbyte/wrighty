using Highbyte.Wrighty.Claims;
using Highbyte.Wrighty.Models;
using Highbyte.Wrighty.Workers;
using Microsoft.AspNetCore.Html;

namespace Highbyte.Wrighty.Web;

public sealed record BoardPageModel(
    IReadOnlyList<string> Statuses,
    IReadOnlyList<string> Priorities,
    IReadOnlyList<BoardColumnModel> Columns,
    IReadOnlyList<BoardCardModel> Archived,
    string Scope,
    string Revision,
    string? ErrorCode = null,
    string? ErrorMessage = null,
    IReadOnlyList<ProviderAvailabilityView>? ProviderCircuits = null)
{
    public IReadOnlyList<ProviderAvailabilityView> EffectiveProviderCircuits =>
        ProviderCircuits ?? [];
}

public sealed record BoardColumnModel(string Name, IReadOnlyList<BoardCardModel> Cards);

public sealed record BoardCardModel(
    string Id,
    string DisplayId,
    string Title,
    string? Status,
    string? Priority,
    bool Archived,
    ClaimOwnershipState ClaimState,
    string ClaimLabel,
    string? ClaimantKindLabel,
    string? AgentTypeLabel,
    bool AutomationEligible,
    string? PreferredAgent,
    string? WorkerState,
    string Activity,
    bool HasRecordedWorktree = false,
    ProviderAvailabilityView? ProviderBlock = null);

public sealed record ItemPageModel(
    string Id,
    string DisplayId,
    string Title,
    string Body,
    string? Status,
    string? Priority,
    bool Archived,
    string Revision,
    ClaimOwnershipState ClaimState,
    string ClaimLabel,
    string? ClaimantKindLabel,
    string? AgentTypeLabel,
    bool WebMutationProtected,
    string? WebMutationProtectionMessage,
    bool TakeoverAvailable,
    string? ClaimantId,
    string? ClaimGeneration,
    bool HasResumeAddress,
    bool CanQueueForWorker,
    string? ResumeCommand,
    string? WorkerResumeCommand,
    string? ResumePrompt,
    string? ResumeAgentLabel,
    bool AutomationEligible,
    string? PreferredAgent,
    string? WorkerState,
    string Activity,
    IReadOnlyList<string> Statuses,
    IReadOnlyList<string> Priorities,
    IHtmlContent RenderedBody,
    string? Notice = null,
    string? ErrorCode = null,
    string? ErrorMessage = null,
    bool Editing = false,
    IReadOnlyDictionary<string, string>? Fields = null,
    string? RawFrontmatter = null,
    WorkspaceView? Workspace = null,
    LastRunView? LastRun = null,
    WorkerDispatchInfo? Dispatch = null,
    ProviderAvailabilityView? ProviderBlock = null)
{
    public IReadOnlyDictionary<string, string> EffectiveFields =>
        Fields ?? EmptyFields;

    private static readonly IReadOnlyDictionary<string, string> EmptyFields =
        new Dictionary<string, string>();
}

/// <summary>
/// Sanitized installation-local provider capacity projected into the Local Markdown dashboard.
/// This deliberately contains neither raw provider responses nor account identity.
/// </summary>
public sealed record ProviderAvailabilityView(
    string AgentType,
    string AgentLabel,
    ProviderAvailabilityState State,
    string? Reason,
    DateTimeOffset? Until,
    AgentFailureConfidence Confidence,
    int ConsecutiveFailures)
{
    public bool ProbeInProgress => State == ProviderAvailabilityState.ProbeDue;
    public bool HasCapacityFailure => ConsecutiveFailures > 0;

    public static ProviderAvailabilityView Available(string agentType) => new(
        agentType,
        Label(agentType),
        ProviderAvailabilityState.Available,
        null,
        null,
        AgentFailureConfidence.Authoritative,
        0);

    public static ProviderAvailabilityView From(ProviderAvailability availability) => new(
        availability.AgentType,
        Label(availability.AgentType),
        availability.State,
        availability.Reason,
        availability.UnavailableUntil,
        availability.Confidence,
        availability.ConsecutiveFailures);

    private static string Label(string agentType) =>
        string.IsNullOrWhiteSpace(agentType)
            ? "Provider"
            : char.ToUpperInvariant(agentType[0]) + agentType[1..];
}

public sealed record ProviderCapacityPageModel(
    IReadOnlyList<ProviderAvailabilityView> Providers,
    string Revision,
    string? Notice = null,
    string? ErrorCode = null,
    string? ErrorMessage = null);

/// <summary>
/// The captured outcome of the most recent agent run, surfaced in the item panel's "Last run"
/// block so an operator can read the block reason and clarify/requeue without opening the vendor
/// session first.
/// </summary>
public sealed record LastRunView(
    RunOutcome Outcome,
    string Label,
    DateTimeOffset? EndedAt,
    string? FinalMessage,
    AgentFailure? Failure)
{
    public static LastRunView? From(AgentSessionRecord? session) =>
        session is { Outcome: { } outcome }
            ? new LastRunView(
                outcome,
                outcome switch
                {
                    RunOutcome.Succeeded => "succeeded",
                    RunOutcome.Failed => "failed",
                    RunOutcome.Rejected => "rejected",
                    _ => outcome.ToString().ToLowerInvariant()
                },
                session.EndedAt,
                session.FinalMessage,
                session.Failure)
            : null;
}

/// <summary>
/// The durable worker worktree recorded for an item, with its git-calculated state when it could
/// be read on this host. <see cref="StatusAvailable"/> is false when the worktree is absent here
/// or git could not be read, in which case <see cref="Unavailable"/> carries a display message.
/// </summary>
public sealed record WorkspaceView(
    string Path,
    string? Branch,
    bool StatusAvailable,
    bool Dirty,
    bool Merged,
    string? Unavailable,
    bool Removed,
    IReadOnlyList<WorkerOperatorAction> CompletionActions);

public sealed record ConflictPageModel(
    ItemPageModel Current,
    string SubmittedTitle,
    string SubmittedBody,
    string SubmittedStatus,
    string? SubmittedPriority,
    bool SubmittedAutomationEligible,
    string? SubmittedPreferredAgent);

public sealed record WebErrorModel(string Code, string Message);

public sealed record CreateItemPageModel(
    string Title,
    string Body,
    string Status,
    string? Priority,
    bool AutomationEligible,
    string? PreferredAgent,
    string CreationAttemptId,
    IReadOnlyList<string> Statuses,
    IReadOnlyList<string> Priorities,
    string? ErrorCode = null,
    string? ErrorMessage = null);
