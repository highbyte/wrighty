using Highbyte.Wrighty.Configuration;
using Highbyte.Wrighty.Errors;
using Highbyte.Wrighty.Models;

namespace Highbyte.Wrighty.Actions;

public sealed class WorkflowBatchService(TrackerService tracker, WorkflowBatchStore store)
{
    public async Task<WorkflowBatchPreview> PreviewAsync(TrackerConfig config, string action,
        IReadOnlyList<WorkItemId> selection, CancellationToken cancellationToken)
    {
        WorkflowActionService.EnsureSupported(action);
        if (tracker.Backend(config) is not IWorkflowActionBackend)
            throw new TrackerException("NOT_SUPPORTED", "This backend does not support batch workflow execution.", 3);
        var ids = selection.Distinct().OrderBy(id => id.Value, StringComparer.Ordinal).ToArray();
        List<WorkflowBatchCandidate> candidates = [];
        var eligible = 0;
        foreach (var id in ids)
        {
            try
            {
                var state = await tracker.GetOperationalAsync(config, id, cancellationToken);
                var descriptor = WorkflowActionService.Select(config, state, action);
                if (descriptor.Availability != "available") continue;
                eligible++;
                if (candidates.Count < WorkflowBatchPolicy.MaximumCandidates)
                    candidates.Add(new(id.Value, state.Item.Title, WorkflowActionService.Version(config, state),
                        WorkflowActionService.Describe(state), descriptor.Description));
            }
            catch (TrackerException exception) when (WorkflowBatchPolicy.IsItemConflict(exception.Code))
            {
                // A deleted or newly unavailable item is outside the eligible frozen set.
            }
        }
        return await store.CreateAsync(config, action, candidates, ids.Length, eligible, cancellationToken);
    }

    public Task<WorkflowBatchRecord> ExecuteAsync(TrackerConfig config, string previewId,
        Func<CancellationToken, Task<TrackerConfig>> reload, CancellationToken cancellationToken) =>
        store.ExecuteAsync(config, previewId, async (preview, candidate, token) =>
        {
            var currentConfig = await reload(token);
            if (WorkflowBatchPolicy.ConfigurationVersion(currentConfig) != preview.ConfigurationVersion)
                throw new TrackerException("BATCH_CONFIG_CHANGED", "Configuration changed; review a new batch.", 6);
            return await new WorkflowActionService(tracker).ExecuteAsync(currentConfig,
                new WorkItemId(candidate.Id), preview.Action, candidate.StateVersion, token);
        }, cancellationToken);
}
