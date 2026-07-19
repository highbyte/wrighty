using Highbyte.Wrighty.Addressing;
using Highbyte.Wrighty.AgentContext;
using Highbyte.Wrighty.Claims;
using Highbyte.Wrighty.Configuration;
using Highbyte.Wrighty.Models;

namespace Highbyte.Wrighty.Backends;

public interface ITrackerBackend
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

    Task<WorkItemDetail?> GetAsync(
        TrackerConfig config,
        WorkItemId id,
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
