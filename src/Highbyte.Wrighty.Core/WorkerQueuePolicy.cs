using Highbyte.Wrighty.Configuration;
using Highbyte.Wrighty.Models;

namespace Highbyte.Wrighty;

public readonly record struct WorkerQueueRuleResult(WorkItemPatch Patch, bool CycleContextApproval);

public static class WorkerQueuePolicy
{
    public static WorkerQueueRuleResult Apply(TrackerConfig config, string? currentStatus,
        bool? contextApproval, WorkItemPatch patch)
    {
        if (!config.EffectiveWorker.UseWorkerQueue || !patch.Status.IsSpecified ||
            patch.AutomaticExecutionAllowed.IsSpecified)
            return new(patch, false);
        var wasQueued = string.Equals(currentStatus, config.DefaultPickFrom, StringComparison.OrdinalIgnoreCase);
        var willQueue = string.Equals(patch.Status.Value, config.DefaultPickFrom, StringComparison.OrdinalIgnoreCase);
        if (wasQueued == willQueue)
            return new(patch, false);
        return new(patch with { AutomaticExecutionAllowed = OptionalValue<bool>.From(willQueue) },
            willQueue && contextApproval is not null);
    }
}
