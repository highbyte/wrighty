using Highbyte.Wrighty.Configuration;

namespace Highbyte.Wrighty.ApprovedContext;

/// <summary>Why an evaluation did or did not queue a continuation.</summary>
public enum ContinuationOutcome
{
    /// <summary>Queue the retained session; <see cref="ContinuationVerdict.Trigger"/> says on what.</summary>
    Queue,

    /// <summary>Nothing a trusted author wrote since the last run asks for a continuation.</summary>
    NoCandidate,

    /// <summary>A candidate exists but its revision is too fresh to trust as final; re-evaluate later.</summary>
    Deferred,

    /// <summary>Every candidate revision already queued a run.</summary>
    AlreadyConsumed,

    /// <summary>The automatic continuation budget for this session is spent.</summary>
    LimitReached,

    /// <summary>A continuation queued too recently for another one.</summary>
    CoolingDown,

    /// <summary>Something else in the conversation is undecided, so continuing would narrow the task.</summary>
    ContextPending,

    /// <summary>
    /// A continuation was warranted but the backend cannot queue a waiting session without an
    /// operator. Decided by the caller after the evaluation, never by the evaluator itself.
    /// </summary>
    QueueUnavailable
}

public sealed record ContinuationVerdict(
    ContinuationOutcome Outcome,
    TrustedContinuationEvent? Trigger = null,
    IReadOnlyList<string>? ConsumedKeys = null,
    string? Reason = null)
{
    public bool ShouldQueue => Outcome == ContinuationOutcome.Queue;
}

/// <summary>
/// Decides whether a trusted author's comment should continue a waiting session (plan 030
/// decision 19, comment half).
///
/// <para><b>It never decides who is trusted.</b> Candidates come only from decisions the
/// <see cref="ApprovedContextResolver"/> already resolved to
/// <see cref="DiscussionDecisionKind.Include"/> with source
/// <see cref="DiscussionDecisionSource.TrustedAuthor"/>. This is not a stylistic preference. In the
/// recommended solo configuration the trusted author and the run-report publisher are the same
/// GitHub identity, so an independent author check here would read Wrighty's own run report as a
/// trusted reply and continue forever, spending real agent turns. The resolver drops Wrighty's
/// protocol comments before any decision exists, and reusing its output is what keeps that closed.
/// </para>
///
/// <para>Pure and clock-injected: preconditions that belong to the worker — automatic execution, a
/// resumable same-installation session, a needs-attention dispatch state — are checked by the
/// caller, so everything here is decided from its arguments alone.</para>
/// </summary>
public sealed class TrustedContinuationEvaluator
{
    public ContinuationVerdict Evaluate(
        ExecutionContextSnapshot snapshot,
        ContextManifest? suppliedManifest,
        SessionContinuationState state,
        WorkerContinuationConfig config,
        DateTimeOffset now)
    {
        // Defensive: the resolver refuses rather than returning an unresolved snapshot, so the
        // caller normally maps that refusal itself. Continuing on a conversation with an undecided
        // comment would silently narrow the approved task.
        if (!snapshot.IsFullyResolved)
            return new ContinuationVerdict(
                ContinuationOutcome.ContextPending,
                Reason: "Another comment on this item is undecided, so continuing would act on " +
                        "less than the conversation currently says.");

        var candidates = Candidates(snapshot, suppliedManifest, config);
        if (candidates.Count == 0)
            return new ContinuationVerdict(
                ContinuationOutcome.NoCandidate,
                Reason: config.RequiresCommand
                    ? $"No trusted comment since the last run opens with \"{config.Command}\"."
                    : "No trusted comment has arrived since the last run.");

        var fresh = candidates.Where(c => !state.HasConsumed(c.ConsumptionKey)).ToArray();
        if (fresh.Length == 0)
            return new ContinuationVerdict(
                ContinuationOutcome.AlreadyConsumed,
                Reason: "Every trusted comment here has already continued this session once. " +
                        "Editing one produces a new revision, which counts again.");

        // The newest names the trigger, because that is the comment an operator just wrote and
        // expects to see acknowledged. Every fresh candidate is consumed, though: they are all
        // delivered to the resumed session, so leaving the older ones unconsumed would let them
        // queue a second run that adds nothing.
        var trigger = fresh.OrderByDescending(c => c.RevisionAt).First();
        var budget = state.BudgetWith(
            config.MaxAutomaticContinuations, config.Cooldown, config.Debounce);

        // Debounce defers rather than rejects: a comment edited moments after posting must not
        // spend a turn on the pre-edit text, and consuming it here would require another edit to
        // bring it back.
        if (!budget.HasSettledAt(trigger.RevisionAt, now))
            return new ContinuationVerdict(
                ContinuationOutcome.Deferred,
                trigger,
                Reason: "The comment was written or edited moments ago; waiting for it to settle.");

        if (budget.IsExhausted)
            return new ContinuationVerdict(
                ContinuationOutcome.LimitReached,
                trigger,
                Reason: $"This session has used all {budget.MaxAutomaticContinuations} automatic " +
                        "continuations. It stays here until you resume or release it yourself.");

        if (!budget.CanQueueAt(now))
            return new ContinuationVerdict(
                ContinuationOutcome.CoolingDown,
                trigger,
                Reason: "Another continuation was queued moments ago; waiting for the cooldown.");

        return new ContinuationVerdict(
            ContinuationOutcome.Queue,
            trigger,
            [.. fresh.Select(c => c.ConsumptionKey)]);
    }

    /// <summary>
    /// Trusted comments the previous run did not already hold. A comment absent from the supplied
    /// manifest is new; one present at a different revision was edited after that run saw it, which
    /// is a new thing for the agent to read either way.
    /// </summary>
    private static List<TrustedContinuationEvent> Candidates(
        ExecutionContextSnapshot snapshot,
        ContextManifest? suppliedManifest,
        WorkerContinuationConfig config)
    {
        var trusted = snapshot.Decisions
            .Where(d => d.Decision == DiscussionDecisionKind.Include &&
                        d.Source == DiscussionDecisionSource.TrustedAuthor)
            .Select(d => d.CommentId)
            .ToHashSet(StringComparer.Ordinal);

        var supplied = suppliedManifest?.Included.ToDictionary(
            entry => entry.CommentId, entry => entry.RevisionAt, StringComparer.Ordinal);

        var candidates = new List<TrustedContinuationEvent>();
        foreach (var entry in snapshot.Discussion)
        {
            if (!trusted.Contains(entry.StableId)) continue;

            if (supplied is not null &&
                supplied.TryGetValue(entry.StableId, out var suppliedRevision) &&
                suppliedRevision == entry.RevisionAt)
                continue;

            if (config.RequiresCommand && !OpensWithCommand(entry.Body, config.Command)) continue;

            candidates.Add(new TrustedContinuationEvent(
                entry.StableId, entry.Author, entry.RevisionAt));
        }

        return candidates;
    }

    /// <summary>
    /// Whether a body opens with the configured control command, matched as a whole normalized
    /// first line. Never a substring or natural-language match: prose that merely mentions
    /// continuing must not start an agent run.
    /// </summary>
    private static bool OpensWithCommand(string? body, string command)
    {
        if (string.IsNullOrWhiteSpace(body) || string.IsNullOrWhiteSpace(command)) return false;

        var firstLine = body
            .Split('\n', 2)[0]
            .Replace("\r", string.Empty, StringComparison.Ordinal)
            .Trim();

        return string.Equals(firstLine, command.Trim(), StringComparison.OrdinalIgnoreCase);
    }
}
