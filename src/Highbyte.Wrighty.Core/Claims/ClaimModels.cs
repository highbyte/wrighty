using Highbyte.Wrighty.AgentContext;

namespace Highbyte.Wrighty.Claims;

[method: System.Text.Json.Serialization.JsonConstructor]
public sealed record ClaimRecord(
    int Version,
    string EventId,
    string WorkerIdentity,
    DateTimeOffset ClaimedAt,
    DateTimeOffset ExpiresAt,
    string EventType,
    string ClaimantId,
    string ClaimToken,
    string? PreviousClaimToken = null,
    string? AgentType = null,
    string? SessionId = null,
    string ClaimantKind = "unknown",
    string? WorkspacePath = null)
{
    public string ClaimAttemptId => EventId;
    public string State => EventType switch
    {
        "released" or "overrideReleased" => "released",
        "requeued" => "queued",
        _ => "active"
    };
}

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
    string WorkerIdentity,
    DateTimeOffset ExpiresAt,
    string? ClaimAttemptId = null,
    string? AgentType = null,
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
    string? WorkerIdentity = null,
    DateTimeOffset? ExpiresAt = null,
    string? ClaimantId = null,
    string? AgentType = null,
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

public sealed record AgentSessionRecord(
    string? AgentType,
    string? SessionId,
    string? WorkspacePath,
    DateTimeOffset ClaimExpiresAt,
    bool FromCurrentInstallation,
    string? Branch = null,
    RunOutcome? Outcome = null,
    string? FinalMessage = null,
    DateTimeOffset? EndedAt = null)
{
    public bool HasAddress =>
        !string.IsNullOrWhiteSpace(AgentType) ||
        !string.IsNullOrWhiteSpace(SessionId) ||
        !string.IsNullOrWhiteSpace(WorkspacePath);

    public bool IsComplete =>
        !string.IsNullOrWhiteSpace(AgentType) &&
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
    /// item status by <see cref="WorkItemActivities"/> to tell a completed item from a paused one.
    /// </summary>
    public bool HasRunOutcome => Outcome is not null;
}

public sealed record ClaimHandle(
    AgentExecutionContext Claimant,
    string? ClaimToken)
{
    public string ClaimantId => Claimant.ClaimantId ?? string.Empty;
}
