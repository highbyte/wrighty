using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Highbyte.Wrighty.Caching;

namespace Highbyte.Wrighty.Workers;

public enum WorkerInstanceState
{
    Idle,
    RunningItem,
    Stopping,
    Draining,
    StoppingNow,
    Finalizing,
    PreparingItem
}

public enum WorkerHostKind
{
    CliProcess,
    WebHosted
}

public enum WorkerStopMode
{
    Drain,
    Interrupt
}

public enum WorkerInstanceLiveness
{
    Running,
    Stale,
    Unknown
}

public sealed record WorkerInstance(
    string RunId,
    int ProcessId,
    string? ProcessStartIdentity,
    DateTimeOffset StartedAt,
    DateTimeOffset LastHeartbeatAt,
    string ConfigurationPathHash,
    string ConfigurationRevision,
    string WrightyVersion,
    string InvocationSummary,
    string? CurrentItemId,
    WorkerInstanceState State,
    WorkerHostKind HostKind = WorkerHostKind.CliProcess,
    string? CurrentAgent = null,
    int? ControlProtocolVersion = null,
    IReadOnlyList<WorkerStopMode>? SupportedStopModes = null,
    string? CurrentItemTitle = null);

public sealed record WorkerInstanceStatus(
    WorkerInstance Instance,
    WorkerInstanceLiveness Liveness,
    string? Detail);

public sealed record WorkerProcessObservation(bool Exists, string? StartIdentity);

public sealed record WorkerRegistrationMetadata(
    WorkerHostKind HostKind,
    int ControlProtocolVersion = 1,
    IReadOnlyList<WorkerStopMode>? SupportedStopModes = null)
{
    public IReadOnlyList<WorkerStopMode> EffectiveSupportedStopModes =>
        SupportedStopModes ?? [WorkerStopMode.Drain, WorkerStopMode.Interrupt];
}

public sealed record WorkerStopTarget(
    string RunId,
    int ProcessId,
    string? ProcessStartIdentity,
    WorkerHostKind HostKind);

public sealed record WorkerStopRequestResult(bool Accepted, string Code, string Message);

public interface IWorkerInstanceRegistration : IAsyncDisposable
{
    string RunId { get; }

    Task UpdateAsync(
        string? currentItemId,
        WorkerInstanceState state,
        CancellationToken cancellationToken);

    Task UpdateAsync(
        string? currentItemId,
        string? currentAgent,
        WorkerInstanceState state,
        CancellationToken cancellationToken) =>
        UpdateAsync(currentItemId, state, cancellationToken);

    Task UpdateAsync(
        string? currentItemId,
        string? currentItemTitle,
        string? currentAgent,
        WorkerInstanceState state,
        CancellationToken cancellationToken) =>
        UpdateAsync(currentItemId, currentAgent, state, cancellationToken);

    Task<WorkerStopMode?> ReadStopRequestAsync(CancellationToken cancellationToken) =>
        Task.FromResult<WorkerStopMode?>(null);

    Task UpdateStateAsync(
        WorkerInstanceState state,
        CancellationToken cancellationToken) =>
        UpdateAsync(null, state, cancellationToken);
}

public interface IWorkerInstanceRegistry
{
    Task<IWorkerInstanceRegistration> RegisterAsync(
        string configurationPath,
        string configurationRevision,
        string invocationSummary,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<WorkerInstanceStatus>> ListAsync(
        string configurationPath,
        CancellationToken cancellationToken);

    Task<IWorkerInstanceRegistration> RegisterAsync(
        string configurationPath,
        string configurationRevision,
        string invocationSummary,
        WorkerRegistrationMetadata metadata,
        CancellationToken cancellationToken) =>
        RegisterAsync(
            configurationPath,
            configurationRevision,
            invocationSummary,
            cancellationToken);

    Task<WorkerStopRequestResult> RequestStopAsync(
        string configurationPath,
        WorkerStopTarget target,
        WorkerStopMode mode,
        CancellationToken cancellationToken) =>
        Task.FromResult(new WorkerStopRequestResult(
            false,
            "WORKER_CONTROL_UNAVAILABLE",
            "This worker registry does not support cooperative stop requests."));
}

public sealed class NoOpWorkerInstanceRegistry : IWorkerInstanceRegistry
{
    public static NoOpWorkerInstanceRegistry Instance { get; } = new();

    public Task<IWorkerInstanceRegistration> RegisterAsync(
        string configurationPath,
        string configurationRevision,
        string invocationSummary,
        CancellationToken cancellationToken) =>
        Task.FromResult<IWorkerInstanceRegistration>(NoOpRegistration.Registration);

    public Task<IReadOnlyList<WorkerInstanceStatus>> ListAsync(
        string configurationPath,
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<WorkerInstanceStatus>>([]);

    private sealed class NoOpRegistration : IWorkerInstanceRegistration
    {
        public static NoOpRegistration Registration { get; } = new();
        public string RunId => string.Empty;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public Task UpdateAsync(
            string? currentItemId,
            WorkerInstanceState state,
            CancellationToken cancellationToken) => Task.CompletedTask;
    }
}

public sealed class JsonWorkerInstanceRegistry(
    CachePaths paths,
    Func<DateTimeOffset>? clock = null,
    Func<int, WorkerProcessObservation>? observeProcess = null,
    TimeSpan? heartbeatInterval = null,
    TimeSpan? staleThreshold = null) : IWorkerInstanceRegistry
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };
    private readonly Func<DateTimeOffset> now = clock ?? (() => DateTimeOffset.UtcNow);
    private readonly Func<int, WorkerProcessObservation> observe =
        observeProcess ?? ObserveProcess;
    private readonly TimeSpan heartbeatEvery = heartbeatInterval ?? TimeSpan.FromSeconds(15);
    private readonly TimeSpan staleAfter = staleThreshold ?? TimeSpan.FromSeconds(45);

    public Task<IWorkerInstanceRegistration> RegisterAsync(
        string configurationPath,
        string configurationRevision,
        string invocationSummary,
        CancellationToken cancellationToken) =>
        RegisterAsync(
            configurationPath,
            configurationRevision,
            invocationSummary,
            new WorkerRegistrationMetadata(WorkerHostKind.CliProcess),
            cancellationToken);

    public async Task<IWorkerInstanceRegistration> RegisterAsync(
        string configurationPath,
        string configurationRevision,
        string invocationSummary,
        WorkerRegistrationMetadata metadata,
        CancellationToken cancellationToken)
    {
        var process = Process.GetCurrentProcess();
        var pathHash = ConfigurationPathHash(configurationPath);
        var runId = Guid.NewGuid().ToString("N");
        var timestamp = now();
        var instance = new WorkerInstance(
            runId,
            Environment.ProcessId,
            ReadProcessStartIdentity(process),
            timestamp,
            timestamp,
            pathHash,
            configurationRevision,
            typeof(JsonWorkerInstanceRegistry).Assembly.GetName().Version?.ToString() ?? "unknown",
            invocationSummary,
            null,
            WorkerInstanceState.Idle,
            metadata.HostKind,
            CurrentAgent: null,
            metadata.ControlProtocolVersion,
            metadata.EffectiveSupportedStopModes);
        var registration = new Registration(
            RecordPath(pathHash, runId),
            StopRequestPath(pathHash, runId),
            instance,
            now,
            heartbeatEvery);
        await registration.StartAsync(cancellationToken);
        return registration;
    }

    public async Task<IReadOnlyList<WorkerInstanceStatus>> ListAsync(
        string configurationPath,
        CancellationToken cancellationToken)
    {
        var directory = Path.Combine(
            paths.WorkerInstancesRoot,
            ConfigurationPathHash(configurationPath));
        string[] records;
        try
        {
            if (!Directory.Exists(directory))
                return [];
            records = Directory.GetFiles(directory, "*.json")
                .Where(path => !path.EndsWith(".stop.json", StringComparison.Ordinal))
                .ToArray();
            CleanupExpiredStopRequests(directory);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            return [UnreadableStatus(
                configurationPath,
                "Worker registry directory could not be read.")];
        }

        var statuses = new List<WorkerInstanceStatus>();
        foreach (var path in records)
        {
            cancellationToken.ThrowIfCancellationRequested();
            WorkerInstance? instance;
            try
            {
                await using var stream = File.OpenRead(path);
                instance = await JsonSerializer.DeserializeAsync<WorkerInstance>(
                    stream,
                    JsonOptions,
                    cancellationToken);
            }
            catch (Exception exception) when (
                exception is IOException or JsonException or UnauthorizedAccessException)
            {
                statuses.Add(UnreadableStatus(
                    configurationPath,
                    "Worker record could not be read.",
                    Path.GetFileNameWithoutExtension(path)));
                continue;
            }
            if (instance is null)
                continue;

            statuses.Add(Status(instance));
            if (now() - instance.LastHeartbeatAt > TimeSpan.FromHours(24))
            {
                try
                {
                    File.Delete(path);
                    File.Delete(StopRequestPath(
                        instance.ConfigurationPathHash,
                        instance.RunId));
                }
                catch (Exception exception) when (
                    exception is IOException or UnauthorizedAccessException)
                {
                    // Expired records are best-effort cleanup; listing remains authoritative.
                }
            }
        }

        return statuses
            .OrderBy(value => LivenessOrder(value.Liveness))
            .ThenByDescending(value => value.Instance.StartedAt)
            .ThenBy(value => value.Instance.RunId, StringComparer.Ordinal)
            .ToArray();
    }

    public async Task<WorkerStopRequestResult> RequestStopAsync(
        string configurationPath,
        WorkerStopTarget target,
        WorkerStopMode mode,
        CancellationToken cancellationToken)
    {
        var pathHash = ConfigurationPathHash(configurationPath);
        var recordPath = RecordPath(pathHash, target.RunId);
        WorkerInstance? instance;
        try
        {
            await using var stream = File.OpenRead(recordPath);
            instance = await JsonSerializer.DeserializeAsync<WorkerInstance>(
                stream,
                JsonOptions,
                cancellationToken);
        }
        catch (FileNotFoundException)
        {
            return StopRejected("WORKER_NOT_RUNNING", "The worker record no longer exists.");
        }
        catch (Exception exception) when (
            exception is IOException or JsonException or UnauthorizedAccessException)
        {
            return StopRejected(
                "WORKER_CONTROL_UNAVAILABLE",
                "The worker record could not be verified.");
        }

        if (instance is null ||
            instance.RunId != target.RunId ||
            instance.ProcessId != target.ProcessId ||
            instance.HostKind != target.HostKind ||
            !string.Equals(
                instance.ProcessStartIdentity,
                target.ProcessStartIdentity,
                StringComparison.Ordinal) ||
            instance.ConfigurationPathHash != pathHash)
        {
            return StopRejected(
                "WORKER_IDENTITY_CHANGED",
                "The worker identity changed; refresh before requesting a stop.");
        }

        var status = Status(instance);
        if (status.Liveness != WorkerInstanceLiveness.Running)
        {
            return StopRejected(
                "WORKER_NOT_VERIFIED",
                "Only a live worker with a verified process identity can be stopped.");
        }

        if (instance.ControlProtocolVersion != 1 ||
            instance.SupportedStopModes is null ||
            !instance.SupportedStopModes.Contains(mode))
        {
            return StopRejected(
                "WORKER_CONTROL_UNSUPPORTED",
                "This worker version does not support the requested cooperative stop mode.");
        }

        var requestPath = StopRequestPath(pathHash, target.RunId);
        var existing = await ReadStopRequestRecordAsync(requestPath, cancellationToken);
        var effectiveMode = existing?.Mode == WorkerStopMode.Interrupt
            ? WorkerStopMode.Interrupt
            : mode;
        var request = new StopRequestRecord(
            1,
            target.RunId,
            target.ProcessId,
            target.ProcessStartIdentity,
            pathHash,
            effectiveMode,
            now());
        await WriteAtomicallyAsync(requestPath, request, cancellationToken);
        string message;
        if (effectiveMode == WorkerStopMode.Interrupt)
        {
            message = "The worker was asked to stop now and finalize its current item.";
        }
        else if (instance.CurrentItemId is null)
        {
            message = "The idle worker is stopping without claiming another item.";
        }
        else
        {
            message = "The worker will stop after its current item.";
        }
        return new WorkerStopRequestResult(
            true,
            "WORKER_STOP_REQUESTED",
            message);
    }

    private static WorkerStopRequestResult StopRejected(string code, string message) =>
        new(false, code, message);

    private static int LivenessOrder(WorkerInstanceLiveness liveness) => liveness switch
    {
        WorkerInstanceLiveness.Running => 0,
        WorkerInstanceLiveness.Unknown => 1,
        WorkerInstanceLiveness.Stale => 2,
        _ => 3
    };

    private static WorkerInstanceStatus UnreadableStatus(
        string configurationPath,
        string detail,
        string runId = "registry-unavailable") =>
        new(
            new WorkerInstance(
                runId,
                0,
                null,
                DateTimeOffset.MinValue,
                DateTimeOffset.MinValue,
                ConfigurationPathHash(configurationPath),
                string.Empty,
                "unknown",
                "Unreadable worker registry",
                null,
                WorkerInstanceState.Stopping),
            WorkerInstanceLiveness.Unknown,
            detail);

    public static string ConfigurationPathHash(string configurationPath)
    {
        var canonical = Path.GetFullPath(configurationPath);
        if (OperatingSystem.IsWindows())
            canonical = canonical.ToUpperInvariant();
        return Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))[..16];
    }

    private WorkerInstanceStatus Status(WorkerInstance instance)
    {
        var observation = observe(instance.ProcessId);
        if (!observation.Exists)
        {
            return new WorkerInstanceStatus(
                instance,
                WorkerInstanceLiveness.Stale,
                "The recorded process no longer exists.");
        }
        if (now() - instance.LastHeartbeatAt > staleAfter)
        {
            return new WorkerInstanceStatus(
                instance,
                WorkerInstanceLiveness.Stale,
                $"No heartbeat since {instance.LastHeartbeatAt:O}.");
        }
        if (observation.StartIdentity is null || instance.ProcessStartIdentity is null)
        {
            return new WorkerInstanceStatus(
                instance,
                WorkerInstanceLiveness.Unknown,
                "The operating system could not verify process-start identity.");
        }
        if (!string.Equals(
                observation.StartIdentity,
                instance.ProcessStartIdentity,
                StringComparison.Ordinal))
        {
            return new WorkerInstanceStatus(
                instance,
                WorkerInstanceLiveness.Stale,
                "The process ID has been reused.");
        }
        return new WorkerInstanceStatus(instance, WorkerInstanceLiveness.Running, null);
    }

    private string RecordPath(string pathHash, string runId) =>
        Path.Combine(paths.WorkerInstancesRoot, pathHash, $"{runId}.json");

    private string StopRequestPath(string pathHash, string runId) =>
        Path.Combine(paths.WorkerInstancesRoot, pathHash, $"{runId}.stop.json");

    private static async Task<StopRequestRecord?> ReadStopRequestRecordAsync(
        string path,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<StopRequestRecord>(
                stream,
                JsonOptions,
                cancellationToken);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (Exception exception) when (
            exception is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static async Task WriteAtomicallyAsync<T>(
        string path,
        T value,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(
                temporary,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(stream, value, JsonOptions, cancellationToken);
                await stream.WriteAsync("\n"u8.ToArray(), cancellationToken);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(temporary))
                    File.Delete(temporary);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                // Preserve the primary write failure.
            }
        }
    }

    private static WorkerProcessObservation ObserveProcess(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return new WorkerProcessObservation(true, ReadProcessStartIdentity(process));
        }
        catch (ArgumentException)
        {
            return new WorkerProcessObservation(false, null);
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return new WorkerProcessObservation(true, null);
        }
    }

    private static string? ReadProcessStartIdentity(Process process)
    {
        try { return process.StartTime.ToUniversalTime().Ticks.ToString(); }
        catch (Exception exception) when (
            exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    private sealed class Registration(
        string path,
        string stopRequestPath,
        WorkerInstance instance,
        Func<DateTimeOffset> clock,
        TimeSpan heartbeatEvery) : IWorkerInstanceRegistration
    {
        private readonly SemaphoreSlim gate = new(1, 1);
        private readonly CancellationTokenSource stop = new();
        private WorkerInstance current = instance;
        private Task? heartbeat;
        private bool disposed;

        public string RunId => current.RunId;

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            await WriteAsync(cancellationToken);
            heartbeat = HeartbeatAsync();
        }

        public async Task UpdateAsync(
            string? currentItemId,
            WorkerInstanceState state,
            CancellationToken cancellationToken)
            => await UpdateAsync(currentItemId, current.CurrentAgent, state, cancellationToken);

        public async Task UpdateAsync(
            string? currentItemId,
            string? currentAgent,
            WorkerInstanceState state,
            CancellationToken cancellationToken)
            => await UpdateAsync(
                currentItemId,
                currentItemTitle: null,
                currentAgent,
                state,
                cancellationToken);

        public async Task UpdateAsync(
            string? currentItemId,
            string? currentItemTitle,
            string? currentAgent,
            WorkerInstanceState state,
            CancellationToken cancellationToken)
        {
            await gate.WaitAsync(cancellationToken);
            try
            {
                if (disposed)
                    return;
                current = current with
                {
                    CurrentItemId = currentItemId,
                    CurrentItemTitle = currentItemId is null
                        ? null
                        : string.Equals(current.CurrentItemId, currentItemId, StringComparison.Ordinal)
                            ? currentItemTitle ?? current.CurrentItemTitle
                            : currentItemTitle,
                    CurrentAgent = currentAgent,
                    State = state,
                    LastHeartbeatAt = clock()
                };
                await WriteWithoutGateAsync(cancellationToken);
            }
            finally
            {
                gate.Release();
            }
        }

        public async Task<WorkerStopMode?> ReadStopRequestAsync(
            CancellationToken cancellationToken)
        {
            var request = await JsonWorkerInstanceRegistry.ReadStopRequestRecordAsync(
                stopRequestPath,
                cancellationToken);
            if (request is null ||
                request.ProtocolVersion != 1 ||
                request.RunId != current.RunId ||
                request.ProcessId != current.ProcessId ||
                request.ExpectedConfigurationPathHash != current.ConfigurationPathHash ||
                !string.Equals(
                    request.ProcessStartIdentity,
                    current.ProcessStartIdentity,
                    StringComparison.Ordinal))
            {
                return null;
            }
            return request.Mode;
        }

        public async Task UpdateStateAsync(
            WorkerInstanceState state,
            CancellationToken cancellationToken)
        {
            await gate.WaitAsync(cancellationToken);
            try
            {
                if (disposed)
                    return;
                current = current with
                {
                    State = state,
                    LastHeartbeatAt = clock()
                };
                await WriteWithoutGateAsync(cancellationToken);
            }
            finally
            {
                gate.Release();
            }
        }

        public async ValueTask DisposeAsync()
        {
            await gate.WaitAsync(CancellationToken.None);
            try
            {
                if (disposed)
                    return;
                disposed = true;
                await stop.CancelAsync();
            }
            finally
            {
                gate.Release();
            }
            if (heartbeat is not null)
            {
                try { await heartbeat; }
                catch (OperationCanceledException)
                {
                    // Cancellation is the expected heartbeat shutdown path.
                }
                catch (Exception exception) when (
                    exception is IOException or UnauthorizedAccessException)
                {
                    // Disposal remains best effort when the registry becomes unavailable.
                }
            }
            try { File.Delete(path); }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                // A stale record is preferable to failing worker shutdown.
            }
            try { File.Delete(stopRequestPath); }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                // A stale request cannot control a future run because run IDs are unique.
            }
            stop.Dispose();
            gate.Dispose();
        }

        private async Task HeartbeatAsync()
        {
            using var timer = new PeriodicTimer(heartbeatEvery);
            while (await timer.WaitForNextTickAsync(stop.Token))
            {
                await gate.WaitAsync(stop.Token);
                try
                {
                    if (disposed)
                        return;
                    current = current with { LastHeartbeatAt = clock() };
                    await WriteWithoutGateAsync(stop.Token);
                }
                finally
                {
                    gate.Release();
                }
            }
        }

        private async Task WriteAsync(CancellationToken cancellationToken)
        {
            await gate.WaitAsync(cancellationToken);
            try { await WriteWithoutGateAsync(cancellationToken); }
            finally { gate.Release(); }
        }

        private async Task WriteWithoutGateAsync(CancellationToken cancellationToken)
        {
            var directory = Path.GetDirectoryName(path)!;
            Directory.CreateDirectory(directory);
            var temporary = Path.Combine(directory, $".{current.RunId}.{Guid.NewGuid():N}.tmp");
            try
            {
                await using (var stream = new FileStream(
                    temporary,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    4096,
                    FileOptions.Asynchronous))
                {
                    await JsonSerializer.SerializeAsync(
                        stream,
                        current,
                        JsonOptions,
                        cancellationToken);
                    await stream.WriteAsync("\n"u8.ToArray(), cancellationToken);
                }
                File.Move(temporary, path, overwrite: true);
            }
            finally
            {
                try
                {
                    if (File.Exists(temporary))
                        File.Delete(temporary);
                }
                catch (Exception exception) when (
                    exception is IOException or UnauthorizedAccessException)
                {
                    // Preserve the primary write failure when temporary cleanup also fails.
                }
            }
        }
    }

    private void CleanupExpiredStopRequests(string directory)
    {
        foreach (var path in Directory.GetFiles(directory, "*.stop.json"))
        {
            try
            {
                if (now() - File.GetLastWriteTimeUtc(path) > TimeSpan.FromHours(24))
                    File.Delete(path);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                // Run IDs prevent a stale request from controlling a future worker.
            }
        }
    }

    private sealed record StopRequestRecord(
        int ProtocolVersion,
        string RunId,
        int ProcessId,
        string? ProcessStartIdentity,
        [property: JsonPropertyName("configurationPathHash")]
        string ExpectedConfigurationPathHash,
        WorkerStopMode Mode,
        DateTimeOffset RequestedAt);
}
