using Highbyte.Wrighty.Actions;
using Highbyte.Wrighty.Workers;

namespace Highbyte.Wrighty.Cli.Output;

public sealed partial class OutputWriter
{
    public async Task WriteActionsAsync(OperationalActionDiscovery discovery, bool json)
    {
        if (json)
        {
            await WriteJsonAsync(new { schemaVersion = 1, result = discovery });
            return;
        }
        await output.WriteLineAsync($"Actions for {discovery.ItemId}");
        await output.WriteLineAsync("Discovery only; no action is executed.");
        if (discovery.Actions.Count == 0)
            await output.WriteLineAsync("No available actions.");
        foreach (var action in discovery.Actions)
            await WriteActionAsync(action);
    }

    public async Task WriteWorkflowActionAsync(WorkflowActionResult result, WorkerDiscovery? workers,
        string? refreshError, bool json)
    {
        if (json)
        {
            await WriteJsonAsync(new { schemaVersion = 1, result, workers, refreshError });
            return;
        }
        await output.WriteLineAsync($"{result.Action} applied to {result.ItemId}: " +
            $"{result.Before.Status} → {result.After.Status}; {result.After.OperationalStatus}.");
        await output.WriteLineAsync($"Automatic execution: {result.After.AutomaticExecutionAllowed}. No worker started.");
        if (workers is not null) await WriteWorkersAsync(workers, false);
        if (refreshError is not null)
            await output.WriteLineAsync("Action applied; worker assessment could not be refreshed. Inspect before retrying.");
    }

    private async Task WriteActionAsync(OperationalAction action)
    {
        await output.WriteLineAsync();
        var recommended = action.Recommended ? " (recommended)" : string.Empty;
        await output.WriteLineAsync($"{action.Name} — {action.Title}{recommended}");
        await output.WriteLineAsync($"  {action.Description}");
        await output.WriteLineAsync($"  {action.Availability}; execution: {action.Execution}; confirmation: {action.Confirmation}");
        if (action.UnavailableCode is { } code)
            await output.WriteLineAsync($"  {code}: {action.UnavailableReason}");
        if (action.RequiresTty)
            await output.WriteLineAsync("  Requires an interactive terminal for the displayed interactive command.");
        if (action.StartsProcess)
            await output.WriteLineAsync("  Starts a process when explicitly invoked.");
        if (action.Url is { } url)
            await output.WriteLineAsync($"  Link: {url}");
        foreach (var command in action.Commands)
            await output.WriteLineAsync($"  {command}");
        if (action.AgentPrompt is { } prompt)
            await output.WriteLineAsync($"  Prompt for the agent session: {prompt}");
    }
}
