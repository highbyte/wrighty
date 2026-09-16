using System.CommandLine;
using Highbyte.Wrighty.Configuration;
using Highbyte.Wrighty.Errors;
using Highbyte.Wrighty.Workers;

namespace Highbyte.Wrighty.Cli;

public sealed partial class CliApplication
{
    private Command BuildWorkersCommand()
    {
        var json = JsonOption();
        var item = new Option<string?>("--item") { Description = "Assess whether each observed worker could pick up this item." };
        var command = new Command("workers", "Inspect registered local workers or request cooperative control");
        command.Options.Add(json);
        command.Options.Add(item);
        command.Subcommands.Add(BuildWorkerShowCommand());
        command.Subcommands.Add(BuildWorkerStopCommand(WorkerStopMode.Drain));
        command.Subcommands.Add(BuildWorkerStopCommand(WorkerStopMode.Interrupt));
        command.SetAction((parsed, cancellationToken) => ExecuteAsync(parsed.GetValue(json),
            config => InspectWorkersAsync(config, parsed.GetValue(item), null, parsed.GetValue(json), cancellationToken),
            cancellationToken));
        return command;
    }

    private Command BuildWorkerShowCommand()
    {
        var run = new Argument<string>("run-id");
        var json = JsonOption();
        var item = new Option<string?>("--item") { Description = "Assess whether this worker could pick up this item." };
        var command = new Command("show", "Inspect one registered worker run without starting or controlling it");
        command.Arguments.Add(run);
        command.Options.Add(json);
        command.Options.Add(item);
        command.SetAction((parsed, token) => ExecuteAsync(parsed.GetValue(json),
            config => InspectWorkersAsync(config, parsed.GetValue(item), parsed.GetValue(run), parsed.GetValue(json), token),
            token));
        return command;
    }

    private async Task InspectWorkersAsync(TrackerConfig config, string? item, string? run, bool json,
        CancellationToken cancellationToken)
    {
        var discovery = await ReadWorkersAsync(config, item, cancellationToken);
        if (run is not null)
        {
            var matches = discovery.LocalWorkers.Where(entry => entry.Instance.RunId == run).ToArray();
            if (matches.Length == 0)
                throw MissingWorker(discovery.Coverage);
            discovery = discovery with { LocalWorkers = matches };
        }
        await writer.WriteWorkersAsync(discovery, json);
    }

    private Command BuildWorkerStopCommand(WorkerStopMode mode)
    {
        var run = new Argument<string>("run-id");
        var json = JsonOption();
        var yes = new Option<bool>("--yes") { Description = "Authorize control of this exact run." };
        var command = new Command(mode.ToString().ToLowerInvariant(), mode == WorkerStopMode.Drain
            ? "Close intake and finish the current item before exiting"
            : "Interrupt the current agent process tree and finalize the item before exiting");
        command.Arguments.Add(run);
        command.Options.Add(json);
        command.Options.Add(yes);
        command.SetAction((parsed, token) => ExecuteAsync(parsed.GetValue(json), async config =>
        {
            if (!parsed.GetValue(yes))
                throw new TrackerException("WORKER_CONFIRMATION_REQUIRED",
                    "Inspect the run, then pass --yes to authorize the requested control.", 2);
            var path = config.SourcePath ?? Path.Combine(workingDirectory, TrackerConfigLoader.FileName);
            var snapshot = await workerInstances.InspectAsync(path, token);
            var status = snapshot.Workers.SingleOrDefault(entry => entry.Instance.RunId == parsed.GetValue(run));
            if (status is null) throw MissingWorker(snapshot.Coverage);
            var instance = status.Instance;
            var result = await workerInstances.RequestStopAsync(path,
                new(instance.RunId, instance.ProcessId, instance.ProcessStartIdentity, instance.HostKind), mode, token);
            if (!result.Accepted) throw new TrackerException(result.Code, result.Message, 7);
            await writer.WriteWorkerControlAsync(instance.RunId, mode, result, parsed.GetValue(json));
        }, token));
        return command;
    }

    private static TrackerException MissingWorker(string coverage) => coverage == "complete"
        ? new("WORKER_NOT_RUNNING", "The run is no longer registered in this configuration. Inspect the item and owning terminal for its outcome.", 7)
        : new("WORKER_CONTROL_UNAVAILABLE", "The run could not be found in incomplete or unavailable registry evidence.", 7);

    private async Task<WorkerDiscovery> ReadWorkersAsync(TrackerConfig config, string? item,
        CancellationToken cancellationToken)
    {
        var configurationPath = config.SourcePath ?? Path.Combine(workingDirectory, TrackerConfigLoader.FileName);
        var snapshot = await workerInstances.InspectAsync(configurationPath, cancellationToken);
        var revision = await StatusConfigurationRevisionAsync(config, cancellationToken);
        var state = item is null ? null : await tracker.GetOperationalAsync(config,
            tracker.ResolveId(config, item), cancellationToken);
        var workers = new List<WorkerDiscoveryEntry>();
        foreach (var status in snapshot.Workers)
        {
            WorkerPickupAssessment? pickup = null;
            if (state is not null)
                pickup = workerService is null
                    ? new(status.Instance.RunId, state.Item.Id.Value, "unknown", "ASSESSMENT_UNAVAILABLE",
                        "Worker selection assessment is unavailable here.")
                    : await workerService.AssessPickupAsync(config, status, state, revision,
                        snapshot.ObservedAt, cancellationToken);
            workers.Add(WorkerDiscoveryEntry.From(status, revision, pickup));
        }
        return new(snapshot.ObservedAt, snapshot.ConfigurationPathHash,
            snapshot.Coverage, snapshot.Detail, revision, state?.Item.Id.Value, workers);
    }
}
