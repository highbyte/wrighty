using System.CommandLine;
using Highbyte.Wrighty.Configuration;
using Highbyte.Wrighty.Workers;

namespace Highbyte.Wrighty.Cli;

public sealed partial class CliApplication
{
    private Command BuildWorkersCommand()
    {
        var json = JsonOption();
        var item = new Option<string?>("--item") { Description = "Assess whether each observed worker could pick up this item." };
        var command = new Command("workers", "Inspect registered local workers without starting or controlling them");
        command.Options.Add(json);
        command.Options.Add(item);
        command.SetAction((parsed, cancellationToken) => ExecuteAsync(parsed.GetValue(json),
            config => InspectWorkersAsync(config, parsed.GetValue(item), parsed.GetValue(json), cancellationToken),
            cancellationToken));
        return command;
    }

    private async Task InspectWorkersAsync(TrackerConfig config, string? item, bool json,
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
        await writer.WriteWorkersAsync(new(snapshot.ObservedAt, snapshot.ConfigurationPathHash,
            snapshot.Coverage, snapshot.Detail, revision, state?.Item.Id.Value, workers), json);
    }
}
