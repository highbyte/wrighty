using Highbyte.Wrighty.Caching;
using Highbyte.Wrighty.Workers;
using System.Text.Json;

namespace Highbyte.Wrighty.UnitTests.Workers;

public sealed class WorkerInstanceRegistryTests : IDisposable
{
    private readonly string directory = Path.Combine(
        Path.GetTempPath(),
        $"wrighty-worker-registry-{Guid.NewGuid():N}");
    private readonly string configPath = Path.Combine(
        Path.GetTempPath(),
        $"wrighty-config-{Guid.NewGuid():N}",
        ".wrighty.json");

    [Fact]
    public async Task Registration_is_listed_updated_and_removed_on_clean_exit()
    {
        var registry = new JsonWorkerInstanceRegistry(
            new CachePaths(directory),
            heartbeatInterval: TimeSpan.FromMinutes(5));
        var registration = await registry.RegisterAsync(
            configPath,
            "revision-one",
            "wrighty worker --once",
            CancellationToken.None);

        var initial = Assert.Single(await registry.ListAsync(configPath, CancellationToken.None));
        Assert.Equal(WorkerInstanceLiveness.Running, initial.Liveness);
        Assert.Equal(WorkerInstanceState.Idle, initial.Instance.State);
        Assert.Equal("revision-one", initial.Instance.ConfigurationRevision);

        await registration.UpdateAsync(
            "local:42",
            WorkerInstanceState.RunningItem,
            CancellationToken.None);
        var updated = Assert.Single(await registry.ListAsync(configPath, CancellationToken.None));
        Assert.Equal("local:42", updated.Instance.CurrentItemId);
        Assert.Equal(WorkerInstanceState.RunningItem, updated.Instance.State);

        await registration.DisposeAsync();
        Assert.Empty(await registry.ListAsync(configPath, CancellationToken.None));
    }

    [Fact]
    public async Task Missed_heartbeat_and_pid_reuse_are_not_reported_as_running()
    {
        var observedAt = DateTimeOffset.Parse("2026-07-30T10:00:00Z");
        var paths = new CachePaths(directory);
        var writer = new JsonWorkerInstanceRegistry(
            paths,
            () => observedAt,
            heartbeatInterval: TimeSpan.FromMinutes(5));
        await using var registration = await writer.RegisterAsync(
            configPath,
            "revision-one",
            "wrighty worker",
            CancellationToken.None);

        var staleReader = new JsonWorkerInstanceRegistry(
            paths,
            () => observedAt.AddMinutes(1),
            _ => new WorkerProcessObservation(true, "different-process-start"),
            heartbeatInterval: TimeSpan.FromMinutes(5),
            staleThreshold: TimeSpan.FromSeconds(45));
        var stale = Assert.Single(
            await staleReader.ListAsync(configPath, CancellationToken.None));
        Assert.Equal(WorkerInstanceLiveness.Stale, stale.Liveness);
        Assert.Contains("heartbeat", stale.Detail, StringComparison.OrdinalIgnoreCase);

        var reusedReader = new JsonWorkerInstanceRegistry(
            paths,
            () => observedAt.AddSeconds(1),
            _ => new WorkerProcessObservation(true, "different-process-start"),
            heartbeatInterval: TimeSpan.FromMinutes(5),
            staleThreshold: TimeSpan.FromSeconds(45));
        var reused = Assert.Single(
            await reusedReader.ListAsync(configPath, CancellationToken.None));
        Assert.Equal(WorkerInstanceLiveness.Stale, reused.Liveness);
        Assert.Contains("reused", reused.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Unverifiable_process_identity_is_reported_as_unknown()
    {
        var paths = new CachePaths(directory);
        var writer = new JsonWorkerInstanceRegistry(
            paths,
            heartbeatInterval: TimeSpan.FromMinutes(5));
        await using var registration = await writer.RegisterAsync(
            configPath,
            "revision",
            "wrighty worker",
            CancellationToken.None);
        var reader = new JsonWorkerInstanceRegistry(
            paths,
            observeProcess: _ => new WorkerProcessObservation(true, null),
            heartbeatInterval: TimeSpan.FromMinutes(5));

        var status = Assert.Single(
            await reader.ListAsync(configPath, CancellationToken.None));

        Assert.Equal(WorkerInstanceLiveness.Unknown, status.Liveness);
        Assert.Contains("could not verify", status.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Multiple_registrations_coexist_and_corrupt_records_degrade_to_unknown()
    {
        var paths = new CachePaths(directory);
        var registry = new JsonWorkerInstanceRegistry(
            paths,
            heartbeatInterval: TimeSpan.FromMinutes(5));
        await using var first = await registry.RegisterAsync(
            configPath,
            "revision-one",
            "wrighty worker --once",
            CancellationToken.None);
        await using var second = await registry.RegisterAsync(
            configPath,
            "revision-two",
            "wrighty worker --item local:2",
            CancellationToken.None);

        var active = await registry.ListAsync(configPath, CancellationToken.None);
        Assert.Equal(2, active.Count);
        Assert.Contains(active, value => value.Instance.ConfigurationRevision == "revision-one");
        Assert.Contains(active, value => value.Instance.ConfigurationRevision == "revision-two");

        var recordDirectory = Path.Combine(
            paths.WorkerInstancesRoot,
            JsonWorkerInstanceRegistry.ConfigurationPathHash(configPath));
        await File.WriteAllTextAsync(
            Path.Combine(recordDirectory, "corrupt.json"),
            "{not-json");

        var withCorrupt = await registry.ListAsync(configPath, CancellationToken.None);
        var corrupt = Assert.Single(
            withCorrupt,
            value => value.Instance.RunId == "corrupt");
        Assert.Equal(WorkerInstanceLiveness.Unknown, corrupt.Liveness);
        Assert.Contains("could not be read", corrupt.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Missing_registry_and_no_op_registry_are_empty_and_safe()
    {
        var registry = new JsonWorkerInstanceRegistry(new CachePaths(directory));
        Assert.Empty(await registry.ListAsync(configPath, CancellationToken.None));

        await using var registration = await NoOpWorkerInstanceRegistry.Instance.RegisterAsync(
            configPath,
            "revision",
            "wrighty worker",
            CancellationToken.None);
        Assert.Equal(string.Empty, registration.RunId);
        await registration.UpdateAsync(
            "local:1",
            WorkerInstanceState.RunningItem,
            CancellationToken.None);
        Assert.Empty(await NoOpWorkerInstanceRegistry.Instance.ListAsync(
            configPath,
            CancellationToken.None));
    }

    [Fact]
    public async Task Missing_process_is_stale_and_expired_record_is_cleaned_up()
    {
        var observedAt = DateTimeOffset.Parse("2026-07-30T10:00:00Z");
        var paths = new CachePaths(directory);
        var recordDirectory = Path.Combine(
            paths.WorkerInstancesRoot,
            JsonWorkerInstanceRegistry.ConfigurationPathHash(configPath));
        Directory.CreateDirectory(recordDirectory);
        var recordPath = Path.Combine(recordDirectory, "expired.json");
        var instance = new WorkerInstance(
            "expired",
            424242,
            "old-start",
            observedAt.AddDays(-2),
            observedAt.AddDays(-2),
            JsonWorkerInstanceRegistry.ConfigurationPathHash(configPath),
            "old-revision",
            "1.0.0",
            "wrighty worker",
            null,
            WorkerInstanceState.Idle);
        await File.WriteAllTextAsync(
            recordPath,
            JsonSerializer.Serialize(instance));
        var registry = new JsonWorkerInstanceRegistry(
            paths,
            () => observedAt,
            _ => new WorkerProcessObservation(false, null));

        var status = Assert.Single(
            await registry.ListAsync(configPath, CancellationToken.None));

        Assert.Equal(WorkerInstanceLiveness.Stale, status.Liveness);
        Assert.Contains("heartbeat", status.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(recordPath));
    }

    [Fact]
    public async Task Recent_record_for_missing_process_reports_process_exit()
    {
        var observedAt = DateTimeOffset.Parse("2026-07-30T10:00:00Z");
        var paths = new CachePaths(directory);
        var writer = new JsonWorkerInstanceRegistry(
            paths,
            () => observedAt,
            heartbeatInterval: TimeSpan.FromMinutes(5));
        await using var registration = await writer.RegisterAsync(
            configPath,
            "revision",
            "wrighty worker",
            CancellationToken.None);
        var reader = new JsonWorkerInstanceRegistry(
            paths,
            () => observedAt.AddSeconds(1),
            _ => new WorkerProcessObservation(false, null),
            heartbeatInterval: TimeSpan.FromMinutes(5));

        var status = Assert.Single(
            await reader.ListAsync(configPath, CancellationToken.None));

        Assert.Equal(WorkerInstanceLiveness.Stale, status.Liveness);
        Assert.Contains("no longer exists", status.Detail, StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
        var configDirectory = Path.GetDirectoryName(configPath);
        if (configDirectory is not null && Directory.Exists(configDirectory))
            Directory.Delete(configDirectory, recursive: true);
    }
}
