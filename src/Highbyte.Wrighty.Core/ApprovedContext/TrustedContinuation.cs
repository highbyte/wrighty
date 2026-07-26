using System.Text.Json.Serialization;

namespace Highbyte.Wrighty.ApprovedContext;

/// <summary>What produced a continuation candidate.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<TrustedContinuationSource>))]
public enum TrustedContinuationSource
{
    [JsonStringEnumMemberName("comment")]
    Comment,

    [JsonStringEnumMemberName("reaction")]
    Reaction
}

/// <summary>What a trusted actor asked for.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<TrustedContinuationKind>))]
public enum TrustedContinuationKind
{
    /// <summary>Continue the retained session with the supplied response.</summary>
    [JsonStringEnumMemberName("continue")]
    Continue,

    /// <summary>
    /// Ask the resumed agent to finish. It never finishes the item directly: the agent must still
    /// satisfy the ordinary claim and completion checks and call finish itself.
    /// </summary>
    [JsonStringEnumMemberName("completion-requested")]
    CompletionRequested
}

/// <summary>
/// One candidate trigger for continuing a needs-attention session (plan 030 decision 19).
///
/// Recording the revision alongside the identity is what makes consumption idempotent: a comment
/// edited after it queued a run is a different revision and must be evaluated again, while the same
/// revision seen on a later poll must not spend a second agent turn.
/// </summary>
public sealed record TrustedContinuationEvent(
    string StableId,
    TrustedContinuationSource Source,
    string Actor,
    DateTimeOffset CreatedAt,
    TrustedContinuationKind Kind,
    string? Revision = null,
    DateTimeOffset? ConsumedAt = null,
    string? TriggeredRunId = null)
{
    /// <summary>
    /// The durable deduplication key: the stable ID plus the current revision for a comment, or the
    /// bare reaction ID. Deliberately not the issue's <c>updatedAt</c>, comment ordering, or worker
    /// host time — none of those identify a specific consumable event.
    /// </summary>
    public string ConsumptionKey => Source == TrustedContinuationSource.Reaction
        ? $"reaction:{StableId}"
        : $"comment:{StableId}@{Revision ?? "unknown"}";

    public bool IsConsumed => ConsumedAt is not null;
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

    /// <summary>
    /// Whether another automatic continuation may be queued at <paramref name="now"/>. The cooldown
    /// is measured from the last queue rather than the last run end, so a rapid series of replies
    /// cannot bypass it by arriving while a run is still finishing.
    /// </summary>
    public bool CanQueueAt(DateTimeOffset now)
    {
        if (IsExhausted) return false;
        if (LastQueuedAt is not { } last) return true;
        return now - last >= (Cooldown ?? DefaultCooldown);
    }
}
