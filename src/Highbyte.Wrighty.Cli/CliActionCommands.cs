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
        var exec = new Option<bool>("--exec") { Description = "Execute one supported workflow action after revalidation." };
        var yes = new Option<bool>("--yes") { Description = "Authorize the selected action without prompting." };
        var expected = new Option<string?>("--expected-version") { Description = "Require the state version returned by action discovery." };
        var command = new Command("actions", "Discover or explicitly execute a work item action");
        command.Arguments.Add(id);
        command.Arguments.Add(name);
        command.Options.Add(all);
        command.Options.Add(json);
        command.Options.Add(exec);
        command.Options.Add(yes);
        command.Options.Add(expected);
        command.SetAction((parsed, cancellationToken) => ExecuteAsync(parsed.GetValue(json),
            config => RunActionCommandAsync(config, parsed.GetValue(id)!, parsed.GetValue(name),
                parsed.GetValue(all), parsed.GetValue(exec), parsed.GetValue(yes),
                parsed.GetValue(expected), parsed.GetValue(json), cancellationToken), cancellationToken));
        return command;
    }

    private async Task RunActionCommandAsync(TrackerConfig config, string id, string? selected,
        bool all, bool execute, bool yes, string? expectedVersion, bool json, CancellationToken cancellationToken)
    {
        ValidateActionOptions(selected, all, execute, yes, expectedVersion);
        var itemId = tracker.ResolveId(config, id);
        var state = await tracker.GetOperationalAsync(config, itemId, cancellationToken);
        var discovery = await DiscoverActionsAsync(config, state, cancellationToken);
        var shown = SelectActions(discovery, selected, all);
        if (!execute)
        {
            await writer.WriteActionsAsync(shown, json);
            return;
        }
        await ConfirmWorkflowActionAsync(shown.Actions.Single(), yes, json, cancellationToken);
        config = await configLoader.LoadAsync(workingDirectory, cancellationToken);
        var result = await new WorkflowActionService(tracker).ExecuteAsync(config, itemId, selected!,
            expectedVersion ?? discovery.StateVersion, cancellationToken);
        await WriteExecutedActionAsync(config, result, json, cancellationToken);
    }

    private static void ValidateActionOptions(string? selected, bool all, bool execute, bool yes, string? expectedVersion)
    {
        if (execute && (selected is null || all))
            throw new TrackerException("ARGUMENT_INVALID", "--exec requires one action name and cannot use --all.", 2);
        if (!execute && (yes || expectedVersion is not null))
            throw new TrackerException("ARGUMENT_INVALID", "--yes and --expected-version require --exec.", 2);
        if (execute) WorkflowActionService.EnsureSupported(selected!);
    }

    private async Task WriteExecutedActionAsync(TrackerConfig config, WorkflowActionResult result, bool json,
        CancellationToken cancellationToken)
    {
        WorkerDiscovery? workers = null;
        string? refreshError = null;
        try { workers = await ReadWorkersAsync(config, result.ItemId, cancellationToken); }
        catch (Exception exception) when (exception is TrackerException or IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            // The mutation succeeded. Failed follow-up inspection must not invite replay.
            refreshError = "WORKER_REFRESH_UNAVAILABLE";
        }
        await writer.WriteWorkflowActionAsync(result, workers, refreshError, json);
    }

    private async Task ConfirmWorkflowActionAsync(OperationalAction action, bool yes, bool json,
        CancellationToken cancellationToken)
    {
        if (yes) return;
        if (json || isInputRedirected())
            throw new TrackerException("ACTION_CONFIRMATION_REQUIRED",
                $"{action.Description} Pass --yes to authorize this operation.", 2);
        await output.WriteLineAsync(action.Description);
        await output.WriteAsync($"Confirm {action.Title}? [y/N] ");
        var answer = await input.ReadLineAsync(cancellationToken);
        if (!string.Equals(answer, "y", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(answer, "yes", StringComparison.OrdinalIgnoreCase))
            throw new TrackerException("ACTION_CONFIRMATION_REQUIRED", "The action was cancelled.", 2);
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
