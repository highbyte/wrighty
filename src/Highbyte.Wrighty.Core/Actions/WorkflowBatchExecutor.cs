using Highbyte.Wrighty.Errors;

namespace Highbyte.Wrighty.Actions;

/// <summary>Storage hooks run outside mutation error handling: a failed journal write must stop execution.</summary>
public interface IWorkflowBatchProgress
{
    void Starting(string id);
    void Completed(WorkflowBatchItemResult result);
}

public sealed record WorkflowBatchExecution(IReadOnlyList<WorkflowBatchItemResult> Items, string? StopCode);

/// <summary>
/// The single batch loop for every surface. Callers supply their reviewed selection and per-item
/// revalidation; this owns sequencing, conflict handling, cancellation, and partial outcomes.
/// </summary>
public static class WorkflowBatchExecutor
{
    public static async Task<WorkflowBatchExecution> ExecuteAsync(
        IReadOnlyList<string> itemIds,
        Func<string, CancellationToken, Task<WorkflowActionResult?>> execute,
        CancellationToken cancellationToken,
        IWorkflowBatchProgress? progress = null,
        Action<Exception>? onFailure = null)
    {
        // Do not let changes to the caller's selection add work after execution has started.
        var frozen = itemIds.ToArray();
        if (frozen.Length > WorkflowBatchPolicy.MaximumCandidates ||
            frozen.Any(string.IsNullOrWhiteSpace) || frozen.Distinct(StringComparer.Ordinal).Count() != frozen.Length)
            throw new TrackerException("BATCH_SELECTION_INVALID", "A batch requires at most 100 distinct item IDs.", 2);
        List<WorkflowBatchItemResult> items = [];
        string? stopCode = null;
        foreach (var id in frozen)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                stopCode = "BATCH_CANCELLED";
                break;
            }
            progress?.Starting(id);
            var item = await ApplyAsync(id, execute, cancellationToken, onFailure);
            items.Add(item);
            progress?.Completed(item);
            if (item.Outcome != "failed") continue;
            stopCode = item.Code;
            break;
        }
        return Complete(frozen, items, stopCode);
    }

    internal static WorkflowBatchExecution Complete(IReadOnlyList<string> ids,
        IReadOnlyList<WorkflowBatchItemResult> items, string? stopCode)
    {
        var processed = items.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        return new([.. items, .. ids.Where(id => !processed.Contains(id))
            .Select(id => new WorkflowBatchItemResult(id, "unprocessed", stopCode))], stopCode);
    }

    private static async Task<WorkflowBatchItemResult> ApplyAsync(string id,
        Func<string, CancellationToken, Task<WorkflowActionResult?>> execute,
        CancellationToken cancellationToken, Action<Exception>? onFailure)
    {
        try
        {
            return new(id, "applied", Applied: await execute(id, cancellationToken));
        }
        catch (TrackerException exception) when (WorkflowBatchPolicy.IsItemConflict(exception.Code))
        {
            return new(id, "skipped", exception.Code);
        }
        catch (Exception exception)
        {
            onFailure?.Invoke(exception);
            var code = exception switch
            {
                TrackerException trackerError => trackerError.Code,
                OperationCanceledException => "BATCH_CANCELLED",
                _ => "BATCH_EXECUTION_FAILED"
            };
            // A failure can follow a backend write. Only known pre-mutation refusals are definite.
            return new(id, "failed", code, MutationMayHaveApplied: code != "BATCH_CONFIG_CHANGED");
        }
    }
}
