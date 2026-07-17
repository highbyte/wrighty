using Highbyte.Wrighty.Configuration;
using Highbyte.Wrighty.Claims;
using Highbyte.Wrighty.Models;

namespace Highbyte.Wrighty.Backends;

public interface IWorkItemBackend
{
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
        WorkItemPatch patch,
        CancellationToken cancellationToken);

    Task<UpdateWorkItemResult> UpdateAsync(TrackerConfig config, WorkItemId id,
        WorkItemPatch patch, ClaimHandle claimHandle, CancellationToken cancellationToken) =>
        UpdateAsync(config, id, patch, cancellationToken);
}
