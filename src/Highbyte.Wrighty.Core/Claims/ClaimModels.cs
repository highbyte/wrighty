using Highbyte.Wrighty.AgentContext;
using Highbyte.Wrighty.Workers;

namespace Highbyte.Wrighty.Claims;

[method: System.Text.Json.Serialization.JsonConstructor]
public sealed record ClaimRecord(
    int Version,
    string EventId,
    string InstallationId,
    DateTimeOffset ClaimedAt,
    DateTimeOffset ExpiresAt,
    string EventType,
    string ClaimantId,
    string ClaimToken,
    string? PreviousClaimToken = null,
    string? Agent = null,
    string? SessionId = null,
    string ClaimantKind = "unknown",
    string? WorkspacePath = null);

public sealed record ClaimEvent(
    long CommentId,
    DateTimeOffset CreatedAt,
    ClaimRecord Claim);

public enum ClaimOutcome
{
    Acquired,
    HeldByOther,
    HeldByLocalClaimant,
    AlreadyOwned,
    TakenOver
}

public sealed record ClaimResult(
    ClaimOutcome Outcome,
    string InstallationId,
    DateTimeOffset ExpiresAt,
    string? EventId = null,
    string? Agent = null,
    string? SessionId = null,
    string ClaimantKind = "unknown",
    string? ClaimantId = null,
    string? ClaimToken = null,
    bool TakeoverAvailable = false,
    string? WorkspacePath = null);

public enum ClaimOwnershipState
{
    OwnedByCurrent,
    HeldByOther,
    Unclaimed
}

public sealed record ClaimOwnershipResult(
    ClaimOwnershipState State,
    string? InstallationId = null,
    DateTimeOffset? ExpiresAt = null,
    string? ClaimantId = null,
    string? Agent = null,
    string? SessionId = null,
    string ClaimantKind = "unknown",
    bool TakeoverAvailable = false,
    string? WorkspacePath = null);

/// <summary>
/// The durable outcome of the most recent agent run recorded for a work item. Captured when the
/// worker emits its terminal event (finished / needs-attention / failed) so the "what happened"
/// signal survives the worker terminal. Backend-neutral: both the sidecar and the GitHub session
/// cache carry it.
/// </summary>
[System.Text.Json.Serialization.JsonConverter(
    typeof(System.Text.Json.Serialization.JsonStringEnumConverter<RunOutcome>))]
public enum RunOutcome
{
    Succeeded,
    Failed,
    Rejected
}

public sealed record SessionAddress(
    string? Agent,
    string? Id,
    string? WorkspacePath,
    string? Branch = null)
{
    public bool IsPresent =>
        !string.IsNullOrWhiteSpace(Agent) ||
        !string.IsNullOrWhiteSpace(Id) ||
        !string.IsNullOrWhiteSpace(WorkspacePath);

    public bool IsComplete =>
        !string.IsNullOrWhiteSpace(Agent) &&
        !string.IsNullOrWhiteSpace(Id) &&
        !string.IsNullOrWhiteSpace(WorkspacePath);
}

public sealed record LastRunRecord(
    RunOutcome Outcome,
    DateTimeOffset EndedAt,
    string? FinalMessage = null,
    AgentFailure? Failure = null);

public sealed record AgentSessionRecord(
    string? Agent,
    string? SessionId,
    string? WorkspacePath,
    DateTimeOffset ClaimExpiresAt,
    bool FromCurrentInstallation,
    string? Branch = null,
    RunOutcome? Outcome = null,
    string? FinalMessage = null,
    DateTimeOffset? EndedAt = null,
    AgentFailure? Failure = null,
    DispatchInfo? Dispatch = null,
    // Additive and optional: a session written before approved-context support simply has none,
    // which blocks unattended resume across a changed revision rather than guessing what that
    // agent was given. See SessionContextMetadata.
    ApprovedContext.SessionContextMetadata? Context = null,
    // The last run's structured report, stored whether or not it was ever published.
    ApprovedContext.AgentRunReport? LastReport = null,
    // What this session has spent on automatic continuation. Additive and optional: a session
    // written before continuation support reads as unspent, which is the correct starting point.
    // Kept beside Context rather than inside it so the reset rules stay explicit — see
    // SessionContinuationState.
    ApprovedContext.SessionContinuationState? Continuation = null,
    // What the fresh launch actually asked the vendor for. Machine-local only: never a GitHub
    // label, comment, Project field, work-item front matter, URL, or transcript, because it
    // describes this installation's mapping rather than anything the repository agreed to.
    //
    // Recorded so a resumed run can be shown to have kept its original selection. A vendor-native
    // session stays on the model it started with regardless of what the mapping says now, and
    // without this record there would be no way to tell that from a mapping that never applied.
    Workers.ExecutionSelection? Selection = null)
{
    public bool HasAddress =>
        !string.IsNullOrWhiteSpace(Agent) ||
        !string.IsNullOrWhiteSpace(SessionId) ||
        !string.IsNullOrWhiteSpace(WorkspacePath);

    public bool IsComplete =>
        !string.IsNullOrWhiteSpace(Agent) &&
        !string.IsNullOrWhiteSpace(SessionId) &&
        !string.IsNullOrWhiteSpace(WorkspacePath);

    /// <summary>
    /// True when a dedicated worker worktree is recorded for the item. Keyed on the recorded
    /// branch, which only worktree mode records — a current/shared-mode session records the main
    /// checkout as its workspace but no branch, and is not a retained worktree. Derived purely from
    /// the session address (no git shell-out), so it is cheap enough for the list/board at-a-glance
    /// badge; the per-item dirty/merged detail stays on the single-item surfaces (get, item viewer,
    /// workspaces).
    /// </summary>
    public bool HasRecordedWorktree => !string.IsNullOrWhiteSpace(Branch);

    /// <summary>
    /// True when the last recorded run finished the item (the agent called finish and the run
    /// succeeded), as opposed to a session merely retained for later resumption. Combined with the
    /// item status by <see cref="OperationalStatuses"/> to tell a completed item from a paused one.
    /// </summary>
    public bool HasRunOutcome => Outcome is not null;

    /// <summary>
    /// The consumed automatic trigger awaiting a resulting run. A report already correlated to
    /// the same key wins over a failed continuation-state refresh, preventing a later manual resume
    /// from replaying an old operator control merely because best-effort bookkeeping failed.
    /// </summary>
    public ApprovedContext.TrustedContinuationEvent? PendingContinuationTrigger
    {
        get
        {
            var pending = Continuation?.PendingTrigger;
            return pending is not null && !string.Equals(
                pending.ConsumptionKey,
                LastReport?.Trigger?.ConsumptionKey,
                StringComparison.Ordinal)
                ? pending
                : null;
        }
    }
}

public sealed record ClaimHandle(
    AgentExecutionContext Claimant,
    string? ClaimToken)
{
    public string ClaimantId => Claimant.ClaimantId ?? string.Empty;
}
