namespace Highbyte.Wrighty.Workers;

public enum FencedAction { Kill, Detach }

public enum WorkerItemIntent { Auto, Fresh, Resume }

public enum WorkerItemDisposition
{
    Finished,
    NeedsAttention,
    Failed,
    TimedOut,
    Rejected,
    RetryScheduled,
    Fenced
}

public sealed record WorkerRunSummary(
    int Processed,
    int NeedsAttention = 0,
    int Failed = 0)
{
    public int ExitCode => NeedsAttention > 0 ? 10 : Failed > 0 ? 7 : 0;
}

public sealed record WorkerOptions(
    string? Agent,
    bool Once,
    int? MaxItems,
    WorkspaceMode WorkspaceMode,
    IReadOnlyDictionary<string, string> Filters,
    TimeSpan? IdleTimeout,
    TimeSpan ItemTimeout,
    FencedAction OnFenced,
    string? ClaimantId,
    string ClaimantKind,
    bool DryRun,
    bool Json,
    string? FromStatus = null,
    string? ToStatus = null,
    bool KeepWorkspace = false);

public sealed record WorkerCandidateSummary(
    string Status,
    int StatusItems,
    int MissingAuto,
    int MissingItemAgent,
    int FilteredOut,
    int UnresolvedAgent,
    int Eligible,
    int Claimed = 0,
    int Claimable = 0,
    int ProviderUnavailable = 0);

public sealed record WorkerOperatorAction(
    string Scenario,
    IReadOnlyList<string> Commands,
    string Description,
    // A second, distinct snippet that is pasted into the opened agent session (not run in the
    // terminal). Rendered as its own code block after Commands so the two destinations are not
    // conflated, and so a work-item id inside it is never auto-linked as prose.
    string? AgentPrompt = null);

public sealed record WorkerEvent(
    string Type,
    string? ItemId = null,
    string? Agent = null,
    string? WorkspacePath = null,
    AgentOutcome? Outcome = null,
    string? Message = null,
    IReadOnlyList<string>? Arguments = null,
    string? SessionId = null,
    DateTimeOffset? ClaimExpiresAt = null,
    WorkerCandidateSummary? Candidates = null,
    string? ReviewCommand = null,
    IReadOnlyList<WorkerOperatorAction>? OperatorActions = null,
    DateTimeOffset? OccurredAt = null,
    TimeSpan? Elapsed = null,
    TimeSpan? TimeoutRemaining = null,
    DateTimeOffset? TimeoutAt = null,
    string? WorkspaceMode = null,
    string? Branch = null,
    AgentFailure? Failure = null,
    WorkerDispatchInfo? Dispatch = null,
    ProviderAvailability? ProviderAvailability = null);

public enum WorkerEventSemantic
{
    Success,
    Info,
    Warning,
    Danger,
    Muted
}

public static class WorkerEventClassifier
{
    public static WorkerEventSemantic? Classify(string eventType) => eventType switch
    {
        "check" or "finished" or "workspace-removed" => WorkerEventSemantic.Success,
        "info" or "ready" or "started" or "resumed" or "session" or "dry-run" or
            "retry-due" or "retry-started" or "provider-available" =>
            WorkerEventSemantic.Info,
        "needs-attention" or "workspace-busy" or "skipped-claimed" or "retry-scheduled" or
            "provider-unavailable" =>
            WorkerEventSemantic.Warning,
        "retry-interrupted" => WorkerEventSemantic.Warning,
        "failed" or "fenced" or "timed-out" or "rejected" => WorkerEventSemantic.Danger,
        "idle" or "no-item" or "running" or "renewed" or "waiting" =>
            WorkerEventSemantic.Muted,
        _ => null
    };
}
