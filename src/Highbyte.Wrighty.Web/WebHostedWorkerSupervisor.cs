using System.Text;
using Highbyte.Wrighty.Configuration;
using Highbyte.Wrighty.Workers;

namespace Highbyte.Wrighty.Web;

public enum WebHostedWorkerState
{
    Stopped,
    Starting,
    Running,
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
    public bool CanStart => State is WebHostedWorkerState.Stopped or WebHostedWorkerState.Failed;
    public bool CanStop => State is WebHostedWorkerState.Starting or WebHostedWorkerState.Running or
        WebHostedWorkerState.Draining;
}

public sealed record HostedWorkerCommandResult(bool Accepted, string Code, string Message);

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

/// <summary>Owns the one worker task whose lifetime is the current web-server process.</summary>
public sealed class WebHostedWorkerSupervisor(
    WorkerService? worker,
    IWorkerInstanceRegistry workerInstances,
    WebApplicationState applicationState)
{
    private const int MaximumLogEntries = 200;
    private const int MaximumLogBytes = 128 * 1024;
    private readonly object gate = new();
    private readonly HostedWorkerLogBuffer log = new(MaximumLogEntries, MaximumLogBytes);
    private WorkerRunControl? control;
    private Task? runTask;
    private WebHostedWorkerState state = WebHostedWorkerState.Stopped;
    private string? runId;
    private string? currentItemId;
    private string? currentAgent;
    private DateTimeOffset? startedAt;
    private DateTimeOffset? endedAt;
    private string? failure;

    public bool Available => worker is not null;

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
        if (worker is null)
        {
            return Rejected(
                "HOSTED_WORKER_UNAVAILABLE",
                "Worker services are not configured in this web console.");
        }

        var drift = await ConfigurationDriftAsync();
        if (drift is not null)
            return Rejected("CONFIGURATION_RESTART_REQUIRED", drift);

        WorkerRunControl nextControl;
        Task nextRunTask;
        lock (gate)
        {
            if (state is not (WebHostedWorkerState.Stopped or WebHostedWorkerState.Failed))
            {
                return Rejected(
                    "HOSTED_WORKER_ALREADY_RUNNING",
                    "This web console already owns a worker run.");
            }

            nextControl = new WorkerRunControl();
            control = nextControl;
            state = WebHostedWorkerState.Starting;
            runId = null;
            currentItemId = null;
            currentAgent = null;
            startedAt = DateTimeOffset.UtcNow;
            endedAt = null;
            failure = null;
            log.Clear();
            AddLogWithoutLock("info", "host-starting", message:
                "The web console is starting a continuous worker.");
            nextRunTask = runTask = RunOwnedAsync(worker, nextControl);
        }
        await Task.WhenAny(nextControl.RegistrationCompleted, nextRunTask);
        if (!nextControl.RegistrationCompleted.IsCompletedSuccessfully)
        {
            var snapshot = Snapshot();
            return Rejected(
                "HOSTED_WORKER_START_FAILED",
                snapshot.Failure ?? "The web console could not start its worker.");
        }
        lock (gate)
        {
            if (ReferenceEquals(runTask, nextRunTask))
                runId = nextControl.RunId;
        }
        return new HostedWorkerCommandResult(
            true,
            "HOSTED_WORKER_STARTED",
            "The web console started a worker. It continues if this browser tab closes.");
    }

    public HostedWorkerCommandResult RequestDrain()
    {
        lock (gate)
        {
            if (control is null || !SnapshotCanStopWithoutLock())
                return Rejected("HOSTED_WORKER_NOT_RUNNING", "No hosted worker is running.");
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
                    : "The worker will stop after its current item.");
        }
    }

    public HostedWorkerCommandResult RequestInterrupt()
    {
        lock (gate)
        {
            if (control is null || !SnapshotCanStopWithoutLock())
                return Rejected("HOSTED_WORKER_NOT_RUNNING", "No hosted worker is running.");
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
                "The worker is stopping the active agent and finalizing its item.");
        }
    }

    public async Task StopForHostShutdownAsync(TimeSpan timeout)
    {
        Task? owned;
        lock (gate)
        {
            owned = runTask;
            if (control is null || owned is null || owned.IsCompleted)
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

        try
        {
            await owned.WaitAsync(timeout);
        }
        catch (TimeoutException)
        {
            // The process shutdown boundary is bounded. The interruption journal remains if the
            // finalizer could not finish before the host exits.
        }
    }

    private async Task RunOwnedAsync(WorkerService service, WorkerRunControl ownedControl)
    {
        try
        {
            lock (gate)
                state = WebHostedWorkerState.Running;
            var configurationPath = applicationState.Config.SourcePath is { } sourcePath
                ? Path.GetFullPath(sourcePath)
                : Path.Combine(applicationState.WorkspacePath, TrackerConfigLoader.FileName);
            var options = new WorkerOptions(
                applicationState.Config.EffectiveWorker.DefaultAgent,
                Once: false,
                MaxItems: null,
                WorkspaceMode(applicationState.Config.EffectiveWorker.WorkspaceMode),
                new Dictionary<string, string>(),
                IdleTimeout: null,
                ItemTimeout: TimeSpan.FromHours(1),
                FencedAction.Kill,
                ClaimantId: null,
                ClaimantKind: "agent",
                DryRun: false,
                Json: false);
            var host = new WorkerRunHost(service, workerInstances);
            await host.RunAsync(
                applicationState.Config,
                options,
                applicationState.WorkspacePath,
                configurationPath,
                applicationState.ActiveConfigurationRevision ?? string.Empty,
                "wrighty web hosted worker",
                WorkerHostKind.WebHosted,
                new WorkerRunSelection(null),
                ownedControl,
                ObserveEventAsync,
                ObserveWarningAsync,
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
            var running = value.Type is "started" or "resumed" or "running" or "session";
            var terminal = value.Type is "finished" or "needs-attention" or "failed" or
                "fenced" or "timed-out" or "rejected" or "retry-scheduled" or "interrupted";
            if (running && value.ItemId is not null)
            {
                currentItemId = value.ItemId;
                currentAgent = value.Agent;
            }
            else if (terminal)
            {
                currentItemId = null;
                currentAgent = null;
                if (state == WebHostedWorkerState.StoppingNow)
                    state = WebHostedWorkerState.Finalizing;
            }
            AddLogWithoutLock(
                Level(value),
                value.Type,
                value.ItemId,
                value.Agent,
                value.Outcome?.ToString(),
                SafeEventMessage(value));
        }
        return Task.CompletedTask;
    }

    private async Task<string?> ConfigurationDriftAsync()
    {
        if (applicationState.Config.SourcePath is not { } sourcePath || !File.Exists(sourcePath))
            return null;
        var current = await RepositoryConfigurationService.RevisionAsync(
            sourcePath,
            CancellationToken.None);
        return string.Equals(
            current,
            applicationState.ActiveConfigurationRevision,
            StringComparison.Ordinal)
            ? null
            : "The repository configuration changed after the web console started. Restart the web console before starting a worker.";
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
            SafeToken(type) ?? "event",
            SafeToken(itemId),
            SafeToken(agent),
            SafeToken(outcome),
            SafeMessage(message));
    }

    private bool SnapshotCanStopWithoutLock() => state is
        WebHostedWorkerState.Starting or WebHostedWorkerState.Running or
        WebHostedWorkerState.Draining;

    private static string Level(WorkerEvent value) =>
        WorkerEventClassifier.Classify(value.Type) switch
        {
            WorkerEventSemantic.Success => "success",
            WorkerEventSemantic.Warning => "warning",
            WorkerEventSemantic.Danger => "danger",
            WorkerEventSemantic.Muted => "muted",
            _ => "info"
        };

    private static string? SafeEventMessage(WorkerEvent value) => value.Type switch
    {
        "idle" or "no-item" or "retry-scheduled" or "workspace-busy" or
            "agent-unavailable" or "provider-unavailable" => SafeMessage(value.Message),
        "started" => "The agent session started.",
        "resumed" => "The retained agent session resumed.",
        "running" or "session" => "The agent session is running.",
        "finished" => "The item finished.",
        "needs-attention" => "The item needs operator attention.",
        "failed" => "The agent session failed.",
        "fenced" => "The worker lost claim ownership.",
        "timed-out" => "The agent session timed out.",
        "rejected" => "The agent session was rejected.",
        "interrupted" => value.Outcome == AgentOutcome.InterruptedByOperator
            ? "The operator stopped the agent session; item finalization completed."
            : "The web host interrupted the agent session; item finalization completed.",
        _ => null
    };

    private static string? SafeToken(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var safe = new string(value.Trim().Where(character => !char.IsControl(character)).ToArray());
        return safe.Length <= 100 ? safe : safe[..100];
    }

    private static string? SafeMessage(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var safe = new string(value.Trim().Where(character =>
            character is '\t' or '\r' or '\n' || !char.IsControl(character)).ToArray());
        return safe.Length <= 500 ? safe : $"{safe[..499]}…";
    }

    private static HostedWorkerCommandResult Rejected(string code, string message) =>
        new(false, code, message);

    private static Highbyte.Wrighty.Workers.WorkspaceMode WorkspaceMode(string? value) =>
        value?.ToLowerInvariant() switch
        {
            "worktree" => Highbyte.Wrighty.Workers.WorkspaceMode.Worktree,
            "shared" => Highbyte.Wrighty.Workers.WorkspaceMode.Shared,
            _ => Highbyte.Wrighty.Workers.WorkspaceMode.Current
        };
}
