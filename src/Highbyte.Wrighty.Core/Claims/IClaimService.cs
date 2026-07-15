using Highbyte.Wrighty.Configuration;
using Highbyte.Wrighty.AgentContext;
using Highbyte.Wrighty.Models;

namespace Highbyte.Wrighty.Claims;

public interface IClaimService
{
    Task<ClaimResult> TryClaimAsync(
        TrackerConfig config,
        WorkItemId id,
        AgentExecutionContext agentContext,
        CancellationToken cancellationToken);

    Task ReleaseAsync(
        TrackerConfig config,
        WorkItemId id,
        CancellationToken cancellationToken);

    Task<bool> IsOwnedByCurrentWorkerAsync(
        TrackerConfig config,
        WorkItemId id,
        CancellationToken cancellationToken);

    Task<ClaimOwnershipResult> GetOwnershipAsync(
        TrackerConfig config,
        WorkItemId id,
        CancellationToken cancellationToken);
}
