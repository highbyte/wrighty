using Highbyte.Wrighty.Claims;
using Highbyte.Wrighty.Configuration;

namespace Highbyte.Wrighty.Models;

/// <summary>
/// Defines the narrow boundary where permanent deletion is safer and clearer than archiving.
/// Backends must repeat this check inside their mutation lock; this policy also drives previews.
/// </summary>
public static class WorkItemDeletionPolicy
{
    public static bool HasProcessingHistory(AgentSessionRecord? session) =>
        session is not null &&
        (session.HasAddress ||
         session.HasRunOutcome ||
         session.Dispatch is not null ||
         session.Context is not null ||
         session.LastReport is not null ||
         session.Continuation is not null ||
         session.Selection is not null);

    public static WorkItemDeletionEligibility Evaluate(
        TrackerConfig config,
        WorkItemDetail item,
        WorkItemClaimSummary claim,
        bool hasProcessingHistory)
    {
        if (item.Archived)
            return Refuse("Archived items are retained as history and cannot be deleted.");

        if (claim.State != ClaimOwnershipState.Unclaimed)
            return Refuse("Release the current claim before deleting this item.");

        if (hasProcessingHistory)
            return Refuse("This item has processing history. Archive it instead.");

        if (!string.IsNullOrWhiteSpace(item.DispatchState))
            return Refuse("This item has queued or pending worker activity. Archive it instead.");

        var backlog = WorkflowStatusPolicy.InferBacklogStatus(
            config,
            config.LocalMarkdown?.Statuses ?? []);
        if (!Matches(item.Status, config.DefaultPickFrom) && !Matches(item.Status, backlog))
        {
            return Refuse(
                "Only unprocessed items in the backlog or worker queue can be deleted.");
        }

        return new WorkItemDeletionEligibility(true);
    }

    private static WorkItemDeletionEligibility Refuse(string reason) => new(false, reason);

    private static bool Matches(string? left, string? right) =>
        !string.IsNullOrWhiteSpace(left) &&
        !string.IsNullOrWhiteSpace(right) &&
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
}
