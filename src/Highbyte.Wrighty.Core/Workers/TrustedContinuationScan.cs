using Highbyte.Wrighty.ApprovedContext;
using Highbyte.Wrighty.Claims;
using Highbyte.Wrighty.Configuration;
using Highbyte.Wrighty.Errors;
using Highbyte.Wrighty.Models;

namespace Highbyte.Wrighty.Workers;

/// <summary>What one item's continuation evaluation did, for diagnostics and handover text.</summary>
public sealed record ContinuationScanResult(
    WorkItemId Id,
    ContinuationOutcome Outcome,
    string? TriggerStableId = null,
    string? Actor = null,
    string? Reason = null,
    TrustedContinuationSource? TriggerSource = null,
    TrustedContinuationKind? TriggerKind = null);

/// <summary>
/// Scans waiting items for a trusted author's reply and queues the retained session (plan 030
/// decision 19, comment half).
///
/// <para>It performs exactly one mutation per qualifying item — <c>needs-attention</c> to
/// <c>queued</c>, plus the durable consumption record — and then stops. Claiming, provider capacity,
/// prompt rendering, and the launch preflight all stay where they already work: the queued session
/// is picked up by the ordinary queued-candidate path on the same poll. A defect here can at worst
/// queue something that should have waited; it cannot corrupt a claim or bypass a preflight.</para>
/// </summary>
public sealed class TrustedContinuationScan(
    TrackerService tracker,
    Func<TrackerConfig, IExecutionContextProvider?> providers,
    Func<TrackerConfig, ContextLimits>? limits = null,
    Func<DateTimeOffset>? clock = null,
    Func<TrackerConfig, ITrustedControlReactionProvider?>? controlReactionProviders = null)
{
    private readonly Func<DateTimeOffset> now = clock ?? (() => DateTimeOffset.UtcNow);

    public Task<IReadOnlyList<ContinuationScanResult>> RunAsync(
        TrackerConfig config,
        WorkerOptions options,
        CancellationToken cancellationToken) =>
        RunAsync(config, options, act: true, cancellationToken);

    /// <summary>
    /// Evaluates without consuming or queueing anything. Preflight runs before the operator has
    /// confirmed execution, so it must be able to see that a trusted reply is waiting without
    /// spending the continuation turn — the real scan in the worker loop does the spending.
    /// </summary>
    public Task<IReadOnlyList<ContinuationScanResult>> ProbeAsync(
        TrackerConfig config,
        WorkerOptions options,
        CancellationToken cancellationToken) =>
        RunAsync(config, options, act: false, cancellationToken);

    private async Task<IReadOnlyList<ContinuationScanResult>> RunAsync(
        TrackerConfig config,
        WorkerOptions options,
        bool act,
        CancellationToken cancellationToken)
    {
        if (providers(config) is not { } provider)
            return [];

        var results = new List<ContinuationScanResult>();
        var activeStatus = options.ToStatus ?? config.DefaultPickTo;
        var summaries = await tracker.ListAsync(
            config, new ListWorkItemsRequest(activeStatus, null), cancellationToken);

        foreach (var summary in summaries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await EvaluateAsync(
                    config, options, provider, controlReactionProviders?.Invoke(config), summary,
                    act, cancellationToken)
                is { } result)
                results.Add(result);
        }

        return results;
    }

    private async Task<ContinuationScanResult?> EvaluateAsync(
        TrackerConfig config,
        WorkerOptions options,
        IExecutionContextProvider provider,
        ITrustedControlReactionProvider? controlReactions,
        WorkItemSummary summary,
        bool act,
        CancellationToken cancellationToken)
    {
        var id = summary.Id;
        var detail = await tracker.GetAsync(config, id, cancellationToken);
        if (!string.Equals(
                detail.DispatchState, DispatchStates.NeedsAttention, StringComparison.OrdinalIgnoreCase))
            return null;
        if (!detail.AutomaticExecutionAllowed ||
            !WorkerPolicyGate.MatchesFilters(detail, options.Filters))
            return null;

        var session = await tracker.GetAgentSessionAsync(config, id, cancellationToken);
        if (session is not { IsComplete: true, FromCurrentInstallation: true })
            // Nothing to continue here: a session that cannot resume on this host must not be
            // restarted somewhere else, and the operator recovery choices already cover it.
            return null;

        var state = session.Continuation ?? new SessionContinuationState();
        var settings = config.Worker?.EffectiveContinuation ?? new WorkerContinuationConfig();

        TrustedControlReactionReading? control = null;
        if (controlReactions is not null &&
            config.EffectiveWorker.EffectiveHandoverComment != HandoverCommentMode.Off &&
            session.LastReport is { } latestReport)
        {
            try
            {
                control = await controlReactions.ReadAsync(
                    config, id, latestReport, state, settings, cancellationToken);
            }
            catch (TrackerException exception)
            {
                // The optional reaction channel failed closed, but an ordinary trusted comment is
                // still independently observable and approved. Preserve that path when the issue
                // revision says the discussion changed; otherwise surface the reaction diagnostic.
                if (!state.MayHaveChangedSince(summary.UpdatedAt))
                    return new ContinuationScanResult(
                        id, ContinuationOutcome.NoCandidate, Reason: exception.Message);
            }
        }

        var stateAtRead = control is null ? state : state.WithControlReport(control);
        var freshControl = control?.Event is { } controlEvent &&
                           !state.HasConsumed(controlEvent.ConsumptionKey);

        // The conversation read is the expensive part of this scan, and a waiting item is usually
        // waiting precisely because nobody has replied. An item whose own revision has not moved
        // since it was last examined cannot have gained a comment, so the read is skipped entirely.
        // A backend that cannot report a revision answers "unknown", which reads as "may have
        // changed" and simply keeps paying for the read.
        if (!state.MayHaveChangedSince(summary.UpdatedAt) &&
            !freshControl && control?.Reason is null)
        {
            if (act && stateAtRead != state)
                await RememberControlAsync(config, id, stateAtRead, cancellationToken);
            return null;
        }

        ExecutionContextResult context;
        try
        {
            context = await provider.GetAsync(
                config, id, ContextReadPurpose.PreLaunch,
                (limits ?? (_ => ContextLimits.Default))(config), cancellationToken);
        }
        catch (TrackerException exception)
        {
            // One unreadable item must not stop the scan; the worker keeps polling and the item is
            // reconsidered next time.
            return new ContinuationScanResult(
                id, ContinuationOutcome.NoCandidate, Reason: exception.Message);
        }

        if (!context.IsApproved || context.Snapshot is not { } snapshot)
            return new ContinuationScanResult(
                id,
                context.Code == ExecutionContextResult.Codes.CommentPending
                    ? ContinuationOutcome.ContextPending
                    : ContinuationOutcome.NoCandidate,
                Reason: context.Message);

        var verdict = TrustedContinuationEvaluator.Evaluate(
            snapshot,
            session.Context?.Manifest,
            stateAtRead,
            settings,
            now(),
            freshControl ? control!.Event : null,
            control?.Reason);

        if (!verdict.ShouldQueue)
        {
            // Only after a read that found nothing: recording the revision is what lets the next
            // poll skip this item, and recording it without having read would skip a reply.
            // Deferred is excluded — it is waiting for the same revision to settle, and marking it
            // observed would mean never looking again.
            if (act && verdict.Outcome != ContinuationOutcome.Deferred)
                await RememberAsync(
                    config, id, stateAtRead, summary.UpdatedAt, cancellationToken);
            return new ContinuationScanResult(
                id, verdict.Outcome, verdict.Trigger?.StableId, verdict.Trigger?.Actor,
                verdict.Reason, verdict.Trigger?.Source, verdict.Trigger?.Kind);
        }

        return act
            ? await QueueAsync(
                config, id, stateAtRead, summary.UpdatedAt, verdict, cancellationToken)
            : new ContinuationScanResult(
                id, ContinuationOutcome.Queue, verdict.Trigger!.StableId, verdict.Trigger.Actor,
                TriggerSource: verdict.Trigger.Source, TriggerKind: verdict.Trigger.Kind);
    }

    /// <summary>
    /// Publishes the transition and records the spend, in that order.
    ///
    /// <para>The reverse — spend first, restore on refusal — burned the trigger whenever anything
    /// stopped the scan between the two writes, and live testing hit exactly that: an operator's
    /// Ctrl+C during the queue attempt's API calls left the key consumed, the item unqueued, and
    /// every later poll answering "already consumed" for a comment no run had ever seen.</para>
    ///
    /// <para>Queue-first has no burning failure mode. A refusal spends nothing, so there is nothing
    /// to restore. A crash after the transition costs one uncounted budget turn instead: the item is
    /// already queued, so the scan does not reconsider it; the resumed run delivers the comment and
    /// records it in the session manifest, and the manifest — not the key — excludes it from every
    /// later evaluation.</para>
    /// </summary>
    private async Task<ContinuationScanResult> QueueAsync(
        TrackerConfig config,
        WorkItemId id,
        SessionContinuationState state,
        DateTimeOffset? observedItemRevision,
        ContinuationVerdict verdict,
        CancellationToken cancellationToken)
    {
        try
        {
            await tracker.QueuePausedAsync(config, id, cancellationToken);
        }
        catch (Exception exception) when (
            exception is NotSupportedException or TrackerException)
        {
            return new ContinuationScanResult(
                id, ContinuationOutcome.QueueUnavailable, verdict.Trigger!.StableId,
                verdict.Trigger.Actor,
                exception is NotSupportedException
                    ? "This backend cannot queue a waiting session automatically, so the reply " +
                      "was read but nothing was started. Resume the item yourself."
                    : exception.Message,
                verdict.Trigger.Source,
                verdict.Trigger.Kind);
        }

        try
        {
            await tracker.RecordContinuationAsync(
                config,
                id,
                state.WithConsumed(verdict.ConsumedEvents!, now())
                    .WithObservedItemRevision(observedItemRevision),
                cancellationToken);
        }
        catch (Exception exception) when (
            exception is not OperationCanceledException)
        {
            // The queue already succeeded, and that is the outcome that matters: the worker picks
            // the item up regardless. Failing the item over the bookkeeping write would report a
            // continuation that in fact happened as broken; the cost of the lost record is the
            // same one uncounted turn a crash here costs.
        }

        return new ContinuationScanResult(
            id, ContinuationOutcome.Queue, verdict.Trigger!.StableId, verdict.Trigger.Actor,
            TriggerSource: verdict.Trigger.Source, TriggerKind: verdict.Trigger.Kind);
    }

    /// <summary>
    /// Stores the item revision this scan examined, so an unchanged item costs nothing next poll.
    ///
    /// Best-effort by design: losing it costs one redundant read, so a failure here must not turn a
    /// correctly-evaluated item into a reported problem. Skipped when the revision is unknown or
    /// already recorded, which keeps an idle backlog from writing on every poll.
    /// </summary>
    private async Task RememberAsync(
        TrackerConfig config,
        WorkItemId id,
        SessionContinuationState state,
        DateTimeOffset? observedItemRevision,
        CancellationToken cancellationToken)
    {
        var updated = state.WithObservedItemRevision(observedItemRevision);
        if (updated == state) return;

        try
        {
            await tracker.RecordContinuationAsync(config, id, updated, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Intentionally swallowed; see the summary.
        }
    }

    private async Task RememberControlAsync(
        TrackerConfig config,
        WorkItemId id,
        SessionContinuationState state,
        CancellationToken cancellationToken)
    {
        try
        {
            await tracker.RecordContinuationAsync(config, id, state, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Losing the locator costs another paginated lookup; it cannot lose or duplicate a
            // trigger because consumption remains keyed by the immutable reaction ID.
        }
    }
}
