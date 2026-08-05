namespace Highbyte.Wrighty.ApprovedContext;

/// <summary>
/// What a session has spent on automatic continuation, and what it has already acted on.
///
/// Deliberately a sibling of <see cref="SessionContextMetadata"/> rather than part of it. That
/// record describes the approved context a session was given, and its <c>Supersede</c> carries
/// exactly one field forward on purpose; folding spend into it would mean every later change to
/// that method silently decides a budget question. The lifetimes differ too — a superseding
/// context replaces the manifest while the spend so far must survive.
///
/// Every field is optional and the whole record is nullable on the session, so a session written by
/// an older binary stays readable and simply reads as unspent.
/// </summary>
public sealed record SessionContinuationState(
    IReadOnlyList<string>? ConsumedKeys = null,
    int AutomaticContinuations = 0,
    DateTimeOffset? LastQueuedAt = null,
    DateTimeOffset? LastObservedItemUpdatedAt = null,
    IReadOnlyList<TrustedContinuationEvent>? Events = null,
    string? ControlReportId = null,
    string? ControlReportCommentId = null,
    DateTimeOffset? ControlReportRevisionAt = null)
{
    /// <summary>
    /// Whether the item may have gained a reply since it was last examined.
    ///
    /// Unknown answers yes. A missing timestamp on either side is not evidence of no change, and
    /// being wrong here costs one redundant read rather than a missed continuation.
    /// </summary>
    public bool MayHaveChangedSince(DateTimeOffset? itemUpdatedAt) =>
        LastObservedItemUpdatedAt is not { } observed ||
        itemUpdatedAt is not { } current ||
        current > observed;

    /// <summary>
    /// Records the item revision an evaluation actually saw.
    ///
    /// <para>This must be the <em>observed</em> item timestamp, never the worker's wall clock, and
    /// the difference is not cosmetic. GitHub propagates a comment edit to the issue's own
    /// <c>updatedAt</c> with a short delay, so a poll taken just after an edit still reads the
    /// pre-edit value. Storing wall-clock time would record an instant later than the edit the gate
    /// is waiting for, and that edit would then never look newer than what was stored — the reply
    /// would be skipped permanently rather than merely one poll late. Measured on a live repository:
    /// two consecutive edits each read stale immediately after, then converged within seconds.</para>
    /// </summary>
    public SessionContinuationState WithObservedItemRevision(DateTimeOffset? itemUpdatedAt) =>
        itemUpdatedAt is null ? this : this with { LastObservedItemUpdatedAt = itemUpdatedAt };

    /// <summary>
    /// Whether this exact comment revision already queued a run. Ordinal because the key is built
    /// from a stable ID and a round-tripped timestamp, not from anything user-facing.
    /// </summary>
    public bool HasConsumed(string consumptionKey) =>
        ConsumedKeys is { } keys && keys.Contains(consumptionKey, StringComparer.Ordinal);

    /// <summary>
    /// Records that a continuation queued a run: every key it acted on becomes unrepeatable and the
    /// spend advances by exactly one, however many comments that run carried. The budget counts
    /// agent turns, not comments — charging per comment would make a burst of replies cost more
    /// than the single run they produce.
    ///
    /// The key list is never trimmed. It is bounded by the continuation budget — only a queued
    /// continuation consumes, and the budget caps how many of those a session can have — so
    /// trimming would buy nothing and would let an old revision trigger a second time.
    /// </summary>
    public SessionContinuationState WithConsumed(
        IReadOnlyList<string> consumptionKeys,
        DateTimeOffset queuedAt) =>
        this with
        {
            ConsumedKeys = [.. ConsumedKeys ?? [], .. consumptionKeys],
            AutomaticContinuations = AutomaticContinuations + 1,
            LastQueuedAt = queuedAt
        };

    public SessionContinuationState WithConsumed(
        IReadOnlyList<TrustedContinuationEvent> events,
        DateTimeOffset queuedAt) =>
        this with
        {
            ConsumedKeys = [.. ConsumedKeys ?? [], .. events.Select(value => value.ConsumptionKey)],
            Events = [.. Events ?? [], .. events.Select(value => value.Consumed(queuedAt))],
            AutomaticContinuations = AutomaticContinuations + 1,
            LastQueuedAt = queuedAt
        };

    public SessionContinuationState WithControlReport(TrustedControlReactionReading reading) =>
        this with
        {
            ControlReportId = reading.ReportId,
            ControlReportCommentId = reading.ReportCommentId,
            ControlReportRevisionAt = reading.ReportRevisionAt
        };

    public TrustedContinuationEvent? PendingTrigger => Events?
        .LastOrDefault(value => value.ConsumedAt is not null && value.TriggeredRunId is null);

    public SessionContinuationState WithTriggeredRun(string consumptionKey, string runId) =>
        Events is null
            ? this
            : this with
            {
                Events = Events.Select(value =>
                    string.Equals(value.ConsumptionKey, consumptionKey, StringComparison.Ordinal)
                        ? value.Triggered(runId)
                        : value).ToArray()
            };

    public TrustedContinuationBudget BudgetWith(int max, TimeSpan cooldown, TimeSpan debounce) =>
        new(max, AutomaticContinuations, LastQueuedAt, cooldown, debounce);
}
