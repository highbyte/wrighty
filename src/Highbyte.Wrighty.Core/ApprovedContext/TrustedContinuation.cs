namespace Highbyte.Wrighty.ApprovedContext;

/// <summary>
/// One candidate trigger for continuing a needs-attention session: a trusted author's comment
/// (plan 030 decision 19, comment half).
///
/// Recording the revision alongside the identity is what makes consumption idempotent. A comment
/// edited after it queued a run is a different revision and must be evaluated again, while the same
/// revision seen on a later poll must not spend a second agent turn.
///
/// Deliberately carries no trigger-source discriminator. The control-reaction trigger is a separate
/// slice with no producer here yet, and a source enum with one member is the shape amendment 4
/// removed once already: adding <c>reaction:</c> keys later does not disturb the comment keys
/// written now.
/// </summary>
public sealed record TrustedContinuationEvent(
    string CommentId,
    string Actor,
    DateTimeOffset RevisionAt)
{
    /// <summary>
    /// The durable deduplication key: the comment's stable ID plus the revision that was acted on.
    /// Deliberately not the issue's <c>updatedAt</c>, comment ordering, or worker host time — none
    /// of those identify a specific consumable event.
    /// </summary>
    public string ConsumptionKey => $"comment:{CommentId}@{RevisionAt.ToUniversalTime():O}";
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

    /// <summary>
    /// Whether another automatic continuation may be queued at <paramref name="now"/>. The cooldown
    /// is measured from the last queue rather than the last run end, so a rapid series of replies
    /// cannot bypass it by arriving while a run is still finishing.
    /// </summary>
    public bool CanQueueAt(DateTimeOffset now)
    {
        if (IsExhausted) return false;
        if (LastQueuedAt is not { } last) return true;
        return now - last >= EffectiveCooldown;
    }

    /// <summary>
    /// Whether a comment revision has settled long enough to act on. An edit moments after posting
    /// must not spend a turn on the pre-edit text, so a too-young revision defers to a later poll
    /// rather than being rejected — rejecting would consume the candidate and require another edit
    /// to bring it back.
    /// </summary>
    public bool HasSettledAt(DateTimeOffset revisionAt, DateTimeOffset now) =>
        now - revisionAt >= EffectiveDebounce;
}
