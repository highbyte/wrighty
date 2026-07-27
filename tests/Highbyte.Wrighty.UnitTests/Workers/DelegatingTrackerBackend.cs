using Highbyte.Wrighty.Addressing;
using Highbyte.Wrighty.AgentContext;
using Highbyte.Wrighty.ApprovedContext;
using Highbyte.Wrighty.Backends;
using Highbyte.Wrighty.Claims;
using Highbyte.Wrighty.Configuration;
using Highbyte.Wrighty.Models;

namespace Highbyte.Wrighty.UnitTests.Workers;

/// <summary>
/// A tracker backend that forwards everything to a real one, so a test can override the single
/// operation it cares about.
///
/// It exists because <see cref="ITrackerBackend"/> requires seventeen members before a test can
/// change one of them, and a decorator written inline is almost entirely delegation nobody reads.
/// Only the required members are declared here; everything else on the interface has a default
/// implementation, and a subclass needing one of those declares it directly.
/// </summary>
internal abstract class DelegatingTrackerBackend(ITrackerBackend inner) : ITrackerBackend
{
    protected ITrackerBackend Inner { get; } = inner;

    public virtual string Name => Inner.Name;

    public virtual IWorkItemAddressResolver AddressResolver => Inner.AddressResolver;

    public virtual Task<BackendInitializationResult> InitializeAsync(
        TrackerConfig config, bool checkOnly, CancellationToken cancellationToken) =>
        Inner.InitializeAsync(config, checkOnly, cancellationToken);

    public virtual Task<IReadOnlyList<WorkItemSummary>> ListAsync(
        TrackerConfig config, ListWorkItemsRequest request, CancellationToken cancellationToken) =>
        Inner.ListAsync(config, request, cancellationToken);

    public virtual Task<WorkItemDetail?> GetAsync(
        TrackerConfig config, WorkItemId id, CancellationToken cancellationToken) =>
        Inner.GetAsync(config, id, cancellationToken);

    public virtual Task<CreateWorkItemResult> CreateAsync(
        TrackerConfig config, CreateWorkItemOperation operation, CancellationToken cancellationToken) =>
        Inner.CreateAsync(config, operation, cancellationToken);

    public virtual Task<UpdateWorkItemResult> UpdateAsync(
        TrackerConfig config, WorkItemId id, UpdateWorkItemOperation operation,
        CancellationToken cancellationToken) =>
        Inner.UpdateAsync(config, id, operation, cancellationToken);

    public virtual Task<ClaimResult> TryClaimAsync(
        TrackerConfig config, WorkItemId id, AgentExecutionContext agentContext,
        CancellationToken cancellationToken) =>
        Inner.TryClaimAsync(config, id, agentContext, cancellationToken);

    public virtual Task<ClaimResult> TryClaimAsync(
        TrackerConfig config, WorkItemId id, AgentExecutionContext agentExecutionContext,
        CancellationToken cancellationToken, string? expectedClaimToken) =>
        Inner.TryClaimAsync(config, id, agentExecutionContext, cancellationToken, expectedClaimToken);

    public virtual Task<ClaimResult> TakeoverAsync(
        TrackerConfig config, WorkItemId id, AgentExecutionContext claimantContext,
        string? currentClaimToken, CancellationToken cancellationToken) =>
        Inner.TakeoverAsync(config, id, claimantContext, currentClaimToken, cancellationToken);

    public virtual Task<ClaimResult> RenewClaimAsync(
        TrackerConfig config, WorkItemId id, ClaimHandle claimHandle, string? workspacePath,
        string? sessionId, CancellationToken cancellationToken) =>
        Inner.RenewClaimAsync(config, id, claimHandle, workspacePath, sessionId, cancellationToken);

    public virtual Task<ClaimResult> RenewClaimAsync(
        TrackerConfig config, WorkItemId id, ClaimHandle claimHandle, string? workspacePath,
        string? sessionId, string? branch, CancellationToken cancellationToken) =>
        Inner.RenewClaimAsync(
            config, id, claimHandle, workspacePath, sessionId, branch, cancellationToken);

    public virtual Task<ClaimOwnershipResult> GetClaimOwnershipAsync(
        TrackerConfig config, WorkItemId id, CancellationToken cancellationToken) =>
        Inner.GetClaimOwnershipAsync(config, id, cancellationToken);

    public virtual Task<AgentSessionRecord?> GetAgentSessionAsync(
        TrackerConfig config, WorkItemId id, CancellationToken cancellationToken) =>
        Inner.GetAgentSessionAsync(config, id, cancellationToken);

    public virtual Task RecordSessionContextAsync(
        TrackerConfig config, WorkItemId id, SessionContextMetadata context,
        CancellationToken cancellationToken) =>
        Inner.RecordSessionContextAsync(config, id, context, cancellationToken);

    public virtual Task ReleaseAsync(
        TrackerConfig config, WorkItemId id, ClaimHandle claimHandle, bool overrideClaimant,
        CancellationToken cancellationToken) =>
        Inner.ReleaseAsync(config, id, claimHandle, overrideClaimant, cancellationToken);

    public virtual Task ReleaseAsync(
        TrackerConfig config, WorkItemId id, CancellationToken cancellationToken) =>
        Inner.ReleaseAsync(config, id, cancellationToken);

    public virtual Task<ArchiveWorkItemResult> ArchiveAsync(
        TrackerConfig config, WorkItemId id, CancellationToken cancellationToken) =>
        Inner.ArchiveAsync(config, id, cancellationToken);

    public virtual Task<ArchiveWorkItemResult> ArchiveAsync(
        TrackerConfig config, WorkItemId id, ClaimHandle claimHandle,
        CancellationToken cancellationToken) =>
        Inner.ArchiveAsync(config, id, claimHandle, cancellationToken);

    public virtual Task<ArchiveWorkItemResult> UnarchiveAsync(
        TrackerConfig config, WorkItemId id, CancellationToken cancellationToken) =>
        Inner.UnarchiveAsync(config, id, cancellationToken);
}
