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
        RenewAsync(config, id, claimHandle, workspacePath, sessionId, branch: null,
            cancellationToken);

    Task<ClaimResult> RenewAsync(
        TrackerConfig config,
        WorkItemId id,
        ClaimHandle claimHandle,
        string? workspacePath,
        string? sessionId,
        string? branch,
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

    /// <summary>
    /// Records the outcome of the just-ended agent run onto the item's durable session record.
    /// Overwrite-only, best-effort, and backend-neutral: preserves the recorded address and only
    /// updates the run outcome, final message, and end time. The default is a no-op for backends
    /// that keep no durable session records.
    /// </summary>
    Task RecordRunOutcomeAsync(
        TrackerConfig config,
        WorkItemId id,
        RunOutcome outcome,
        string? finalMessage,
        DateTimeOffset endedAt,
        CancellationToken cancellationToken) =>
        Task.CompletedTask;

    /// <summary>
    /// Posts or overwrites the single marker-identified handover comment on the item's issue.
    /// Best-effort and backend-neutral; the default is a no-op for backends without a comment
    /// surface (Local Markdown uses the web dashboard instead).
    /// </summary>
    Task PostHandoverAsync(
        TrackerConfig config,
        Workers.HandoverContent content,
        CancellationToken cancellationToken) =>
        Task.CompletedTask;

    /// <summary>
    /// Trims any existing handover comment to a short "resolved" form when the item is requeued,
    /// archived, or its workspace is cleaned up, so stale instructions do not linger. No-op when no
    /// handover comment exists.
    /// </summary>
    Task ResolveHandoverAsync(
        TrackerConfig config,
        WorkItemId id,
        string reason,
        CancellationToken cancellationToken) =>
        Task.CompletedTask;

    /// <summary>
    /// Reads ownership and the recorded agent session together. The default composes the two
    /// separate reads; implementations that derive both from one underlying fetch should
    /// override it so operational views pay for the fetch once.
    /// </summary>
    async Task<ClaimStateReading> GetClaimStateAsync(
        TrackerConfig config,
        WorkItemId id,
        CancellationToken cancellationToken) =>
        new(await GetOwnershipAsync(config, id, cancellationToken),
            await GetAgentSessionAsync(config, id, cancellationToken));
}

public sealed record ClaimStateReading(
    ClaimOwnershipResult Ownership,
    AgentSessionRecord? Session);
