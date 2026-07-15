namespace Highbyte.Wrighty.Claims;

public sealed record ClaimRecord(
    int Version,
    string ClaimAttemptId,
    string WorkerIdentity,
    DateTimeOffset ClaimedAt,
    DateTimeOffset ExpiresAt,
    string State,
    string? AgentType = null,
    string? SessionId = null);

public sealed record ClaimEvent(
    long CommentId,
    DateTimeOffset CreatedAt,
    ClaimRecord Claim);

public enum ClaimOutcome
{
    Acquired,
    HeldByOther,
    AlreadyOwned
}

public sealed record ClaimResult(
    ClaimOutcome Outcome,
    string WorkerIdentity,
    DateTimeOffset ExpiresAt,
    string? ClaimAttemptId = null,
    string? AgentType = null,
    string? SessionId = null);

public enum ClaimOwnershipState
{
    OwnedByCurrent,
    HeldByOther,
    Unclaimed
}

public sealed record ClaimOwnershipResult(
    ClaimOwnershipState State,
    string? WorkerIdentity = null,
    DateTimeOffset? ExpiresAt = null);
