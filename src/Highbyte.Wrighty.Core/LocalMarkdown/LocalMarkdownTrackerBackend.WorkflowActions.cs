using Highbyte.Wrighty.Actions;
using Highbyte.Wrighty.Configuration;
using Highbyte.Wrighty.Models;

namespace Highbyte.Wrighty.LocalMarkdown;

public sealed partial class LocalMarkdownTrackerBackend
{
    public async Task<WorkflowActionResult> ExecuteWorkflowActionAsync(
        TrackerConfig config, WorkItemId id, string action, string expectedVersion,
        CancellationToken cancellationToken)
    {
        WorkflowActionService.EnsureSupported(action);
        EnsureStore(config);
        var paths = Paths(config);
        await using var storeLock = await LocalStoreLock.AcquireAsync(paths.Root, cancellationToken);
        await PauseAfterLockAsync("workflow-action", cancellationToken);
        var document = await RequiredUnlockedAsync(config, id, cancellationToken);
        var runtime = await LocalRuntimeStateStore.LoadUnlockedAsync(paths.Root, cancellationToken);
        var installation = await identityProvider.GetInstallationIdAsync(cancellationToken);
        var before = WorkflowActionService.Operational(config, Snapshot(document, runtime, installation, clock.UtcNow));
        WorkflowActionService.Validate(config, before, action, expectedVersion);
        if (action == "resume")
        {
            await QueuePausedUnlockedAsync(config, id, paths, document, cancellationToken);
            runtime = await LocalRuntimeStateStore.LoadUnlockedAsync(paths.Root, cancellationToken);
        }
        else
        {
            var target = action == "queue" ? config.DefaultPickFrom
                : WorkflowStatusPolicy.InferBacklogStatus(config, config.LocalMarkdown!.Statuses)!;
            var patch = WorkerQueuePolicy.Apply(config, document.Status, null, WorkItemPatch.StatusOnly(target)).Patch;
            if (patch.AutomaticExecutionAllowed is { IsSpecified: true, Value: false })
                patch = patch with { DispatchState = OptionalValue<string?>.From(null) };
            WorkItemPatchValidator.Validate(patch, supportedAgentIds);
            List<string> changed = [];
            ApplyPatch(config, document, new(patch, config.ShouldArchiveStatus(target)), changed);
            var originalPath = document.Path;
            document.UpdatedAt = clock.UtcNow;
            document.Path = CanonicalPath(config, document);
            await WriteUnlockedAsync(document, originalPath, cancellationToken);
        }
        var after = WorkflowActionService.Operational(config, Snapshot(document, runtime, installation, clock.UtcNow));
        return new(id.Value, action, "applied", clock.UtcNow, WorkflowActionService.Version(config, after),
            WorkflowActionService.Describe(before), WorkflowActionService.Describe(after));
    }
}
