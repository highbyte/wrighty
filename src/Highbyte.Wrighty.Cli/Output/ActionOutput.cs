using Highbyte.Wrighty.Actions;

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
