using System.CommandLine;
using Highbyte.Wrighty.Actions;
using Highbyte.Wrighty.Configuration;
using Highbyte.Wrighty.Errors;
using Highbyte.Wrighty.Models;

namespace Highbyte.Wrighty.Cli;

public sealed partial class CliApplication
{
    private Command BuildBatchCommand()
    {
        var command = new Command("batch", "Preview, execute, or inspect a frozen workflow batch");
        command.Subcommands.Add(BuildBatchPreviewCommand());
        command.Subcommands.Add(BuildBatchRecordCommand(execute: false));
        command.Subcommands.Add(BuildBatchRecordCommand(execute: true));
        return command;
    }

    private WorkflowBatchStore BatchStore => new(storageLocations.WorkflowBatchesRoot);

    private Command BuildBatchPreviewCommand()
    {
        var action = new Argument<string>("action-name");
        var ids = new Option<string[]>("--id") { Description = "Explicit item ID; repeat to select several items." };
        var status = new Option<string?>("--status") { Description = "Select active items in this workflow status." };
        var fields = FieldOption("Filter the status selection by name=value; repeat for AND semantics.");
        var json = JsonOption();
        var command = new Command("preview", "Freeze up to 100 eligible items for five minutes; no items are changed");
        command.Arguments.Add(action);
        command.Options.Add(ids);
        command.Options.Add(status);
        command.Options.Add(fields);
        command.Options.Add(json);
        command.SetAction((parsed, token) => ExecuteAsync(parsed.GetValue(json), async config =>
        {
            WorkflowActionService.EnsureSupported(parsed.GetValue(action)!);
            var selection = await SelectBatchItemsAsync(config, parsed.GetValue(ids) ?? [],
                parsed.GetValue(status), parsed.GetValue(fields) ?? [], token);
            var preview = await new WorkflowBatchService(tracker, BatchStore).PreviewAsync(
                config, parsed.GetValue(action)!, selection, token);
            await writer.WriteBatchAsync(new(preview, "preview", []), parsed.GetValue(json));
        }, token));
        return command;
    }

    private async Task<IReadOnlyList<WorkItemId>> SelectBatchItemsAsync(TrackerConfig config,
        string[] ids, string? status, string[] fields, CancellationToken cancellationToken)
    {
        if (tracker.Backend(config) is not IWorkflowActionBackend)
            throw new TrackerException("NOT_SUPPORTED", "Batch workflow actions require Local Markdown.", 3);
        if ((ids.Length == 0 && string.IsNullOrWhiteSpace(status)) ||
            (ids.Length > 0 && (status is not null || fields.Length > 0)))
            throw new TrackerException("ARGUMENT_INVALID", "Select --id values or --status with optional --field filters.", 2);
        if (ids.Length > 0) return ids.Select(id => tracker.ResolveId(config, id)).ToArray();
        var items = await tracker.ListAsync(config, new ListWorkItemsRequest(status, null, ArchiveScope.Active,
            ParseFields(fields, allowDeletion: false).ToDictionary(pair => pair.Key, pair => pair.Value!, StringComparer.Ordinal)),
            cancellationToken);
        return items.Select(item => item.Id).ToArray();
    }

    private Command BuildBatchRecordCommand(bool execute)
    {
        var id = new Argument<string>("preview-id");
        var yes = new Option<bool>("--yes") { Description = "Authorize exactly the persisted preview's items and action." };
        var json = JsonOption();
        var command = new Command(execute ? "execute" : "show", execute
            ? "Execute a reviewed preview once, or return its recorded result"
            : "Inspect a preview or its recorded result without executing items");
        command.Arguments.Add(id);
        command.Options.Add(json);
        if (execute) command.Options.Add(yes);
        command.SetAction((parsed, token) => RunBatchRecordAsync(
            new(parsed.GetValue(id)!, execute, parsed.GetValue(yes), parsed.GetValue(json)), token));
        return command;
    }

    private sealed record BatchRecordRequest(string Id, bool Execute, bool Yes, bool Json);

    private async Task<int> RunBatchRecordAsync(BatchRecordRequest request, CancellationToken token)
    {
        var resultExit = 0;
        var exit = await ExecuteAsync(request.Json, async config =>
        {
            var store = BatchStore;
            var record = await store.ReadAsync(config, request.Id, token);
            if (request.Execute)
            {
                if (!request.Yes)
                    throw new TrackerException("BATCH_CONFIRMATION_REQUIRED",
                        "Review batch show output, then pass --yes to authorize exactly that preview.", 2);
                record = await new WorkflowBatchService(tracker, store).ExecuteAsync(config,
                    request.Id, ct => configLoader.LoadAsync(workingDirectory, ct), token);
                resultExit = BatchExitCode(record);
            }
            await writer.WriteBatchAsync(record, request.Json);
        }, token);
        return exit == 0 ? resultExit : exit;
    }

    private static int BatchExitCode(WorkflowBatchRecord record)
    {
        if (record.StopCode == "BATCH_CANCELLED") return 130;
        return record.HasIssues ? 6 : 0;
    }
}
