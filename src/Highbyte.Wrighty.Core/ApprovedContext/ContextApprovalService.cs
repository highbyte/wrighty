using Highbyte.Wrighty.Configuration;
using Highbyte.Wrighty.Errors;
using Highbyte.Wrighty.Models;

namespace Highbyte.Wrighty.ApprovedContext;

/// <summary>
/// One application boundary for context-approval inspection and mutation. CLI, web, and workflow
/// entry points use this service so approval cycling and post-mutation diagnostics cannot drift.
/// </summary>
public interface IContextApprovalService
{
    Task<ExecutionContextResult> InspectAsync(
        TrackerConfig config,
        WorkItemId id,
        CancellationToken cancellationToken);

    Task<ExecutionContextResult> ApproveAsync(
        TrackerConfig config,
        WorkItemId id,
        CancellationToken cancellationToken);

    Task<ContextApprovalInvalidationDisposition> InvalidateAsync(
        TrackerConfig config,
        WorkItemId id,
        CancellationToken cancellationToken);
}

public enum ContextApprovalInvalidationDisposition
{
    ResetToNeedsReview,
    PreservedNewerApproval
}

/// <summary>
/// Decides whether a delayed edit workflow may still revoke approval. GitHub Actions can start
/// after an operator has already reapproved the edited issue, so the workflow must inspect the
/// current revision before mutating the project field.
/// </summary>
public static class ContextApprovalInvalidation
{
    public const string UnsafeCode = "CONTEXT_APPROVAL_INVALIDATION_UNSAFE";

    public static ContextApprovalInvalidationDisposition Decide(ExecutionContextResult current)
    {
        if (current.Code is ExecutionContextResult.Codes.BaseNeedsReview
            or ExecutionContextResult.Codes.ApprovalUnavailable)
        {
            return ContextApprovalInvalidationDisposition.ResetToNeedsReview;
        }

        if (current.IsApproved || current.EffectiveDiagnostics?.Approval.IsApproved == true)
        {
            return ContextApprovalInvalidationDisposition.PreservedNewerApproval;
        }

        throw new TrackerException(
            UnsafeCode,
            $"Context approval could not be invalidated safely because the current context " +
            $"inspection returned '{current.Code ?? "an unknown result"}'. Retry after the " +
            "conversation can be read completely.",
            10);
    }
}

public sealed class ContextApprovalService(
    TrackerService tracker,
    Func<TrackerConfig, IExecutionContextProvider?> providers) : IContextApprovalService
{
    public Task<ExecutionContextResult> InspectAsync(
        TrackerConfig config,
        WorkItemId id,
        CancellationToken cancellationToken) =>
        Provider(config).GetAsync(
            config,
            id,
            ContextReadPurpose.Diagnostics,
            config.EffectiveWorker.EffectiveContext.ToLimits(),
            cancellationToken);

    public async Task<ExecutionContextResult> ApproveAsync(
        TrackerConfig config,
        WorkItemId id,
        CancellationToken cancellationToken)
    {
        await tracker.CycleContextApprovalAsync(config, id, cancellationToken);
        return await InspectAsync(config, id, cancellationToken);
    }

    public async Task<ContextApprovalInvalidationDisposition> InvalidateAsync(
        TrackerConfig config,
        WorkItemId id,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(config.Backend, "github", StringComparison.OrdinalIgnoreCase))
        {
            throw new TrackerException(
                "CONTEXT_APPROVAL_UNSUPPORTED",
                $"The '{config.Backend}' backend has no context approval surface to invalidate.",
                3);
        }

        var current = await InspectAsync(config, id, cancellationToken);
        var disposition = ContextApprovalInvalidation.Decide(current);
        if (disposition == ContextApprovalInvalidationDisposition.ResetToNeedsReview)
        {
            await tracker.InvalidateContextApprovalAsync(config, id, cancellationToken);
        }

        return disposition;
    }

    private IExecutionContextProvider Provider(TrackerConfig config) =>
        providers(config) ?? throw new TrackerException(
            ExecutionContextResult.Codes.Unsupported,
            $"The '{config.Backend}' backend cannot assemble an approved context.",
            3);
}
