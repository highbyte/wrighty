using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Highbyte.Wrighty.Caching;

namespace Highbyte.Wrighty.Workers;

public enum WorkerInstanceState
{
    Idle,
    RunningItem,
    Stopping
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
    WorkerInstanceState State);

public sealed record WorkerInstanceStatus(
    WorkerInstance Instance,
    WorkerInstanceLiveness Liveness,
    string? Detail);

public sealed record WorkerProcessObservation(bool Exists, string? StartIdentity);

public interface IWorkerInstanceRegistration : IAsyncDisposable
{
    string RunId { get; }

    Task UpdateAsync(
        string? currentItemId,
        WorkerInstanceState state,
        CancellationToken cancellationToken);
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

    public async Task<IWorkerInstanceRegistration> RegisterAsync(
        string configurationPath,
        string configurationRevision,
        string invocationSummary,
        CancellationToken cancellationToken)
    {
        var process = Process.GetCurrentProcess();
        var pathHash = ConfigurationPathHash(configurationPath);
        var runId = Guid.NewGuid().ToString("N");
        var timestamp = now();
        var instance = new WorkerInstance(
            runId,
            Environment.ProcessId,
            ProcessStartIdentity(process),
            timestamp,
            timestamp,
            pathHash,
            configurationRevision,
            typeof(JsonWorkerInstanceRegistry).Assembly.GetName().Version?.ToString() ?? "unknown",
            invocationSummary,
            null,
            WorkerInstanceState.Idle);
        var registration = new Registration(
            RecordPath(pathHash, runId),
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
            records = Directory.GetFiles(directory, "*.json");
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
                try { File.Delete(path); }
                catch (Exception exception) when (
                    exception is IOException or UnauthorizedAccessException)
                {
                    // Expired records are best-effort cleanup; listing remains authoritative.
                }
            }
        }

        return statuses
            .OrderBy(value => value.Instance.StartedAt)
            .ToArray();
    }

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
        if (now() - instance.LastHeartbeatAt > staleAfter)
        {
            return new WorkerInstanceStatus(
                instance,
                WorkerInstanceLiveness.Stale,
                $"No heartbeat since {instance.LastHeartbeatAt:O}.");
        }

        var observation = observe(instance.ProcessId);
        if (!observation.Exists)
        {
            return new WorkerInstanceStatus(
                instance,
                WorkerInstanceLiveness.Stale,
                "The recorded process no longer exists.");
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

    private static WorkerProcessObservation ObserveProcess(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return new WorkerProcessObservation(true, ProcessStartIdentity(process));
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

    private static string? ProcessStartIdentity(Process process)
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
        {
            await gate.WaitAsync(cancellationToken);
            try
            {
                if (disposed)
                    return;
                current = current with
                {
                    CurrentItemId = currentItemId,
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
}
