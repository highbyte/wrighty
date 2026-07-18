namespace Highbyte.Wrighty.Workers;

public enum FencedAction { Kill, Detach }

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
    IReadOnlyList<string>? RecommendedCommands = null,
    WorkerCandidateSummary? Candidates = null,
    string? ReviewCommand = null);
