using Highbyte.Wrighty.Actions;

namespace Highbyte.Wrighty.Web;

/// <summary>Adapts Board display data and its accepted-request lifetime to the shared Core loop.</summary>
public static class BoardBatchExecution
{
    public static async Task<BoardBatchResult> ExecuteAsync(BoardBatchIntent intent,
        Func<BoardBatchCandidate, CancellationToken, Task<WorkflowActionResult?>> execute,
        Action<Exception>? onFailure = null)
    {
        var candidates = intent.Candidates.ToDictionary(candidate => candidate.Id, StringComparer.Ordinal);
        // A browser disconnect does not cancel an already accepted bounded operation.
        var result = await WorkflowBatchExecutor.ExecuteAsync(
            intent.Candidates.Select(candidate => candidate.Id).ToArray(),
            (id, token) => execute(candidates[id], token), CancellationToken.None, onFailure: onFailure);
        return new(intent.Id, intent.Action, DateTimeOffset.UtcNow,
            result.Items.Select(item => new BoardBatchItemResult(item.Id, candidates[item.Id].DisplayId,
                item.Outcome == "applied", item.Outcome == "skipped", Reason(item),
                item.Outcome == "unprocessed", item.MutationMayHaveApplied)).ToArray(),
            result.StopCode is null ? null : $"The batch stopped because Wrighty could not continue safely ({result.StopCode}).");
    }

    private static string? Reason(WorkflowBatchItemResult item) => item.Outcome switch
    {
        "applied" => null,
        "unprocessed" => "Not processed because the batch stopped.",
        "skipped" when item.Code == "BATCH_ITEM_INELIGIBLE" => "No longer eligible for this action.",
        "skipped" => "The item changed before Wrighty could apply the action.",
        _ when item.MutationMayHaveApplied =>
            $"The action may have applied ({item.Code}). Inspect this item before retrying.",
        _ => $"Wrighty could not complete this item ({item.Code})."
    };
}
