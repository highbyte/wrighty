using System.Text.Json;
using Highbyte.Wrighty.Caching;
using Highbyte.Wrighty.Workers;

namespace Highbyte.Wrighty.UnitTests.Cli;

public sealed partial class CliApplicationTests
{
    [Theory]
    [InlineData("drain", WorkerStopMode.Drain, true)]
    [InlineData("interrupt", WorkerStopMode.Interrupt, true)]
    [InlineData("drain", WorkerStopMode.Drain, false)]
    public async Task Control_addresses_one_hosted_run_without_stopping_its_shared_process(string verb, WorkerStopMode mode, bool json)
    {
        var path = Path.Combine(Path.GetTempPath(), $"wrighty-control-{Guid.NewGuid():N}");
        temporarySettingsRoots.Add(path);
        var config = Config with { SourcePath = Path.Combine(path, ".wrighty.json"), SourceRevision = "revision" };
        var registry = new JsonWorkerInstanceRegistry(new CachePaths(path));
        await using var first = await registry.RegisterAsync(config.SourcePath, "revision", "web worker",
            new(WorkerHostKind.WebHosted), CancellationToken.None);
        await using var second = await registry.RegisterAsync(config.SourcePath, "revision", "web worker",
            new(WorkerHostKind.WebHosted), CancellationToken.None);
        var output = new StringWriter();
        var app = Application(new RecordingBackend(failReads: true), new StringReader(""), output,
            config: config, workerInstanceRegistry: registry);

        Assert.Equal(0, await app.InvokeAsync(["workers", "show", first.RunId, "--json"]));
        using (var inspection = JsonDocument.Parse(output.ToString()))
        {
            var entry = Assert.Single(inspection.RootElement.GetProperty("result").GetProperty("localWorkers").EnumerateArray());
            Assert.Equal(first.RunId, entry.GetProperty("instance").GetProperty("runId").GetString());
        }
        output.GetStringBuilder().Clear();
        Assert.Equal(0, await app.InvokeAsync(json
            ? ["workers", verb, first.RunId, "--yes", "--json"]
            : ["workers", verb, first.RunId, "--yes"]));
        if (json)
        {
            using var result = JsonDocument.Parse(output.ToString());
            Assert.True(result.RootElement.GetProperty("result").GetProperty("accepted").GetBoolean());
            Assert.False(result.RootElement.GetProperty("result").GetProperty("completed").GetBoolean());
        }
        else
        {
            Assert.Contains(first.RunId, output.ToString());
            Assert.Contains("completion is not yet verified", output.ToString());
        }
        Assert.Equal(mode, await first.ReadStopRequestAsync(CancellationToken.None));
        Assert.Null(await second.ReadStopRequestAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData("complete", "WORKER_NOT_RUNNING")]
    [InlineData("unavailable", "WORKER_CONTROL_UNAVAILABLE")]
    public async Task Missing_run_preserves_uncertainty_for_inspection_and_control(string coverage, string code)
    {
        IWorkerInstanceRegistry registry = coverage == "complete" ? new DiscoveryRegistry([]) : NoOpWorkerInstanceRegistry.Instance;
        foreach (var args in new[] { new[] { "workers", "show", "missing", "--json" },
            ["workers", "drain", "missing", "--yes", "--json"] })
        {
            var output = new StringWriter();
            Assert.Equal(7, await Application(new RecordingBackend(failReads: true), new StringReader(""), output,
                workerInstanceRegistry: registry, error: output).InvokeAsync(args));
            Assert.Contains(code, output.ToString());
        }
    }

    [Fact]
    public async Task Control_requires_authorization_before_registry_or_tracker_access()
    {
        var output = new StringWriter();
        var registry = new DiscoveryRegistry([]);
        Assert.Equal(2, await Application(new RecordingBackend(failReads: true), new StringReader(""), output,
            workerInstanceRegistry: registry, error: output).InvokeAsync(["workers", "interrupt", "run", "--json"]));
        Assert.Contains("WORKER_CONFIRMATION_REQUIRED", output.ToString());
        Assert.Null(registry.ObservedPath);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Control_rechecks_process_identity_and_rejects_legacy_protocol(bool live, bool modern)
    {
        var path = Path.Combine(Path.GetTempPath(), $"wrighty-control-{Guid.NewGuid():N}");
        temporarySettingsRoots.Add(path);
        var config = Config with { SourcePath = Path.Combine(path, ".wrighty.json") };
        var registry = new JsonWorkerInstanceRegistry(new CachePaths(path));
        await using var run = await registry.RegisterAsync(config.SourcePath, "revision", "worker",
            new(WorkerHostKind.CliProcess, ControlProtocolVersion: modern ? 1 : 0), CancellationToken.None);
        var reader = live ? registry : new JsonWorkerInstanceRegistry(new CachePaths(path),
            observeProcess: _ => new(true, null));
        var output = new StringWriter();
        Assert.Equal(live && modern ? 0 : 7, await Application(new RecordingBackend(failReads: true),
            new StringReader(""), output, config: config, workerInstanceRegistry: reader, error: output)
            .InvokeAsync(["workers", "drain", run.RunId, "--yes", "--json"]));
        var expected = modern ? "WORKER_STOP_REQUESTED" : "WORKER_CONTROL_UNSUPPORTED";
        if (!live) expected = "WORKER_NOT_VERIFIED";
        Assert.Contains(expected, output.ToString());
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public async Task Foreground_run_receipts_report_registration_limits_and_completion(bool registered, bool registrationFails)
    {
        var path = Path.Combine(Path.GetTempPath(), $"wrighty-launch-{Guid.NewGuid():N}");
        temporarySettingsRoots.Add(path);
        var config = Config with { SourcePath = Path.Combine(path, ".wrighty.json"), SourceRevision = "revision" };
        IWorkerInstanceRegistry registry = registered ? new JsonWorkerInstanceRegistry(new CachePaths(path)) : NoOpWorkerInstanceRegistry.Instance;
        if (registrationFails) registry = new FailingWorkerRegistry();
        var output = new StringWriter();
        var app = Application(new RecordingBackend(automaticExecutionAllowed: true), new StringReader(""), output,
            inputRedirected: true, workerCandidate: true, candidateDisappearsAfterPreflight: true,
            config: config, workerInstanceRegistry: registry);
        Assert.Equal(0, await app.InvokeAsync(["worker", "--once", "--yes", "--json"]));
        var events = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonSerializer.Deserialize<JsonElement>(line)).ToArray();
        var launch = Assert.Single(events, e => e.GetProperty("type").GetString() == "worker-run-started");
        var completed = Assert.Single(events, e => e.GetProperty("type").GetString() == "worker-run-completed");
        Assert.Equal(registered, launch.GetProperty("registered").GetBoolean());
        Assert.Equal(registered ? JsonValueKind.String : JsonValueKind.Null, launch.GetProperty("runId").ValueKind);
        Assert.Equal(launch.GetProperty("runId").GetString(), completed.GetProperty("runId").GetString());
        Assert.Equal("foreground-process", launch.GetProperty("owner").GetProperty("kind").GetString());
        Assert.Equal("bounded", launch.GetProperty("scheduling").GetProperty("mode").GetString());
        Assert.Equal(1, launch.GetProperty("scheduling").GetProperty("itemLimit").GetInt32());
        Assert.Equal("finished", completed.GetProperty("reason").GetString());
        Assert.Empty((await registry.InspectAsync(config.SourcePath, CancellationToken.None)).Workers);
    }

    private sealed class FailingWorkerRegistry : IWorkerInstanceRegistry
    {
        public Task<IWorkerInstanceRegistration> RegisterAsync(string configurationPath,
            string configurationRevision, string invocationSummary, CancellationToken cancellationToken) =>
            throw new IOException("Test registry unavailable.");

        public Task<IReadOnlyList<WorkerInstanceStatus>> ListAsync(string configurationPath,
            CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<WorkerInstanceStatus>>([]);
    }
}
