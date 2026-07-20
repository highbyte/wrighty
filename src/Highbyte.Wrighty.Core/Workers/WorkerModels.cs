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
    int Claimable = 0);

public sealed record WorkerOperatorAction(
    string Scenario,
    IReadOnlyList<string> Commands,
    string Description);

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
    string? Branch = null);

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
        "info" or "ready" or "started" or "resumed" or "session" or "dry-run" =>
            WorkerEventSemantic.Info,
        "needs-attention" or "workspace-busy" or "skipped-claimed" =>
            WorkerEventSemantic.Warning,
        "failed" or "fenced" or "timed-out" or "rejected" => WorkerEventSemantic.Danger,
        "idle" or "no-item" or "running" or "renewed" or "waiting" =>
            WorkerEventSemantic.Muted,
        _ => null
    };
}
