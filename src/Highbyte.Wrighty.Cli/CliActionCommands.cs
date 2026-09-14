using System.CommandLine;
using Highbyte.Wrighty.Actions;
using Highbyte.Wrighty.Configuration;
using Highbyte.Wrighty.Errors;
using Highbyte.Wrighty.Models;
using Highbyte.Wrighty.Processes;
using Highbyte.Wrighty.Workers;

namespace Highbyte.Wrighty.Cli;

public sealed partial class CliApplication
{
    private Command BuildActionsCommand()
    {
        var id = WorkItemIdArgument();
        var name = new Argument<string?>("action-name") { Arity = ArgumentArity.ZeroOrOne };
        var all = new Option<bool>("--all") { Description = "Include unavailable actions and their reasons." };
        var json = JsonOption();
        var exec = new Option<bool>("--exec") { Description = "Reserved; action execution is not supported yet." };
        var command = new Command("actions", "Discover the actions available for a work item (read-only)");
        command.Arguments.Add(id);
        command.Arguments.Add(name);
        command.Options.Add(all);
        command.Options.Add(json);
        command.Options.Add(exec);
        command.SetAction((parsed, cancellationToken) => ExecuteAsync(parsed.GetValue(json), async config =>
        {
            if (parsed.GetValue(exec))
                throw new TrackerException("ACTION_EXECUTION_UNSUPPORTED",
                    "Action discovery is read-only; use the documented focused command after review.", 2);
            var itemId = tracker.ResolveId(config, parsed.GetValue(id)!);
            var state = await tracker.GetOperationalAsync(config, itemId, cancellationToken);
            var discovery = await DiscoverActionsAsync(config, state, cancellationToken);
            var shown = SelectActions(discovery, parsed.GetValue(name), parsed.GetValue(all));
            await writer.WriteActionsAsync(shown, parsed.GetValue(json));
        }, cancellationToken));
        return command;
    }

    private static OperationalActionDiscovery SelectActions(
        OperationalActionDiscovery discovery, string? selected, bool all)
    {
        if (selected is null)
            return discovery with
            {
                Actions = discovery.Actions.Where(action => all || action.Availability == "available").ToArray()
            };
        var action = discovery.Actions.SingleOrDefault(value => value.Name == selected)
            ?? throw new TrackerException("ACTION_UNKNOWN", $"Unknown action '{selected}'.", 2);
        if (all)
            throw new TrackerException("ARGUMENT_INVALID", "--all cannot be combined with an action name.", 2);
        if (action.UnavailableCode is { } code)
            throw new TrackerException(code, action.UnavailableReason!, 5);
        return discovery with { Actions = [action] };
    }

    private async Task<OperationalActionDiscovery> DiscoverActionsAsync(
        TrackerConfig config, WorkItemOperationalState state, CancellationToken cancellationToken)
    {
        var session = state.Session;
        var context = new OperationalActionContext(config, state,
            clock?.Invoke() ?? DateTimeOffset.UtcNow,
            session is { FromCurrentInstallation: true, WorkspacePath: { } path } && Directory.Exists(path));
        if (OperationalActionResolver.SessionAvailability(context).Code is null)
        {
            context = context with
            {
                InteractiveAdmission = DescribeInteractiveAvailability(session!.Agent!),
                WorkerAdmission = await DescribeWorkerAvailabilityAsync(config, state, cancellationToken)
            };
        }
        if (session?.Continuation is { } continuation)
        {
            var settings = config.Worker?.EffectiveContinuation ?? new WorkerContinuationConfig();
            context = context with
            {
                ContinuationBudget = continuation.BudgetWith(
                    settings.MaxAutomaticContinuations, settings.Cooldown, settings.Debounce)
            };
        }
        return OperationalActionResolver.Resolve(context);
    }
    private ActionAvailability DescribeInteractiveAvailability(string agent)
    {
        try
        {
            if (agents.Find(agent)?.InteractiveAdapter is null)
                return new("AGENT_INTERACTIVE_UNSUPPORTED", "The recorded agent has no interactive resume adapter.");
            var runtime = (runtimes ?? new AgentRuntimeCatalog(agents, new PathExecutableResolver()))
                .Snapshot().Find(agent);
            return runtime?.Installed == true ? ActionAvailability.Available
                : new("AGENT_NOT_INSTALLED", "The recorded agent CLI is not installed.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or TrackerException)
        {
            return ActionAvailability.Unverified;
        }
    }

    private async Task<ActionAvailability> DescribeWorkerAvailabilityAsync(
        TrackerConfig config, WorkItemOperationalState state, CancellationToken cancellationToken)
    {
        if (workerService is null || state.Item.Archived ||
            string.Equals(state.Item.Status, config.DefaultFinishTo, StringComparison.OrdinalIgnoreCase))
            return ActionAvailability.Unverified;
        try
        {
            // Reuse the worker's read-only exact-item admission path. Never claim work, prepare a
            // workspace, start a vendor process, or probe paid provider capacity for discovery.
            var options = new WorkerOptions(null, true, null, WorkspaceMode.Current,
                new Dictionary<string, string>(), null, TimeSpan.FromHours(1),
                FencedAction.Kill, null, "agent", true, true);
            await workerService.PreflightItemAsync(config, options, workingDirectory, state.Item.Id,
                WorkerItemIntent.Auto, _ => Task.CompletedTask, cancellationToken);
            return ActionAvailability.Available;
        }
        catch (TrackerException exception)
        {
            return new(exception.Code, exception.Message);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return ActionAvailability.Unverified;
        }
    }

}
