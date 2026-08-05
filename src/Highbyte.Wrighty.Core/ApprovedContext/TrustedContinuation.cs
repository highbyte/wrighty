using System.Text.Json.Serialization;
using Highbyte.Wrighty.Configuration;

namespace Highbyte.Wrighty.ApprovedContext;

[JsonConverter(typeof(JsonStringEnumConverter<TrustedContinuationSource>))]
public enum TrustedContinuationSource
{
    [JsonStringEnumMemberName("comment")]
    Comment,

    [JsonStringEnumMemberName("reaction")]
    Reaction
}

[JsonConverter(typeof(JsonStringEnumConverter<TrustedContinuationKind>))]
public enum TrustedContinuationKind
{
    [JsonStringEnumMemberName("continue")]
    Continue,

    [JsonStringEnumMemberName("completion-requested")]
    CompletionRequested
}

/// <summary>
/// One trusted operator event that may continue a needs-attention session.
///
/// Comment events are revision-addressed because an edit is new input. Reaction events are
/// identity-addressed because GitHub gives every reaction its own immutable stable ID. The record
/// deliberately contains no comment body or other task content: it is durable operational
/// provenance and is safe to show in reports and handovers.
/// </summary>
[method: JsonConstructor]
public sealed record TrustedContinuationEvent(
    string StableId,
    TrustedContinuationSource Source,
    string Actor,
    DateTimeOffset CreatedAt,
    DateTimeOffset? RevisionAt = null,
    TrustedContinuationKind Kind = TrustedContinuationKind.Continue,
    DateTimeOffset? ConsumedAt = null,
    string? TriggeredRunId = null)
{
    public TrustedContinuationEvent(string commentId, string actor, DateTimeOffset revisionAt)
        : this(commentId, TrustedContinuationSource.Comment, actor, revisionAt, revisionAt)
    {
    }

    public string ConsumptionKey => Source switch
    {
        TrustedContinuationSource.Reaction => $"reaction:{StableId}",
        _ => $"comment:{StableId}@{(RevisionAt ?? CreatedAt).ToUniversalTime():O}"
    };

    /// <summary>Compatibility name for callers that are specifically handling comment events.</summary>
    [JsonIgnore]
    public string CommentId => StableId;

    public DateTimeOffset OccurredAt => RevisionAt ?? CreatedAt;

    public string TriggerMode => Source switch
    {
        TrustedContinuationSource.Comment => "trusted-comment",
        _ when Kind == TrustedContinuationKind.CompletionRequested => "completion-reaction",
        _ => "resume-reaction"
    };

    public TrustedContinuationEvent Consumed(DateTimeOffset at) => this with { ConsumedAt = at };

    public TrustedContinuationEvent Triggered(string runId) => this with { TriggeredRunId = runId };

    public string Describe() => Source switch
    {
        TrustedContinuationSource.Comment => $"trusted comment by @{Actor}",
        _ when Kind == TrustedContinuationKind.CompletionRequested =>
            $"completion reaction by @{Actor}",
        _ => $"resume reaction by @{Actor}"
    };
}

/// <summary>
/// A content-free reading of the latest unresolved strict Wrighty status comment and its one
/// effective control reaction. The comment identity is cached in continuation state so later polls
/// can read this one comment rather than page the whole issue discussion.
/// </summary>
public sealed record TrustedControlReactionReading(
    string ReportId,
    string ReportCommentId,
    DateTimeOffset ReportRevisionAt,
    TrustedContinuationEvent? Event = null,
    string? Reason = null);

/// <summary>Backend capability for control reactions on the current Wrighty status comment.</summary>
public interface ITrustedControlReactionProvider
{
    Task<TrustedControlReactionReading?> ReadAsync(
        Configuration.TrackerConfig config,
        Models.WorkItemId id,
        AgentRunReport latestReport,
        SessionContinuationState state,
        WorkerContinuationConfig continuation,
        CancellationToken cancellationToken);
}

/// <summary>
/// The spend controls around automatic continuation. Reaching the budget never auto-finishes,
/// archives, starts a fresh session, or resets the counter — the item stays in needs-attention
/// until an operator acts.
/// </summary>
public sealed record TrustedContinuationBudget(
    int MaxAutomaticContinuations = TrustedContinuationBudget.DefaultMaxAutomaticContinuations,
    int Used = 0,
    DateTimeOffset? LastQueuedAt = null,
    TimeSpan? Cooldown = null,
    TimeSpan? Debounce = null)
{
    public const int DefaultMaxAutomaticContinuations = 10;
    public static readonly TimeSpan DefaultCooldown = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan DefaultDebounce = TimeSpan.FromSeconds(10);

    public int Remaining => Math.Max(0, MaxAutomaticContinuations - Used);

    public bool IsExhausted => Remaining == 0;

    public TimeSpan EffectiveCooldown => Cooldown ?? DefaultCooldown;

    public TimeSpan EffectiveDebounce => Debounce ?? DefaultDebounce;

    public bool CanQueueAt(DateTimeOffset now)
    {
        if (IsExhausted) return false;
        if (LastQueuedAt is not { } last) return true;
        return now - last >= EffectiveCooldown;
    }

    public bool HasSettledAt(DateTimeOffset revisionAt, DateTimeOffset now) =>
        now - revisionAt >= EffectiveDebounce;
}
