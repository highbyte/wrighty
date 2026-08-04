using Highbyte.Wrighty.Addressing;
using Highbyte.Wrighty.AgentContext;
using Highbyte.Wrighty.Claims;
using Highbyte.Wrighty.Configuration;
using Highbyte.Wrighty.Models;

namespace Highbyte.Wrighty.Backends;

/// <summary>
/// Reading one work item's current content. Split out of <see cref="ITrackerBackend"/> so a
/// component that only needs to read an item does not take a dependency on claiming, mutation, and
/// initialization as well. Every backend satisfies it already.
/// </summary>
public interface IWorkItemContentReader
{
    Task<WorkItemDetail?> GetAsync(
        TrackerConfig config,
        WorkItemId id,
        CancellationToken cancellationToken);
}

public interface ITrackerBackend : IWorkItemContentReader
{
    string Name { get; }

    IWorkItemAddressResolver AddressResolver { get; }

    Task<BackendInitializationResult> InitializeAsync(
        TrackerConfig config,
        bool checkOnly,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<WorkItemSummary>> ListAsync(
        TrackerConfig config,
        ListWorkItemsRequest request,
        CancellationToken cancellationToken);

    Task<CreateWorkItemResult> CreateAsync(
        TrackerConfig config,
        CreateWorkItemOperation operation,
        CancellationToken cancellationToken);

    Task<UpdateWorkItemResult> UpdateAsync(
        TrackerConfig config,
        WorkItemId id,
        UpdateWorkItemOperation operation,
        CancellationToken cancellationToken);

    Task<ClaimResult> TryClaimAsync(
        TrackerConfig config,
        WorkItemId id,
        AgentExecutionContext agentContext,
        CancellationToken cancellationToken);

    Task<ClaimResult> TryClaimAsync(TrackerConfig config, WorkItemId id,
        AgentExecutionContext agentExecutionContext, CancellationToken cancellationToken,
        string? expectedClaimToken);

    Task<ClaimResult> TakeoverAsync(
        TrackerConfig config,
        WorkItemId id,
        AgentExecutionContext claimantContext,
        string? currentClaimToken,
        CancellationToken cancellationToken);

    Task<ClaimResult> RenewClaimAsync(
        TrackerConfig config,
        WorkItemId id,
        ClaimHandle claimHandle,
        string? workspacePath,
        string? sessionId,
        CancellationToken cancellationToken) =>
        RenewClaimAsync(config, id, claimHandle, workspacePath, sessionId, branch: null,
            cancellationToken);

    Task<ClaimResult> RenewClaimAsync(
        TrackerConfig config,
        WorkItemId id,
        ClaimHandle claimHandle,
        string? workspacePath,
        string? sessionId,
        string? branch,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    Task<ClaimOwnershipResult> GetClaimOwnershipAsync(
        TrackerConfig config,
        WorkItemId id,
        CancellationToken cancellationToken);

    Task<AgentSessionRecord?> GetAgentSessionAsync(
        TrackerConfig config,
        WorkItemId id,
        CancellationToken cancellationToken) =>
        Task.FromResult<AgentSessionRecord?>(null);

    /// <summary>
    /// Stores the run's structured report with the durable session record. Separate from publishing:
    /// publishing decides whether other people see it, this decides whether it survives at all, and
    /// a backend with no comment surface still keeps the agent's account.
    /// </summary>
    Task RecordRunReportAsync(
        TrackerConfig config,
        WorkItemId id,
        ApprovedContext.AgentRunReport report,
        CancellationToken cancellationToken) =>
        Task.CompletedTask;

    /// <summary>
    /// Publishes the durable record of one finished run. Backends without a comment surface are
    /// no-ops: the report is still stored locally, it simply has nowhere public to appear.
    /// </summary>
    Task PublishRunReportAsync(
        TrackerConfig config,
        WorkItemId id,
        ApprovedContext.AgentRunReport report,
        string? branch,
        CancellationToken cancellationToken) =>
        Task.CompletedTask;

    /// <summary>
    /// Records what the launch supplied to the item's session: the context manifest, the approval
    /// instants, and the continuation state. Hashes and identifiers only — plan 030 forbids keeping
    /// comment bodies in durable machine-local state, and a later launch re-reads the content and
    /// verifies it against this record rather than trusting a stored copy.
    ///
    /// Overwrite-only and best-effort. The default is a no-op for backends that keep no durable
    /// session records; such a backend simply cannot resume across a changed context.
    /// </summary>
    Task RecordSessionContextAsync(
        TrackerConfig config,
        WorkItemId id,
        ApprovedContext.SessionContextMetadata context,
        CancellationToken cancellationToken) =>
        Task.CompletedTask;

    /// <summary>
    /// Records automatic-continuation spend for the item's session: which comment revisions have
    /// already queued a run, and how many turns this session has spent.
    ///
    /// Overwrite-only, and written *after* the queue transition is published, never before.
    /// Spend-first looked safer on paper but burned the trigger in practice: any interruption
    /// between the spend and the queue left a consumed key for a comment no run ever saw, and no
    /// later poll would touch it again. The reverse gap is benign — a queued item is not
    /// re-evaluated, and the resumed run records the comment in its session manifest, which is
    /// what excludes it from later evaluations. A crash after queueing therefore costs one
    /// uncounted budget turn, not the operator's trigger. The default is a no-op for backends
    /// that keep no durable session records; such a backend cannot continue automatically
    /// because it cannot resume at all.
    /// </summary>
    Task RecordContinuationAsync(
        TrackerConfig config,
        WorkItemId id,
        ApprovedContext.SessionContinuationState continuation,
        CancellationToken cancellationToken) =>
        Task.CompletedTask;

    /// <summary>
    /// Records the outcome of the just-ended agent run onto the item's durable session record.
    /// Overwrite-only and best-effort; the default is a no-op for backends without durable
    /// session records.
    /// </summary>
    Task RecordRunOutcomeAsync(
        TrackerConfig config,
        WorkItemId id,
        RunOutcome outcome,
        string? finalMessage,
        DateTimeOffset endedAt,
        Workers.AgentFailure? failure,
        CancellationToken cancellationToken) =>
        Task.CompletedTask;

    Task RecordPendingDispatchAsync(
        TrackerConfig config,
        WorkItemId id,
        Workers.PendingDispatch dispatch,
        CancellationToken cancellationToken) =>
        Task.CompletedTask;

    Task ClearPendingDispatchAsync(
        TrackerConfig config,
        WorkItemId id,
        CancellationToken cancellationToken) =>
        Task.CompletedTask;

    /// <summary>
    /// Best-effort presentation of an already-persisted dispatch decision. Backends without a
    /// separate presentation surface are no-ops.
    /// </summary>
    Task PresentDispatchAsync(
        TrackerConfig config,
        WorkItemId id,
        Workers.DispatchInfo dispatch,
        CancellationToken cancellationToken) =>
        Task.CompletedTask;

    /// <summary>
    /// Posts or overwrites the single marker-identified handover comment on the item. Best-effort;
    /// the default is a no-op for backends without a comment surface.
    /// </summary>
    Task PostHandoverAsync(
        TrackerConfig config,
        Workers.HandoverContent content,
        CancellationToken cancellationToken) =>
        Task.CompletedTask;

    /// <summary>
    /// Trims any existing handover comment to a "resolved" form. No-op by default and when no
    /// handover comment exists.
    /// </summary>
    Task ResolveHandoverAsync(
        TrackerConfig config,
        WorkItemId id,
        string reason,
        CancellationToken cancellationToken) =>
        Task.CompletedTask;

    Task ReleaseAsync(
        TrackerConfig config,
        WorkItemId id,
        CancellationToken cancellationToken);

    Task ReleaseAsync(
        TrackerConfig config,
        WorkItemId id,
        ClaimHandle claimHandle,
        bool overrideClaimant,
        CancellationToken cancellationToken);

    /// <summary>Ends a fenced claim while preserving a worker-state decision that was written
    /// immediately before release (for example retry-scheduled or needs-attention).</summary>
    Task ReleasePreservingDispatchStateAsync(
        TrackerConfig config,
        WorkItemId id,
        ClaimHandle claimHandle,
        CancellationToken cancellationToken) =>
        ReleaseAsync(config, id, claimHandle, overrideClaimant: false, cancellationToken);

    Task RequeueAsync(
        TrackerConfig config,
        WorkItemId id,
        ClaimHandle claimHandle,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    Task QueuePausedAsync(
        TrackerConfig config,
        WorkItemId id,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    /// <summary>
    /// Performs decision 10's reapproval cycle on the item's context approval surface: reset to
    /// needs-review, then approve, moving the batch comment cutoff to now. The default refuses:
    /// only a backend with an approval surface can offer it.
    /// </summary>
    Task CycleContextApprovalAsync(
        TrackerConfig config,
        WorkItemId id,
        CancellationToken cancellationToken) =>
        throw new Errors.TrackerException(
            "CONTEXT_APPROVAL_UNSUPPORTED",
            $"The '{config.Backend}' backend has no context approval surface to cycle.",
            3);

    /// <summary>
    /// Sets the context approval surface to needs-review after a title/body edit. The default
    /// refuses so backends whose content is inherently approved never expose this operation.
    /// </summary>
    Task InvalidateContextApprovalAsync(
        TrackerConfig config,
        WorkItemId id,
        CancellationToken cancellationToken) =>
        throw new Errors.TrackerException(
            "CONTEXT_APPROVAL_UNSUPPORTED",
            $"The '{config.Backend}' backend has no context approval surface to invalidate.",
            3);

    Task<ArchiveWorkItemResult> ArchiveAsync(
        TrackerConfig config,
        WorkItemId id,
        CancellationToken cancellationToken);

    Task<ArchiveWorkItemResult> ArchiveAsync(
        TrackerConfig config,
        WorkItemId id,
        ClaimHandle claimHandle,
        CancellationToken cancellationToken);

    Task<ArchiveWorkItemResult> UnarchiveAsync(
        TrackerConfig config,
        WorkItemId id,
        CancellationToken cancellationToken);

    /// <summary>
    /// Reads one item's operational state (content, claim, session). The default composes the
    /// three separate reads; backends that can produce all three from one snapshot should
    /// override it so the result is consistent and cheaper.
    /// </summary>
    async Task<WorkItemOperationalSnapshot?> GetOperationalAsync(
        TrackerConfig config,
        WorkItemId id,
        CancellationToken cancellationToken)
    {
        var item = await GetAsync(config, id, cancellationToken);
        if (item is null)
        {
            return null;
        }

        var ownership = await GetClaimOwnershipAsync(config, id, cancellationToken);
        var session = await GetAgentSessionAsync(config, id, cancellationToken);
        return new WorkItemOperationalSnapshot(
            item,
            WorkItemClaimSummary.FromOwnership(ownership),
            session);
    }

    /// <summary>
    /// Reads operational state for every item matching the request. The default iterates the
    /// per-item read; backends with a snapshot-capable store should override it to read
    /// everything under one consistent snapshot.
    /// </summary>
    async Task<IReadOnlyList<WorkItemOperationalSnapshot>> ListOperationalAsync(
        TrackerConfig config,
        ListWorkItemsRequest request,
        CancellationToken cancellationToken)
    {
        var summaries = await ListAsync(config, request, cancellationToken);
        var results = new List<WorkItemOperationalSnapshot>(summaries.Count);
        foreach (var summary in summaries)
        {
            var snapshot = await GetOperationalAsync(config, summary.Id, cancellationToken);
            if (snapshot is null)
            {
                continue;
            }

            results.Add(snapshot with
            {
                Item = snapshot.Item with
                {
                    Title = summary.Title,
                    Url = summary.Url ?? snapshot.Item.Url,
                    Status = summary.Status,
                    Priority = summary.Priority,
                    Archived = summary.Archived
                }
            });
        }

        return results;
    }
}

public sealed record BackendInitializationResult(
    bool Changed,
    IReadOnlyList<string> Actions);

public interface ITrackerDashboardBackend
{
    Task<DashboardSnapshot> GetDashboardAsync(
        TrackerConfig config,
        ArchiveScope archiveScope,
        CancellationToken cancellationToken);

    Task<EditableWorkItem> GetEditableAsync(
        TrackerConfig config,
        WorkItemId id,
        CancellationToken cancellationToken);
}

public interface ITrackerBackendRegistry
{
    ITrackerBackend Get(string backend);
}

public interface IExistingWorkItemAdoptionBackend
{
    Task<AdoptWorkItemResult> AdoptAsync(
        TrackerConfig config,
        string reference,
        AdoptWorkItemOptions options,
        CancellationToken cancellationToken);
}

public interface IWorkItemImportTargetBackend
{
    Task ValidateImportFieldsAsync(
        TrackerConfig config,
        string status,
        string? priority,
        CancellationToken cancellationToken);

    Task ArchiveImportedAsync(
        TrackerConfig config,
        WorkItemId id,
        CancellationToken cancellationToken);
}

public sealed class TrackerBackendRegistry(IEnumerable<ITrackerBackend> backends)
    : ITrackerBackendRegistry
{
    private readonly IReadOnlyDictionary<string, ITrackerBackend> backends = backends
        .ToDictionary(item => item.Name, StringComparer.OrdinalIgnoreCase);

    public ITrackerBackend Get(string backend)
    {
        if (backends.TryGetValue(backend, out var result))
        {
            return result;
        }

        throw new Errors.TrackerException(
            "BACKEND_UNSUPPORTED",
            $"Unsupported backend '{backend}'. Available backends: {string.Join(", ", backends.Keys.Order())}.",
            3);
    }
}
