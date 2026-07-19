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

public sealed record AgentSessionRecord(
    string? AgentType,
    string? SessionId,
    string? WorkspacePath,
    DateTimeOffset ClaimExpiresAt,
    bool FromCurrentInstallation)
{
    public bool HasAddress =>
        !string.IsNullOrWhiteSpace(AgentType) ||
        !string.IsNullOrWhiteSpace(SessionId) ||
        !string.IsNullOrWhiteSpace(WorkspacePath);

    public bool IsComplete =>
        !string.IsNullOrWhiteSpace(AgentType) &&
        !string.IsNullOrWhiteSpace(SessionId) &&
        !string.IsNullOrWhiteSpace(WorkspacePath);
}

public sealed record ClaimHandle(
    AgentExecutionContext Claimant,
    string? ClaimToken)
{
    public string ClaimantId => Claimant.ClaimantId ?? string.Empty;
}
