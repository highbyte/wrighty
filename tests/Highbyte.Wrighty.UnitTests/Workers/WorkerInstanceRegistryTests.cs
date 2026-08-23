using Highbyte.Wrighty.Caching;
using Highbyte.Wrighty.Workers;
using System.Diagnostics;
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
    public async Task Registration_projects_origin_agent_and_cooperative_control_capabilities()
    {
        var registry = new JsonWorkerInstanceRegistry(
            new CachePaths(directory),
            heartbeatInterval: TimeSpan.FromMinutes(5));
        await using var registration = await registry.RegisterAsync(
            configPath,
            "revision-one",
            "wrighty web hosted worker",
            new WorkerRegistrationMetadata(WorkerHostKind.WebHosted),
            CancellationToken.None);

        await registration.UpdateAsync(
            "local:42",
            "codex",
            WorkerInstanceState.RunningItem,
            CancellationToken.None);
        var status = Assert.Single(await registry.ListAsync(configPath, CancellationToken.None));

        Assert.Equal(WorkerHostKind.WebHosted, status.Instance.HostKind);
        Assert.Equal("local:42", status.Instance.CurrentItemId);
        Assert.Equal("codex", status.Instance.CurrentAgent);
        Assert.Equal(1, status.Instance.ControlProtocolVersion);
        Assert.Equal(
            [WorkerStopMode.Drain, WorkerStopMode.Interrupt],
            status.Instance.SupportedStopModes);
    }

    [Fact]
    public async Task Stop_request_is_identity_checked_idempotent_and_can_escalate()
    {
        var registry = new JsonWorkerInstanceRegistry(
            new CachePaths(directory),
            heartbeatInterval: TimeSpan.FromMinutes(5));
        await using var registration = await registry.RegisterAsync(
            configPath,
            "revision-one",
            "wrighty worker",
            new WorkerRegistrationMetadata(WorkerHostKind.CliProcess),
            CancellationToken.None);
        var status = Assert.Single(await registry.ListAsync(configPath, CancellationToken.None));
        var target = new WorkerStopTarget(
            status.Instance.RunId,
            status.Instance.ProcessId,
            status.Instance.ProcessStartIdentity,
            status.Instance.HostKind);

        var rejected = await registry.RequestStopAsync(
            configPath,
            target with { ProcessId = target.ProcessId + 1 },
            WorkerStopMode.Drain,
            CancellationToken.None);
        Assert.False(rejected.Accepted);
        Assert.Equal("WORKER_IDENTITY_CHANGED", rejected.Code);

        var drain = await registry.RequestStopAsync(
            configPath,
            target,
            WorkerStopMode.Drain,
            CancellationToken.None);
        Assert.True(drain.Accepted);
        Assert.Equal(WorkerStopMode.Drain,
            await registration.ReadStopRequestAsync(CancellationToken.None));

        var interrupt = await registry.RequestStopAsync(
            configPath,
            target,
            WorkerStopMode.Interrupt,
            CancellationToken.None);
        Assert.True(interrupt.Accepted);
        Assert.Equal(WorkerStopMode.Interrupt,
            await registration.ReadStopRequestAsync(CancellationToken.None));

        var laterDrain = await registry.RequestStopAsync(
            configPath,
            target,
            WorkerStopMode.Drain,
            CancellationToken.None);
        Assert.True(laterDrain.Accepted);
        Assert.Equal(WorkerStopMode.Interrupt,
            await registration.ReadStopRequestAsync(CancellationToken.None));
        Assert.Single(await registry.ListAsync(configPath, CancellationToken.None));
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
    public async Task Multiple_registrations_keep_their_order_when_heartbeats_change()
    {
        var observedAt = DateTimeOffset.Parse("2026-08-23T10:00:00Z");
        var registry = new JsonWorkerInstanceRegistry(
            new CachePaths(directory),
            () => observedAt,
            heartbeatInterval: TimeSpan.FromMinutes(5),
            staleThreshold: TimeSpan.FromHours(1));
        await using var first = await registry.RegisterAsync(
            configPath,
            "revision-one",
            "wrighty worker --first",
            CancellationToken.None);
        observedAt = observedAt.AddMinutes(1);
        await using var second = await registry.RegisterAsync(
            configPath,
            "revision-two",
            "wrighty worker --second",
            CancellationToken.None);

        var beforeHeartbeat = await registry.ListAsync(configPath, CancellationToken.None);
        Assert.Equal([second.RunId, first.RunId],
            beforeHeartbeat.Select(value => value.Instance.RunId));

        observedAt = observedAt.AddMinutes(1);
        await first.UpdateStateAsync(WorkerInstanceState.Idle, CancellationToken.None);

        var afterHeartbeat = await registry.ListAsync(configPath, CancellationToken.None);
        Assert.Equal([second.RunId, first.RunId],
            afterHeartbeat.Select(value => value.Instance.RunId));
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
        Assert.Contains("no longer exists", status.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(recordPath));
    }

    [Fact]
    public async Task Workers_are_ordered_by_liveness_then_recent_heartbeat()
    {
        var observedAt = DateTimeOffset.Parse("2026-08-16T10:00:00Z");
        var paths = new CachePaths(directory);
        var recordDirectory = Path.Combine(
            paths.WorkerInstancesRoot,
            JsonWorkerInstanceRegistry.ConfigurationPathHash(configPath));
        Directory.CreateDirectory(recordDirectory);
        var instances = new[]
        {
            Instance("stale", 40, "stale-start", observedAt.AddMinutes(-1), observedAt.AddSeconds(-1)),
            Instance("unknown", 30, null, observedAt.AddMinutes(-2), observedAt.AddSeconds(-2)),
            Instance("running-older", 20, "running-older-start", observedAt.AddMinutes(-4), observedAt.AddSeconds(-4)),
            Instance("running-newer", 10, "running-newer-start", observedAt.AddMinutes(-3), observedAt.AddSeconds(-3))
        };
        foreach (var instance in instances)
        {
            await File.WriteAllTextAsync(
                Path.Combine(recordDirectory, $"{instance.RunId}.json"),
                JsonSerializer.Serialize(instance));
        }
        var registry = new JsonWorkerInstanceRegistry(
            paths,
            () => observedAt,
            processId => processId switch
            {
                10 => new WorkerProcessObservation(true, "running-newer-start"),
                20 => new WorkerProcessObservation(true, "running-older-start"),
                30 => new WorkerProcessObservation(true, null),
                _ => new WorkerProcessObservation(false, null)
            });

        var statuses = await registry.ListAsync(configPath, CancellationToken.None);

        Assert.Equal(
            ["running-newer", "running-older", "unknown", "stale"],
            statuses.Select(status => status.Instance.RunId));
    }

    [Fact]
    public async Task Real_process_identity_detects_a_live_process_and_its_exit()
    {
        var paths = new CachePaths(directory);
        var recordDirectory = Path.Combine(
            paths.WorkerInstancesRoot,
            JsonWorkerInstanceRegistry.ConfigurationPathHash(configPath));
        Directory.CreateDirectory(recordDirectory);
        using var process = StartLongRunningProcess();
        try
        {
            var observedAt = DateTimeOffset.UtcNow;
            var instance = Instance(
                "real-process",
                process.Id,
                process.StartTime.ToUniversalTime().Ticks.ToString(),
                observedAt,
                observedAt);
            await File.WriteAllTextAsync(
                Path.Combine(recordDirectory, "real-process.json"),
                JsonSerializer.Serialize(instance));
            var registry = new JsonWorkerInstanceRegistry(
                paths,
                () => observedAt,
                heartbeatInterval: TimeSpan.FromMinutes(5));

            var running = Assert.Single(
                await registry.ListAsync(configPath, CancellationToken.None));
            Assert.Equal(WorkerInstanceLiveness.Running, running.Liveness);

            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();

            var stopped = Assert.Single(
                await registry.ListAsync(configPath, CancellationToken.None));
            Assert.Equal(WorkerInstanceLiveness.Stale, stopped.Liveness);
            Assert.Contains("no longer exists", stopped.Detail, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
        }
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

    private WorkerInstance Instance(
        string runId,
        int processId,
        string? processStartIdentity,
        DateTimeOffset startedAt,
        DateTimeOffset lastHeartbeatAt) => new(
            runId,
            processId,
            processStartIdentity,
            startedAt,
            lastHeartbeatAt,
            JsonWorkerInstanceRegistry.ConfigurationPathHash(configPath),
            "revision",
            "test",
            "wrighty worker",
            null,
            WorkerInstanceState.Idle);

    private static Process StartLongRunningProcess()
    {
        var start = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("ping", "-n 30 127.0.0.1")
            : new ProcessStartInfo("/bin/sleep", "30");
        start.UseShellExecute = false;
        start.CreateNoWindow = true;
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        return Process.Start(start) ?? throw new InvalidOperationException(
            "Could not start the process-identity test helper.");
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
