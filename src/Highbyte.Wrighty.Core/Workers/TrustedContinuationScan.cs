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
    string? TriggerCommentId = null,
    string? Actor = null,
    string? Reason = null);

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
    Func<DateTimeOffset>? clock = null)
{
    private readonly TrustedContinuationEvaluator evaluator = new();
    private readonly Func<DateTimeOffset> now = clock ?? (() => DateTimeOffset.UtcNow);

    public async Task<IReadOnlyList<ContinuationScanResult>> RunAsync(
        TrackerConfig config,
        WorkerOptions options,
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
            if (await EvaluateAsync(config, options, provider, summary.Id, cancellationToken)
                is { } result)
                results.Add(result);
        }

        return results;
    }

    private async Task<ContinuationScanResult?> EvaluateAsync(
        TrackerConfig config,
        WorkerOptions options,
        IExecutionContextProvider provider,
        WorkItemId id,
        CancellationToken cancellationToken)
    {
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

        var verdict = evaluator.Evaluate(
            snapshot,
            session.Context?.Manifest,
            state,
            config.Worker?.EffectiveContinuation ?? new WorkerContinuationConfig(),
            now());

        if (!verdict.ShouldQueue)
            return new ContinuationScanResult(
                id, verdict.Outcome, verdict.Trigger?.CommentId, verdict.Trigger?.Actor,
                verdict.Reason);

        // Consumption is durable *before* the queue transition. A crash between the two then leaves
        // an item that simply was not queued yet, which the next poll fixes; the reverse order would
        // leave a queued run whose trigger still looks unconsumed, and the same comment would spend
        // a second turn.
        await tracker.RecordContinuationAsync(
            config, id, state.WithConsumed(verdict.ConsumedKeys!, now()), cancellationToken);
        try
        {
            await tracker.QueuePausedAsync(config, id, cancellationToken);
        }
        catch (Exception exception) when (
            exception is NotSupportedException or TrackerException)
        {
            // The consume-first order is right for a crash, which is transient: the next poll
            // finishes the job. A refusal is not transient, so leaving the trigger consumed would
            // burn it — the reply would never queue anything, and editing it would be the only way
            // back. Put the spend back and report why nothing happened.
            await SafeRestoreAsync(config, id, state, cancellationToken);
            return new ContinuationScanResult(
                id, ContinuationOutcome.QueueUnavailable, verdict.Trigger!.CommentId,
                verdict.Trigger.Actor,
                exception is NotSupportedException
                    ? "This backend cannot queue a waiting session automatically, so the reply " +
                      "was read but nothing was started. Resume the item yourself."
                    : exception.Message);
        }

        return new ContinuationScanResult(
            id, ContinuationOutcome.Queue, verdict.Trigger!.CommentId, verdict.Trigger.Actor);
    }

    /// <summary>
    /// Puts a spend back after a refusal. Best-effort: if this fails the trigger stays consumed,
    /// which costs the operator an edit to retry — worse than nothing, but far better than letting
    /// the failure stop the scan.
    /// </summary>
    private async Task SafeRestoreAsync(
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
            // Intentionally swallowed; see the summary.
        }
    }
}
