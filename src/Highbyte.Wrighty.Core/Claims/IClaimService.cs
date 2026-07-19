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

    Task<ClaimResult> TryClaimAsync(TrackerConfig config, WorkItemId id,
        AgentExecutionContext agentContext, CancellationToken cancellationToken,
        string? expectedClaimToken);

    Task<ClaimResult> TakeoverAsync(
        TrackerConfig config,
        WorkItemId id,
        AgentExecutionContext claimantContext,
        string? currentClaimToken,
        CancellationToken cancellationToken);

    Task<ClaimResult> RenewAsync(
        TrackerConfig config,
        WorkItemId id,
        ClaimHandle claimHandle,
        string? workspacePath,
        string? sessionId,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException();

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

    Task RequeueAsync(
        TrackerConfig config,
        WorkItemId id,
        ClaimHandle claimHandle,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    Task<ClaimOwnershipResult> ValidateAsync(
        TrackerConfig config,
        WorkItemId id,
        ClaimHandle claimHandle,
        CancellationToken cancellationToken);

    Task<bool> IsOwnedByCurrentWorkerAsync(
        TrackerConfig config,
        WorkItemId id,
        CancellationToken cancellationToken);

    Task<ClaimOwnershipResult> GetOwnershipAsync(
        TrackerConfig config,
        WorkItemId id,
        CancellationToken cancellationToken);

    Task<AgentSessionRecord?> GetAgentSessionAsync(
        TrackerConfig config,
        WorkItemId id,
        CancellationToken cancellationToken) =>
        Task.FromResult<AgentSessionRecord?>(null);
}
