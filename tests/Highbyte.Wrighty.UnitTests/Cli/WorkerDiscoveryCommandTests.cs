using System.Text.Json;
using Highbyte.Wrighty.Workers;

namespace Highbyte.Wrighty.UnitTests.Cli;

public sealed partial class CliApplicationTests
{
    [Fact]
    public async Task Workers_plain_listing_uses_read_only_registry_without_tracker_access()
    {
        var output = new StringWriter();
        var registry = new DiscoveryRegistry([]);
        Assert.Equal(0, await Application(new RecordingBackend(failReads: true), new StringReader(""), output,
            config: Config with { SourcePath = "/configuration/board.json", SourceRevision = "revision" },
            workerInstanceRegistry: registry).InvokeAsync(["workers", "--json"]));
        using var json = JsonDocument.Parse(output.ToString());
        var result = json.RootElement.GetProperty("result");
        Assert.Equal(1, json.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("complete", result.GetProperty("coverage").GetString());
        Assert.Equal(0, result.GetProperty("localWorkers").GetArrayLength());
        Assert.Equal("/configuration/board.json", registry.ObservedPath);
    }

    [Fact]
    public async Task Workers_with_legacy_registry_reports_unavailable_not_empty_coverage()
    {
        var output = new StringWriter();
        Assert.Equal(0, await Application(new RecordingBackend(failReads: true), new StringReader(""), output)
            .InvokeAsync(["workers", "--json"]));
        using var json = JsonDocument.Parse(output.ToString());
        Assert.Equal("unavailable", json.RootElement.GetProperty("result").GetProperty("coverage").GetString());
    }

    [Theory]
    [InlineData("continuous", "could-pick-up", "ELIGIBLE")]
    [InlineData("busy-continuous", "could-pick-up", "ELIGIBLE")]
    [InlineData("busy-once", "cannot-pick-up", "ITEM_LIMIT_REACHED")]
    [InlineData("filtered", "cannot-pick-up", "FILTER_MISMATCH")]
    [InlineData("drift", "unknown", "CONFIGURATION_DRIFT")]
    [InlineData("denied-runtime", "unknown", "ASSESSMENT_UNAVAILABLE")]
    public async Task Workers_item_assessment_uses_selection_and_launch_evidence(string scenario, string outcome, string code)
    {
        var config = Config with { SourceRevision = "revision", SourcePath = "/configuration/board.json" };
        var observed = DateTimeOffset.UtcNow;
        var scheduling = new WorkerScheduling("continuous", null, WorkerItemIntent.Auto,
            config.DefaultPickFrom, config.DefaultPickTo, null, "claude", WorkspaceMode.Current,
            Directory.GetCurrentDirectory(), new Dictionary<string, string>(), null, null, TimeSpan.FromHours(1), null, false);
        var busy = scenario.StartsWith("busy", StringComparison.Ordinal);
        if (scenario == "busy-once") scheduling = scheduling with { Mode = "bounded", ItemLimit = 1 };
        if (scenario == "filtered") scheduling = scheduling with { Filters = new Dictionary<string, string> { ["priority"] = "P99" } };
        var instance = new WorkerInstance("run", 123, "start", observed, observed, "scope",
            scenario == "drift" ? "old-revision" : "revision", "1", "do not parse", busy ? "github:owner/repo#5" : null,
            busy ? WorkerInstanceState.RunningItem : WorkerInstanceState.Idle,
            Scheduling: scheduling, Progress: new(0, observed));
        var registry = new DiscoveryRegistry([new(instance, WorkerInstanceLiveness.Running, null)]);
        var output = new StringWriter();
        var error = new StringWriter();
        var backend = new RecordingBackend(automaticExecutionAllowed: true);
        var app = Application(backend, new StringReader(""), output, error,
            workerCandidate: true, config: config, workerInstanceRegistry: registry,
            runtimeCatalog: scenario == "denied-runtime" ? new DeniedRuntimeCatalog() : new FixedRuntimeCatalog("claude"));
        Assert.Equal(0, await app.InvokeAsync(["workers", "--item", "42", "--json"]));
        using var json = JsonDocument.Parse(output.ToString());
        var result = json.RootElement.GetProperty("result");
        Assert.Equal("github:owner/repo#42", result.GetProperty("itemId").GetString());
        var entry = Assert.Single(result.GetProperty("localWorkers").EnumerateArray());
        Assert.Equal("Running", entry.GetProperty("liveness").GetString());
        Assert.Equal("cli-process", entry.GetProperty("origin").GetString());
        var pickup = entry.GetProperty("pickup");
        Assert.Equal(outcome, pickup.GetProperty("outcome").GetString());
        Assert.Equal(code, pickup.GetProperty("code").GetString());
        Assert.Null(backend.Patch);
        Assert.Null(backend.Operation);
    }

    [Theory]
    [InlineData(false, "complete")]
    [InlineData(true, "possibly-truncated")]
    public async Task List_preserves_item_array_and_labels_count_scope(bool limited, string completeness)
    {
        var output = new StringWriter();
        var args = limited ? new[] { "list", "--limit", "1", "--json" } : ["list", "--json"];
        Assert.Equal(0, await Application(new RecordingBackend(), new StringReader(""), output, workerCandidate: true).InvokeAsync(args));
        using var json = JsonDocument.Parse(output.ToString());
        Assert.Equal(JsonValueKind.Array, json.RootElement.GetProperty("result").ValueKind);
        var listing = json.RootElement.GetProperty("listing");
        Assert.Equal("returned-items", listing.GetProperty("countScope").GetString());
        Assert.Equal(completeness, listing.GetProperty("completeness").GetString());
        Assert.Equal("unknown", listing.GetProperty("statusOrderSource").GetString());
    }

    [Fact]
    public async Task Human_worker_output_preserves_run_identity_scope_and_uncertainty()
    {
        var time = DateTimeOffset.UtcNow;
        var scheduling = new WorkerScheduling("continuous", null, WorkerItemIntent.Auto,
            Config.DefaultPickFrom, Config.DefaultPickTo, null, "claude", WorkspaceMode.Current,
            Directory.GetCurrentDirectory(), new Dictionary<string, string>(), null, null, TimeSpan.FromHours(1), null, false);
        var known = new WorkerInstance("hosted-one", 123, "start", time, time, "scope", "old", "1", "display", null,
            WorkerInstanceState.Idle, WorkerHostKind.WebHosted, Scheduling: scheduling, Progress: new(0, time));
        var unknown = known with { RunId = "hosted-two", Scheduling = null, Progress = null };
        var registry = new DiscoveryRegistry([
            new(known, WorkerInstanceLiveness.Running, null),
            new(unknown, WorkerInstanceLiveness.Unknown, "Process identity could not be verified.")]);
        var output = new StringWriter();
        Assert.Equal(0, await Application(new RecordingBackend(), new StringReader(""), output,
            config: Config with { SourceRevision = "revision" }, workerCandidate: true, workerInstanceRegistry: registry)
            .InvokeAsync(["workers", "--item", "42"]));
        var text = output.ToString();
        Assert.Contains("hosted-one", text);
        Assert.Contains("hosted-two", text);
        Assert.Contains("Unregistered or remote workers may exist", text);
        Assert.Contains("unlimited", text);
        Assert.Contains("Configuration differs", text);
        Assert.Contains("CONFIGURATION_DRIFT", text);
        Assert.Contains("WORKER_NOT_VERIFIED", text);
        Assert.Contains("Process identity could not be verified", text);
    }

    private sealed class DiscoveryRegistry(IReadOnlyList<WorkerInstanceStatus> workers) : IWorkerInstanceRegistry
    {
        public string? ObservedPath { get; private set; }
        public Task<WorkerRegistrySnapshot> InspectAsync(string configurationPath, CancellationToken cancellationToken)
        {
            ObservedPath = configurationPath;
            return Task.FromResult(new WorkerRegistrySnapshot(DateTimeOffset.UtcNow, "scope", "complete", workers));
        }
        public Task<IReadOnlyList<WorkerInstanceStatus>> ListAsync(string configurationPath, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Discovery must not use a listing that may clean up records.");
        public Task<IWorkerInstanceRegistration> RegisterAsync(string configurationPath, string configurationRevision, string invocationSummary, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Discovery must not register a worker.");
    }
}
