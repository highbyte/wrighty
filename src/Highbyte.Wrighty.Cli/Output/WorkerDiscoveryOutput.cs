using Highbyte.Wrighty.Workers;

namespace Highbyte.Wrighty.Cli.Output;

public sealed partial class OutputWriter
{
    public async Task WriteWorkerControlAsync(string runId, WorkerStopMode mode,
        WorkerStopRequestResult result, bool json)
    {
        if (json)
        {
            await WriteJsonAsync(new { schemaVersion = 1, result = new
            {
                runId, requestedMode = mode.ToString().ToLowerInvariant(),
                result.Accepted, result.Code, result.Message, completed = false
            } });
            return;
        }
        await output.WriteLineAsync($"{runId}: {result.Message}");
        await output.WriteLineAsync("Request accepted; completion is not yet verified. Inspect the run and its owning terminal.");
    }

    public async Task WriteWorkersAsync(WorkerDiscovery discovery, bool json)
    {
        if (json)
        {
            await WriteJsonAsync(new { schemaVersion = 1, result = discovery });
            return;
        }
        await output.WriteLineAsync($"Registered local workers ({discovery.LocalWorkers.Count}); coverage: {discovery.Coverage}");
        await output.WriteLineAsync("Scope: this installation and configuration. Unregistered or remote workers may exist.");
        if (discovery.Detail is not null)
            await output.WriteLineAsync(discovery.Detail);
        foreach (var worker in discovery.LocalWorkers)
            await WriteWorkerDiscoveryEntryAsync(worker);
    }

    private static string AllowanceLabel(WorkerDiscoveryEntry worker)
    {
        if (worker.RemainingItemAllowance is { } remaining)
            return remaining.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return worker.Instance.Scheduling is { ItemLimit: null } scheduling && scheduling.IsUsable()
            ? "unlimited" : "unknown";
    }

    private async Task WriteWorkerDiscoveryEntryAsync(WorkerDiscoveryEntry worker)
    {
        var instance = worker.Instance;
        await output.WriteLineAsync($"  {instance.RunId}  pid {instance.ProcessId}  {instance.HostKind}  {worker.Liveness}  intake: {worker.Intake}");
        await output.WriteLineAsync($"    Last reported: {instance.State}; item: {instance.CurrentItemId ?? "none"}; agent: {instance.CurrentAgent ?? "none"}; heartbeat: {instance.LastHeartbeatAt:O}");
        if (instance.Scheduling is { } scheduling)
            await output.WriteLineAsync($"    Selection: {scheduling.Mode}; source: {scheduling.FromStatus}; target: {scheduling.TargetItemId ?? "any eligible item"}; remaining allowance: {AllowanceLabel(worker)}");
        if (worker.ConfigurationDrift == true)
            await output.WriteLineAsync("    Configuration differs from the startup snapshot.");
        if (worker.Detail is not null)
            await output.WriteLineAsync($"    {worker.Detail}");
        if (worker.Pickup is { } pickup)
            await output.WriteLineAsync($"    {pickup.ItemId}: {pickup.Outcome} ({pickup.Code}) — {pickup.Message}" +
                (pickup.AfterCurrentItem ? " Pickup would follow the current item." : string.Empty));
    }
}
