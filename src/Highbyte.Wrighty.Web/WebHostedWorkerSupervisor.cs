using System.Text;
using Highbyte.Wrighty.Configuration;
using Highbyte.Wrighty.Workers;

namespace Highbyte.Wrighty.Web;

public enum WebHostedWorkerState
{
    Stopped,
    Starting,
    Running,
    WaitingForWorkspace,
    Draining,
    StoppingNow,
    Finalizing,
    Failed
}

public sealed record HostedWorkerLogEntry(
    long Sequence,
    DateTimeOffset OccurredAt,
    string Level,
    string Type,
    string? ItemId,
    string? Agent,
    string? Outcome,
    string? Message);

public sealed record HostedWorkerSnapshot(
    WebHostedWorkerState State,
    string? RunId,
    string? CurrentItemId,
    string? CurrentAgent,
    DateTimeOffset? StartedAt,
    DateTimeOffset? EndedAt,
    string? Failure,
    long LatestLogSequence,
    IReadOnlyList<HostedWorkerLogEntry> Log)
{
    public bool CanStop => State is WebHostedWorkerState.Starting or WebHostedWorkerState.Running or
        WebHostedWorkerState.WaitingForWorkspace or WebHostedWorkerState.Draining;
}

public sealed record HostedWorkerCommandResult(
    bool Accepted,
    string Code,
    string Message,
    string? RunId = null);

internal sealed class HostedWorkerLogBuffer(int maximumEntries, int maximumBytes)
{
    private readonly List<HostedWorkerLogEntry> entries = [];
    private long sequence;
    private int retainedBytes;

    public long LatestSequence => sequence;

    public IReadOnlyList<HostedWorkerLogEntry> Snapshot(long afterSequence) =>
        entries.Where(entry => entry.Sequence > afterSequence).ToArray();

    public void Clear()
    {
        entries.Clear();
        sequence = 0;
        retainedBytes = 0;
    }

    public void Add(
        DateTimeOffset occurredAt,
        string level,
        string type,
        string? itemId,
        string? agent,
        string? outcome,
        string? message)
    {
        var entry = new HostedWorkerLogEntry(
            ++sequence,
            occurredAt,
            level,
            type,
            itemId,
            agent,
            outcome,
            message);
        var consolidateIdle = type is "idle" or "no-item" && entries.Count > 0 &&
            entries[^1].Type == type && entries[^1].ItemId == itemId &&
            entries[^1].Agent == agent && entries[^1].Outcome == outcome;
        if (consolidateIdle)
        {
            retainedBytes -= Size(entries[^1]);
            entries[^1] = entry;
        }
        else
        {
            entries.Add(entry);
        }
        retainedBytes += Size(entry);

        while (entries.Count > maximumEntries || retainedBytes > maximumBytes)
        {
            retainedBytes -= Size(entries[0]);
            entries.RemoveAt(0);
        }
    }

    private static int Size(HostedWorkerLogEntry entry) => Encoding.UTF8.GetByteCount(
        $"{entry.Sequence}{entry.OccurredAt:O}{entry.Level}{entry.Type}{entry.ItemId}" +
        $"{entry.Agent}{entry.Outcome}{entry.Message}");
}

/// <summary>Owns worker tasks whose lifetime is the current web-server process.</summary>
public sealed class WebHostedWorkerSupervisor(
    WorkerService? worker,
    IWorkerInstanceRegistry workerInstances,
    WebApplicationState applicationState)
{
    private const int MaximumLogEntries = 200;
    private const int MaximumLogBytes = 128 * 1024;
    private const int MaximumCompletedRuns = 32;
    private static readonly TimeSpan CompletedRunRetention = TimeSpan.FromSeconds(5);
    private readonly object gate = new();
    private readonly List<HostedWorkerRun> runs = [];

    public bool Available => worker is not null;

    public IReadOnlyList<HostedWorkerSnapshot> Snapshots()
    {
        lock (gate)
            return runs.Select(value => value.Snapshot())
                .Where(value => value.RunId is not null)
                .OrderByDescending(value => value.StartedAt)
                .ToArray();
    }

    public HostedWorkerSnapshot? Snapshot(string runId, long afterSequence = 0)
    {
        HostedWorkerRun? run;
        lock (gate)
            run = runs.FirstOrDefault(value => value.RunId == runId);
        return run?.Snapshot(afterSequence);
    }

    public bool Owns(string runId)
    {
        lock (gate)
            return runs.Any(value => value.RunId == runId);
    }

    public async Task<HostedWorkerCommandResult> StartAsync()
    {
        if (worker is null)
        {
            return Rejected(
                "HOSTED_WORKER_UNAVAILABLE",
                "Worker services are not configured in this web console.");
        }

        var configuration = applicationState.ActiveConfiguration;
        var drift = await ConfigurationDriftAsync(configuration);
        if (drift is not null)
            return Rejected("CONFIGURATION_RESTART_REQUIRED", drift);

        var run = new HostedWorkerRun(
            worker,
            workerInstances,
            applicationState,
            configuration,
            Complete,
            MaximumLogEntries,
            MaximumLogBytes);
        lock (gate)
            runs.Add(run);
        return await run.StartAsync();
    }

    public HostedWorkerCommandResult RequestDrain(string runId)
    {
        HostedWorkerRun? run;
        lock (gate)
            run = runs.FirstOrDefault(value => value.RunId == runId);
        return run?.RequestDrain() ??
            Rejected("HOSTED_WORKER_NOT_RUNNING", "That hosted worker is no longer running.");
    }

    public HostedWorkerCommandResult RequestInterrupt(string runId)
    {
        HostedWorkerRun? run;
        lock (gate)
            run = runs.FirstOrDefault(value => value.RunId == runId);
        return run?.RequestInterrupt() ??
            Rejected("HOSTED_WORKER_NOT_RUNNING", "That hosted worker is no longer running.");
    }

    public async Task StopForHostShutdownAsync(TimeSpan timeout)
    {
        HostedWorkerRun[] active;
        lock (gate)
            active = runs.ToArray();
        foreach (var run in active)
            run.RequestHostShutdown();

        try
        {
            await Task.WhenAll(active.Select(value => value.Completion)).WaitAsync(timeout);
        }
        catch (TimeoutException)
        {
            // The process shutdown boundary is bounded. The interruption journal remains if the
            // finalizer could not finish before the host exits.
        }
    }

    private void Complete(HostedWorkerRun run)
    {
        lock (gate)
        {
            var expired = runs
                .Where(value => value.EndedAt is not null)
                .OrderByDescending(value => value.EndedAt)
                .Skip(MaximumCompletedRuns)
                .ToArray();
            foreach (var value in expired)
                runs.Remove(value);
        }
        _ = RemoveCompletedAsync(run);
    }

    private async Task RemoveCompletedAsync(HostedWorkerRun run)
    {
        await Task.Delay(CompletedRunRetention);
        lock (gate)
            runs.Remove(run);
    }

    private static async Task<string?> ConfigurationDriftAsync(
        ActiveRepositoryConfiguration configuration)
    {
        if (configuration.Config.SourcePath is not { } sourcePath || !File.Exists(sourcePath))
            return null;
        var current = await RepositoryConfigurationService.RevisionAsync(
            sourcePath,
            CancellationToken.None);
        return string.Equals(
            current,
            configuration.Revision,
            StringComparison.Ordinal)
            ? null
            : "The repository configuration changed outside this web console. Refresh Settings before starting a worker; restart the web console only if the backend changed.";
    }

    private static HostedWorkerCommandResult Rejected(string code, string message) =>
        new(false, code, message);

    private sealed class HostedWorkerRun(
        WorkerService worker,
        IWorkerInstanceRegistry workerInstances,
        WebApplicationState applicationState,
        ActiveRepositoryConfiguration configuration,
        Action<HostedWorkerRun> completed,
        int maximumLogEntries,
        int maximumLogBytes)
    {
        private readonly object gate = new();
        private readonly HostedWorkerLogBuffer log = new(maximumLogEntries, maximumLogBytes);
        private WorkerRunControl? control;
        private Task runTask = Task.CompletedTask;
        private WebHostedWorkerState state = WebHostedWorkerState.Stopped;
        private string? runId;
        private string? currentItemId;
        private string? currentAgent;
        private DateTimeOffset? startedAt;
        private DateTimeOffset? endedAt;
        private string? failure;

        public string? RunId
        {
            get
            {
                lock (gate)
                    return runId;
            }
        }

        public DateTimeOffset? EndedAt
        {
            get
            {
                lock (gate)
                    return endedAt;
            }
        }

        public Task Completion
        {
            get
            {
                lock (gate)
                    return runTask;
            }
        }

        public HostedWorkerSnapshot Snapshot(long afterSequence = 0)
        {
            lock (gate)
            {
                return new HostedWorkerSnapshot(
                    state,
                    runId,
                    currentItemId,
                    currentAgent,
                    startedAt,
                    endedAt,
                    failure,
                    log.LatestSequence,
                    log.Snapshot(afterSequence));
            }
        }

        public async Task<HostedWorkerCommandResult> StartAsync()
        {
            WorkerRunControl ownedControl;
            Task ownedTask;
            lock (gate)
            {
                ownedControl = new WorkerRunControl();
                control = ownedControl;
                state = WebHostedWorkerState.Starting;
                startedAt = DateTimeOffset.UtcNow;
                AddLogWithoutLock("info", "host-starting", message:
                    "The web console is starting a continuous worker.");
                ownedTask = runTask = RunOwnedAsync(ownedControl);
            }
            await Task.WhenAny(ownedControl.RegistrationCompleted, ownedTask);
            if (!ownedControl.RegistrationCompleted.IsCompletedSuccessfully)
            {
                var snapshot = Snapshot();
                return Rejected(
                    "HOSTED_WORKER_START_FAILED",
                    snapshot.Failure ?? "The web console could not start its worker.");
            }
            lock (gate)
                runId = ownedControl.RunId;
            return new HostedWorkerCommandResult(
                true,
                "HOSTED_WORKER_STARTED",
                "The web console started a worker. It continues if this browser tab closes.",
                ownedControl.RunId);
        }

        public HostedWorkerCommandResult RequestDrain()
        {
            lock (gate)
            {
                if (control is null || !CanStopWithoutLock())
                    return Rejected("HOSTED_WORKER_NOT_RUNNING", "That hosted worker is no longer running.");
                control.RequestDrain();
                state = WebHostedWorkerState.Draining;
                AddLogWithoutLock(
                    "warning",
                    "host-draining",
                    currentItemId,
                    currentAgent,
                    message: currentItemId is null
                        ? "The worker is stopping without claiming another item."
                        : "The worker will stop after the current item and its bookkeeping complete.");
                return new HostedWorkerCommandResult(
                    true,
                    "HOSTED_WORKER_DRAINING",
                    currentItemId is null
                        ? "The worker is stopping."
                        : "The worker will stop after its current item.",
                    runId);
            }
        }

        public HostedWorkerCommandResult RequestInterrupt()
        {
            lock (gate)
            {
                if (control is null || !CanStopWithoutLock())
                    return Rejected("HOSTED_WORKER_NOT_RUNNING", "That hosted worker is no longer running.");
                control.RequestInterrupt(WorkerInterruptionReason.OperatorStopNow);
                state = WebHostedWorkerState.StoppingNow;
                AddLogWithoutLock(
                    "danger",
                    "host-stopping-now",
                    currentItemId,
                    currentAgent,
                    message: currentItemId is null
                        ? "The worker was asked to stop now."
                        : "The active agent is being stopped; Wrighty will finalize the item as needs attention.");
                return new HostedWorkerCommandResult(
                    true,
                    "HOSTED_WORKER_STOPPING_NOW",
                    "The worker is stopping the active agent and finalizing its item.",
                    runId);
            }
        }

        public void RequestHostShutdown()
        {
            lock (gate)
            {
                if (control is null || runTask.IsCompleted)
                    return;
                control.RequestInterrupt(WorkerInterruptionReason.HostShutdown);
                state = WebHostedWorkerState.StoppingNow;
                AddLogWithoutLock(
                    "warning",
                    "host-shutdown",
                    currentItemId,
                    currentAgent,
                    message: "The web host is shutting down; the worker is finalizing its active item.");
            }
        }

        private async Task RunOwnedAsync(WorkerRunControl ownedControl)
        {
            try
            {
                lock (gate)
                    state = WebHostedWorkerState.Running;
                var config = configuration.Config;
                var configurationPath = config.SourcePath is { } sourcePath
                    ? Path.GetFullPath(sourcePath)
                    : Path.Combine(applicationState.WorkspacePath, TrackerConfigLoader.FileName);
                var options = new WorkerOptions(
                    config.EffectiveWorker.DefaultAgent,
                    Once: false,
                    MaxItems: null,
                    HostedWorkerEventProjection.WorkspaceMode(
                        config.EffectiveWorker.WorkspaceMode),
                    new Dictionary<string, string>(),
                    IdleTimeout: null,
                    ItemTimeout: TimeSpan.FromHours(1),
                    FencedAction.Kill,
                    ClaimantId: null,
                    ClaimantKind: "agent",
                    DryRun: false,
                    Json: false);
                var host = new WorkerRunHost(worker, workerInstances);
                await host.RunAsync(
                    config,
                    options,
                    new WorkerRunIdentity(
                        applicationState.WorkspacePath,
                        configurationPath,
                        configuration.Revision ?? string.Empty,
                        "wrighty web hosted worker",
                        WorkerHostKind.WebHosted),
                    new WorkerRunSelection(null),
                    ownedControl,
                    new WorkerRunCallbacks(ObserveEventAsync, ObserveWarningAsync),
                    CancellationToken.None);
                lock (gate)
                {
                    state = WebHostedWorkerState.Stopped;
                    AddLogWithoutLock("info", "host-stopped", message:
                        "The web-console-hosted worker stopped.");
                }
            }
            catch (Exception exception)
            {
                lock (gate)
                {
                    state = WebHostedWorkerState.Failed;
                    failure = exception is Highbyte.Wrighty.Errors.TrackerException trackerException
                        ? $"The hosted worker stopped with {trackerException.Code}."
                        : "The hosted worker stopped after an unexpected error.";
                    AddLogWithoutLock(
                        "danger",
                        "host-failed",
                        message: failure ?? "The hosted worker failed.");
                }
            }
            finally
            {
                lock (gate)
                {
                    currentItemId = null;
                    currentAgent = null;
                    endedAt = DateTimeOffset.UtcNow;
                    if (ReferenceEquals(control, ownedControl))
                        control = null;
                }
                ownedControl.Dispose();
                completed(this);
            }
        }

        private Task ObserveWarningAsync(string message)
        {
            lock (gate)
                AddLogWithoutLock(
                    "warning",
                    "registry-warning",
                    message: "Worker registry status or control could not be updated.");
            return Task.CompletedTask;
        }

        private Task ObserveEventAsync(WorkerEvent value)
        {
            lock (gate)
            {
                ApplyEventWithoutLock(value);
                AddLogWithoutLock(
                    HostedWorkerEventProjection.Level(value),
                    value.Type,
                    value.ItemId,
                    value.Agent,
                    value.Outcome?.ToString(),
                    HostedWorkerEventProjection.SafeEventMessage(value));
            }
            return Task.CompletedTask;
        }

        private void ApplyEventWithoutLock(WorkerEvent value)
        {
            var projected = HostedWorkerEventProjection.Apply(
                state,
                currentItemId,
                currentAgent,
                value);
            state = projected.State;
            currentItemId = projected.ItemId;
            currentAgent = projected.Agent;
        }

        private void AddLogWithoutLock(
            string level,
            string type,
            string? itemId = null,
            string? agent = null,
            string? outcome = null,
            string? message = null)
        {
            log.Add(
                DateTimeOffset.UtcNow,
                level,
                HostedWorkerEventProjection.SafeToken(type) ?? "event",
                HostedWorkerEventProjection.SafeToken(itemId),
                HostedWorkerEventProjection.SafeToken(agent),
                HostedWorkerEventProjection.SafeToken(outcome),
                HostedWorkerEventProjection.SafeMessage(message));
        }

        private bool CanStopWithoutLock() => state is
            WebHostedWorkerState.Starting or WebHostedWorkerState.Running or
            WebHostedWorkerState.WaitingForWorkspace or WebHostedWorkerState.Draining;

    }

}
