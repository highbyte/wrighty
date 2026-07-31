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
/// <remarks>
/// Deliberately carries no last-observed item revision. The evaluation would ideally skip its
/// conversation read for an item whose issue has not moved since the last poll, but the item
/// timestamp that gate needs is not on <c>WorkItemDetail</c> on either backend — surfacing it is
/// separate planned work. Reserving an unused field here for it would be the same dead machinery
/// that had to be removed from this feature once already, so the read happens every poll for every
/// waiting item until the timestamp exists. The measured propagation behaviour that gate depends on
/// is recorded in the decision-19 design pass, along with the constraint it imposes.
/// </remarks>
public sealed record SessionContinuationState(
    IReadOnlyList<string>? ConsumedKeys = null,
    int AutomaticContinuations = 0,
    DateTimeOffset? LastQueuedAt = null)
{
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

    public TrustedContinuationBudget BudgetWith(int max, TimeSpan cooldown, TimeSpan debounce) =>
        new(max, AutomaticContinuations, LastQueuedAt, cooldown, debounce);
}
