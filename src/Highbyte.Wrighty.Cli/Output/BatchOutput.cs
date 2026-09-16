using Highbyte.Wrighty.Actions;

namespace Highbyte.Wrighty.Cli.Output;

public sealed partial class OutputWriter
{
    public async Task WriteBatchAsync(WorkflowBatchRecord record, bool json)
    {
        if (json)
        {
            await WriteJsonAsync(new { schemaVersion = 1, result = record });
            return;
        }
        var preview = record.Preview;
        await output.WriteLineAsync($"Batch {preview.Id}: {preview.Action}; {record.State}.");
        await output.WriteLineAsync($"Selected {preview.SelectedCount}; eligible {preview.EligibleCount}; frozen {preview.Candidates.Count}. " +
            $"Preview expires {preview.ExpiresAt:O}. No worker is started.");
        foreach (var candidate in preview.Candidates)
        {
            await output.WriteLineAsync($"  {candidate.Id}: {candidate.Title}");
            await output.WriteLineAsync($"    {candidate.Consequence}");
        }
        if (record.State == "preview")
            await output.WriteLineAsync($"After review: wrighty batch execute {preview.Id} --yes");
        foreach (var item in record.Items)
            await output.WriteLineAsync($"  {item.Id}: {item.Outcome}{(item.Code is null ? "" : $" ({item.Code})")}" +
                (item.MutationMayHaveApplied ? "; mutation may have applied — inspect before retrying." : "."));
        if (record.StopCode is not null)
            await output.WriteLineAsync($"Stopped: {record.StopCode}. Remaining items were not processed.");
    }
}
