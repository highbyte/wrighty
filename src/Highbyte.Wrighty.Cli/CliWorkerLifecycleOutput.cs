using System.Text.Json;
using Highbyte.Wrighty.Workers;

namespace Highbyte.Wrighty.Cli;

public sealed partial class CliApplication
{
    private static readonly JsonSerializerOptions WorkerReceiptJson = new(JsonSerializerDefaults.Web);

    private async Task WriteWorkerLaunchAsync(string runId, WorkerRunIdentity identity,
        WorkerScheduling scheduling, bool json)
    {
        var registered = !string.IsNullOrEmpty(runId);
        var statusCommand = registered ? new[] { "wrighty", "workers", "show", runId, "--json" } : null;
        if (json)
        {
            await output.WriteLineAsync(JsonSerializer.Serialize(new
            {
                schemaVersion = 1, type = "worker-run-started", runId = registered ? runId : null,
                registered, configurationPathHash = JsonWorkerInstanceRegistry.ConfigurationPathHash(identity.ConfigurationPath),
                identity.ConfigurationRevision, scheduling,
                owner = new { kind = "foreground-process", processId = Environment.ProcessId,
                    lifetime = "Attached to the invoking process/terminal; no detach or restart guarantee." },
                statusCommand, logs = "Invoking terminal stdout (events) and stderr (diagnostics)."
            }, WorkerReceiptJson));
        }
        else
        {
            await output.WriteLineAsync($"Worker run: {(registered ? runId : "unregistered — registry control unavailable")}");
            await output.WriteLineAsync($"Owner: foreground process {Environment.ProcessId}; selection: {scheduling.Mode}; target: {scheduling.TargetItemId ?? "next eligible items"}; limit: {scheduling.ItemLimit?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unlimited"}.");
            await output.WriteLineAsync("Lifetime: attached to this process/terminal. Logs: terminal stdout/stderr.");
            if (statusCommand is not null)
                await output.WriteLineAsync($"Status (same configuration/cache): {string.Join(' ', statusCommand)}");
        }
        await output.FlushAsync();
    }

    private async Task WriteWorkerCompletionAsync(WorkerRunControl control, WorkerRunSummary summary, bool json)
    {
        var reason = control.IntakeClosed ? "drained" : "finished";
        if (control.IsInterrupted) reason = control.InterruptionReason.ToString();
        if (json)
            await output.WriteLineAsync(JsonSerializer.Serialize(new
            {
                schemaVersion = 1, type = "worker-run-completed",
                runId = string.IsNullOrEmpty(control.RunId) ? null : control.RunId,
                reason,
                summary
            }, WorkerReceiptJson));
        else
            await output.WriteLineAsync($"Worker run ended: {control.RunId ?? "unregistered"}; processed: {summary.Processed}; needs attention: {summary.NeedsAttention}; failed: {summary.Failed}.");
        await output.FlushAsync(CancellationToken.None);
    }
}
