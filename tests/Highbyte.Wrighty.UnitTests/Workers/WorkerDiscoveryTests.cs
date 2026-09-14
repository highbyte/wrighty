using System.Text.Json;
using System.Globalization;
using Highbyte.Wrighty.Caching;
using Highbyte.Wrighty.Configuration;
using Highbyte.Wrighty.Models;
using Highbyte.Wrighty.Workers;

namespace Highbyte.Wrighty.UnitTests.Workers;

public sealed class WorkerDiscoveryTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"wrighty-discovery-{Guid.NewGuid():N}");
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-14T10:00:00Z", CultureInfo.InvariantCulture);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private string ConfigPath => Path.Combine(root, "config.json");
    private string RecordDirectory => Path.Combine(new CachePaths(root).WorkerInstancesRoot,
        JsonWorkerInstanceRegistry.ConfigurationPathHash(ConfigPath));

    [Fact]
    public async Task Inspection_retains_expired_records_and_stop_requests()
    {
        var registry = Registry();
        await WriteRecord(Instance() with { LastHeartbeatAt = Now.AddDays(-2) });
        var stop = Path.Combine(RecordDirectory, "old.stop.json");
        await File.WriteAllTextAsync(stop, "{}");
        File.SetLastWriteTimeUtc(stop, Now.AddDays(-2).UtcDateTime);
        var before = Directory.GetFiles(RecordDirectory).ToDictionary(path => path, File.ReadAllText);
        var snapshot = await registry.InspectAsync(ConfigPath, default);
        Assert.Equal("complete", snapshot.Coverage);
        Assert.Equal(WorkerInstanceLiveness.Stale, Assert.Single(snapshot.Workers).Liveness);
        Assert.Equal(before, Directory.GetFiles(RecordDirectory).ToDictionary(path => path, File.ReadAllText));
    }

    [Fact]
    public async Task Empty_scope_is_distinct_from_unreadable_records_and_legacy_registry()
    {
        Assert.Equal("complete", (await Registry().InspectAsync(ConfigPath, default)).Coverage);
        Assert.False(Directory.Exists(root));
        Directory.CreateDirectory(RecordDirectory);
        await File.WriteAllTextAsync(Path.Combine(RecordDirectory, "broken.json"), "{");
        var partial = await Registry().InspectAsync(ConfigPath, default);
        Assert.Equal("incomplete", partial.Coverage);
        Assert.Equal(WorkerInstanceLiveness.Unknown, Assert.Single(partial.Workers).Liveness);
        IWorkerInstanceRegistry legacy = NoOpWorkerInstanceRegistry.Instance;
        Assert.Equal("unavailable", (await legacy.InspectAsync(ConfigPath, default)).Coverage);
    }

    [Fact]
    public async Task Inaccessible_registry_path_is_not_a_confidently_empty_scope()
    {
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(new CachePaths(root).WorkerInstancesRoot, "not a directory");
        var snapshot = await Registry().InspectAsync(ConfigPath, default);
        Assert.Equal("unavailable", snapshot.Coverage);
        Assert.Equal(WorkerInstanceLiveness.Unknown, Assert.Single(snapshot.Workers).Liveness);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("wrong-scope")]
    [InlineData("wrong-run")]
    public async Task Invalid_record_identity_is_not_treated_as_empty_or_live(string variant)
    {
        var instance = Instance();
        if (variant == "wrong-scope") instance = instance with { ConfigurationPathHash = "elsewhere" };
        if (variant == "wrong-run") instance = instance with { RunId = "elsewhere" };
        Directory.CreateDirectory(RecordDirectory);
        await File.WriteAllTextAsync(Path.Combine(RecordDirectory, "run.json"),
            variant == "null" ? "null" : JsonSerializer.Serialize(instance, JsonOptions));
        Assert.Equal("incomplete", (await Registry().InspectAsync(ConfigPath, default)).Coverage);
    }

    [Theory]
    [InlineData("denied", WorkerInstanceLiveness.Unknown)]
    [InlineData("missing", WorkerInstanceLiveness.Stale)]
    [InlineData("reused", WorkerInstanceLiveness.Stale)]
    public async Task Targeted_process_inspection_degrades_honestly(string scenario, WorkerInstanceLiveness expected)
    {
        await WriteRecord(Instance());
        var inspected = new List<int>();
        var registry = new JsonWorkerInstanceRegistry(new CachePaths(root), () => Now, pid =>
        {
            inspected.Add(pid);
            return scenario switch
            {
                "denied" => throw new UnauthorizedAccessException(),
                "missing" => new(false, null),
                _ => new(true, "reused")
            };
        });
        Assert.Equal(expected, Assert.Single((await registry.InspectAsync(ConfigPath, default)).Workers).Liveness);
        Assert.Equal([123], inspected);
    }

    [Fact]
    public async Task Web_hosted_runs_sharing_a_pid_keep_distinct_run_ids_and_effective_scheduling()
    {
        var registry = new JsonWorkerInstanceRegistry(new CachePaths(root), heartbeatInterval: TimeSpan.FromMinutes(5));
        var metadata = new WorkerRegistrationMetadata(WorkerHostKind.WebHosted, Scheduling: Scheduling());
        await using var one = await registry.RegisterAsync(ConfigPath, "revision", "display only", metadata, default);
        await using var two = await registry.RegisterAsync(ConfigPath, "revision", "display only", metadata, default);
        await one.UpdateAsync("local:1", "Title", "codex", WorkerInstanceState.RunningItem, default);
        await one.UpdateProgressAsync(new(2, Now), default);
        var runs = (await registry.InspectAsync(ConfigPath, default)).Workers;
        Assert.Equal(2, runs.Count);
        Assert.Single(runs.Select(run => run.Instance.ProcessId).Distinct());
        Assert.Equal(2, runs.Select(run => run.Instance.RunId).Distinct().Count());
        var record = runs.Single(run => run.Instance.RunId == one.RunId).Instance;
        Assert.Equal(2, record.Progress!.Processed);
        Assert.Equal("Ideas", record.Scheduling!.FromStatus);
        Assert.Equal("claude", record.Scheduling.DefaultAgent);
        Assert.Equal("codex", record.CurrentAgent);
        await one.UpdateAsync(null, null, null, WorkerInstanceState.Idle, default);
        record = (await registry.InspectAsync(ConfigPath, default)).Workers.Single(run => run.Instance.RunId == one.RunId).Instance;
        Assert.Null(record.Progress); // no transient allowance between completion and loop accounting
    }

    [Theory]
    [InlineData("once-busy", "cannot-pick-up", "ITEM_LIMIT_REACHED")]
    [InlineData("bounded-exhausted", "cannot-pick-up", "ITEM_LIMIT_REACHED")]
    [InlineData("targeted-other", "cannot-pick-up", "TARGET_MISMATCH")]
    [InlineData("targeted-same", "unknown", "TARGETED_STARTING")]
    [InlineData("drain", "cannot-pick-up", "INTAKE_CLOSED")]
    [InlineData("unknown", "unknown", "WORKER_NOT_VERIFIED")]
    [InlineData("legacy", "unknown", "SCHEDULING_UNKNOWN")]
    [InlineData("missing-progress", "unknown", "PROGRESS_UNKNOWN")]
    [InlineData("drift", "unknown", "CONFIGURATION_DRIFT")]
    [InlineData("expired", "cannot-pick-up", "IDLE_EXPIRED")]
    [InlineData("same-item", "could-pick-up", "ALREADY_PROCESSING")]
    public void Assessment_distinguishes_run_selection_and_lifetime(string scenario, string outcome, string code)
    {
        var worker = Instance();
        worker = scenario switch
        {
            "once-busy" => worker with { CurrentItemId = "local:other", State = WorkerInstanceState.RunningItem, Scheduling = Scheduling() with { Mode = "bounded", ItemLimit = 1 } },
            "bounded-exhausted" => worker with { Progress = new(3, Now), Scheduling = Scheduling() with { Mode = "bounded", ItemLimit = 3 } },
            "targeted-other" => worker with { Scheduling = Scheduling() with { Mode = "targeted", TargetItemId = "local:other", ItemLimit = 1 } },
            "targeted-same" => worker with { Scheduling = Scheduling() with { Mode = "targeted", TargetItemId = "local:1", ItemLimit = 1 } },
            "drain" => worker with { State = WorkerInstanceState.Draining },
            "legacy" => worker with { Scheduling = null },
            "missing-progress" => worker with { Progress = null },
            "drift" => worker with { ConfigurationRevision = "changed" },
            "expired" => worker with { Progress = new(0, Now.AddHours(-1)), Scheduling = Scheduling() with { IdleTimeout = TimeSpan.FromMinutes(1) } },
            "same-item" => worker with { CurrentItemId = "local:1", State = WorkerInstanceState.RunningItem },
            _ => worker
        };
        var liveness = scenario == "unknown" ? WorkerInstanceLiveness.Unknown : WorkerInstanceLiveness.Running;
        var result = WorkerPickupPolicy.AssessRegistration(new(worker, liveness, null), new("local:1"), "revision", Now);
        Assert.NotNull(result);
        Assert.Equal(outcome, result.Outcome);
        Assert.Equal(code, result.Code);
        Assert.Equal(scenario == "same-item", result.AlreadyProcessing);
    }

    [Fact]
    public void Continuous_busy_worker_still_requires_item_assessment_and_reports_remaining_allowance()
    {
        var worker = Instance() with { CurrentItemId = "local:other", State = WorkerInstanceState.RunningItem,
            Scheduling = Scheduling() with { Mode = "bounded", ItemLimit = 5 }, Progress = new(2, Now) };
        var status = new WorkerInstanceStatus(worker, WorkerInstanceLiveness.Running, null);
        Assert.Null(WorkerPickupPolicy.AssessRegistration(status, new("local:1"), "revision", Now));
        var row = WorkerDiscoveryEntry.From(status, "revision");
        Assert.Equal(2, row.RemainingItemAllowance);
        Assert.Equal("open", row.Intake);
    }

    [Fact]
    public void Workspace_lock_inspection_never_creates_a_file()
    {
        var locks = new FileWorkspaceExecutionLock(Path.Combine(root, "locks"));
        Assert.Equal("available", locks.Inspect(root).State);
        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public async Task Existing_busy_workspace_is_not_advertised_as_available()
    {
        var locks = new FileWorkspaceExecutionLock(Path.Combine(root, "locks"));
        await using var lease = await locks.AcquireAsync(root, default);
        Assert.Equal("unknown", locks.Inspect(root).State);
    }

    [Theory]
    [InlineData(WorkerInstanceState.Draining, "draining")]
    [InlineData(WorkerInstanceState.StoppingNow, "stopping")]
    [InlineData(WorkerInstanceState.Finalizing, "stopping")]
    public void Discovery_projects_closed_intake(WorkerInstanceState state, string expected)
    {
        var entry = WorkerDiscoveryEntry.From(new(Instance() with { State = state }, WorkerInstanceLiveness.Running, null), "revision");
        Assert.Equal(expected, entry.Intake);
    }

    [Fact]
    public void Startup_capture_copies_filters_and_retains_targeted_limits()
    {
        var filters = new Dictionary<string, string> { ["priority"] = "P1" };
        var options = new WorkerOptions(null, false, 5, WorkspaceMode.Worktree, filters, TimeSpan.FromMinutes(5),
            TimeSpan.FromHours(1), FencedAction.Kill, null, "agent", false, true, Profile: "deep");
        var captured = WorkerScheduling.From(new TrackerConfig(), options,
            new(root, ConfigPath, "revision", "display", WorkerHostKind.CliProcess), new(new WorkItemId("local:1")));
        filters["priority"] = "P2";
        Assert.Equal("P1", captured.Filters["priority"]);
        Assert.Equal("targeted", captured.Mode);
        Assert.Equal("local:1", captured.TargetItemId);
        Assert.Equal(1, captured.ItemLimit);
        Assert.Equal("deep", captured.Profile);
        Assert.True(captured.IsUsable());
    }

    private JsonWorkerInstanceRegistry Registry() => new(new CachePaths(root), () => Now, _ => new(true, "start"));
    private WorkerInstance Instance() => new("run", 123, "start", Now, Now,
        JsonWorkerInstanceRegistry.ConfigurationPathHash(ConfigPath), "revision", "1", "not parsed", null,
        WorkerInstanceState.Idle, Scheduling: Scheduling(), Progress: new(0, Now));
    private WorkerScheduling Scheduling() => new("continuous", null, WorkerItemIntent.Auto, "Ideas", "Doing", null,
        "claude", WorkspaceMode.Current, root, new Dictionary<string, string>(), null, null, TimeSpan.FromHours(1), null, false);
    private async Task WriteRecord(WorkerInstance instance)
    {
        Directory.CreateDirectory(RecordDirectory);
        await File.WriteAllTextAsync(Path.Combine(RecordDirectory, instance.RunId + ".json"),
            JsonSerializer.Serialize(instance, JsonOptions));
    }
    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}
