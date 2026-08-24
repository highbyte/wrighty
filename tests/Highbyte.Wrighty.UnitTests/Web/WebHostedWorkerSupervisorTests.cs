using Highbyte.Wrighty.Backends;
using Highbyte.Wrighty.Caching;
using Highbyte.Wrighty.Configuration;
using Highbyte.Wrighty.Identity;
using Highbyte.Wrighty.LocalMarkdown;
using Highbyte.Wrighty.Web;
using Highbyte.Wrighty.Workers;

namespace Highbyte.Wrighty.UnitTests.Web;

public sealed class WebHostedWorkerSupervisorTests : IDisposable
{
    private readonly string directory = Path.Combine(
        Path.GetTempPath(),
        $"wrighty-web-hosted-worker-{Guid.NewGuid():N}");

    [Fact]
    public void Operational_log_consolidates_idle_heartbeats_and_bounds_history()
    {
        var log = new HostedWorkerLogBuffer(maximumEntries: 3, maximumBytes: int.MaxValue);
        var now = DateTimeOffset.Parse("2026-08-23T10:00:00Z");

        log.Add(now, "info", "idle", null, null, null, "Retrying in 2s.");
        log.Add(now.AddSeconds(2), "info", "idle", null, null, null, "Retrying in 4s.");
        var consolidated = Assert.Single(log.Snapshot(0));
        Assert.Equal(2, consolidated.Sequence);
        Assert.Equal("Retrying in 4s.", consolidated.Message);

        log.Add(now.AddSeconds(3), "info", "started", "local:1", "codex", null, null);
        log.Add(now.AddSeconds(4), "success", "finished", "local:1", "codex", "Succeeded", null);
        log.Add(now.AddSeconds(5), "info", "idle", null, null, null, "Retrying in 2s.");

        var retained = log.Snapshot(0);
        Assert.Equal(3, retained.Count);
        Assert.DoesNotContain(retained, entry => entry.Sequence == 2);
        Assert.Equal(5, log.LatestSequence);
    }

    [Fact]
    public async Task Multiple_hosted_starts_are_registered_alongside_an_existing_external_worker()
    {
        Directory.CreateDirectory(directory);
        var config = new TrackerConfig
        {
            Backend = "local-markdown",
            SourcePath = Path.Combine(directory, TrackerConfigLoader.FileName),
            DefaultPickFrom = "Worker queue",
            DefaultPickTo = "In Progress",
            DefaultFinishTo = "Done",
            LocalMarkdown = new LocalMarkdownBackendConfig
            {
                Path = ".wrighty",
                Statuses = ["Todo", "Worker queue", "In Progress", "Done"]
            },
            Worker = new WorkerConfig
            {
                DefaultAgent = "codex",
                UseWorkerQueue = true
            }
        };
        var configurations = new TrackerConfigLoader();
        await configurations.SaveAsync(config.SourcePath, config, CancellationToken.None);
        var backend = new LocalMarkdownTrackerBackend(
            new FixedIdentity(),
            new Highbyte.Wrighty.Time.SystemClock());
        await backend.InitializeAsync(config, checkOnly: false, CancellationToken.None);
        var tracker = new Highbyte.Wrighty.TrackerService(
            new TrackerBackendRegistry([backend]));
        var worker = new WorkerService(
            tracker,
            new FailIfRunAgent(),
            new CurrentWorkspace(),
            [new CodexAgentAdapter()],
            (_, cancellationToken) => Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken));
        var registry = new JsonWorkerInstanceRegistry(
            new CachePaths(Path.Combine(directory, ".cache")),
            heartbeatInterval: TimeSpan.FromMinutes(5));
        await using var external = await registry.RegisterAsync(
            config.SourcePath,
            "revision",
            "wrighty worker --agent codex",
            new WorkerRegistrationMetadata(WorkerHostKind.CliProcess),
            CancellationToken.None);
        var revision = await RepositoryConfigurationService.RevisionAsync(
            config.SourcePath,
            CancellationToken.None);
        var applicationState = new WebApplicationState(
            config,
            "token",
            directory,
            activeConfigurationRevision: revision);
        var supervisor = new WebHostedWorkerSupervisor(
            worker,
            registry,
            applicationState);

        var first = await supervisor.StartAsync();
        var updated = config with
        {
            Worker = config.Worker! with { WorkspaceMode = "shared" }
        };
        await configurations.SaveAsync(config.SourcePath, updated, CancellationToken.None);
        var updatedRevision = await RepositoryConfigurationService.RevisionAsync(
            config.SourcePath,
            CancellationToken.None);
        Assert.True(applicationState.TryApplyConfiguration(updated, updatedRevision));
        var second = await supervisor.StartAsync();

        Assert.True(first.Accepted);
        Assert.True(second.Accepted);
        var firstRunId = Assert.IsType<string>(first.RunId);
        var secondRunId = Assert.IsType<string>(second.RunId);
        Assert.NotEqual(firstRunId, secondRunId);
        Assert.Equal(2, supervisor.Snapshots().Count);
        var active = await registry.ListAsync(config.SourcePath, CancellationToken.None);
        Assert.Equal(3, active.Count);
        Assert.Contains(active, value =>
            value.Instance.RunId == external.RunId &&
            value.Instance.HostKind == WorkerHostKind.CliProcess);
        Assert.Contains(active, value =>
            value.Instance.RunId == firstRunId &&
            value.Instance.HostKind == WorkerHostKind.WebHosted &&
            value.Instance.ConfigurationRevision == revision);
        Assert.Contains(active, value =>
            value.Instance.RunId == secondRunId &&
            value.Instance.HostKind == WorkerHostKind.WebHosted &&
            value.Instance.ConfigurationRevision == updatedRevision);
        Assert.NotEqual(external.RunId, firstRunId);
        Assert.NotEqual(external.RunId, secondRunId);

        Assert.True(supervisor.RequestDrain(firstRunId).Accepted);
        await WaitUntilAsync(() =>
            supervisor.Snapshot(firstRunId)?.State == WebHostedWorkerState.Stopped);
        Assert.True(supervisor.Owns(secondRunId));
        Assert.True(supervisor.RequestDrain(secondRunId).Accepted);
        await WaitUntilAsync(() =>
            supervisor.Snapshot(secondRunId)?.State == WebHostedWorkerState.Stopped);
        Assert.Equal(2, supervisor.Snapshots().Count);
        var remaining = Assert.Single(
            await registry.ListAsync(config.SourcePath, CancellationToken.None));
        Assert.Equal(external.RunId, remaining.Instance.RunId);
    }

    [Fact]
    public async Task Hosted_worker_reports_when_it_is_waiting_for_the_current_workspace()
    {
        Directory.CreateDirectory(directory);
        var config = new TrackerConfig
        {
            Backend = "local-markdown",
            SourcePath = Path.Combine(directory, TrackerConfigLoader.FileName),
            DefaultPickFrom = "Worker queue",
            LocalMarkdown = new LocalMarkdownBackendConfig
            {
                Path = ".wrighty",
                Statuses = ["Worker queue", "In Progress", "Done"]
            },
            Worker = new WorkerConfig { DefaultAgent = "codex", UseWorkerQueue = false }
        };
        await new TrackerConfigLoader().SaveAsync(
            config.SourcePath,
            config,
            CancellationToken.None);
        var backend = new LocalMarkdownTrackerBackend(
            new FixedIdentity(),
            new Highbyte.Wrighty.Time.SystemClock());
        await backend.InitializeAsync(config, checkOnly: false, CancellationToken.None);
        var registry = new JsonWorkerInstanceRegistry(
            new CachePaths(Path.Combine(directory, ".cache")),
            heartbeatInterval: TimeSpan.FromMinutes(5));
        var worker = new WorkerService(
            new Highbyte.Wrighty.TrackerService(new TrackerBackendRegistry([backend])),
            new FailIfRunAgent(),
            new CurrentWorkspace(),
            [new CodexAgentAdapter()],
            (_, cancellationToken) => Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken),
            workspaceExecutionLock: new AlwaysBusyWorkspaceLock());
        var revision = await RepositoryConfigurationService.RevisionAsync(
            config.SourcePath,
            CancellationToken.None);
        var supervisor = new WebHostedWorkerSupervisor(
            worker,
            registry,
            new WebApplicationState(config, "token", directory, activeConfigurationRevision: revision));

        var started = await supervisor.StartAsync();
        var runId = Assert.IsType<string>(started.RunId);

        await WaitUntilAsync(() =>
            supervisor.Snapshot(runId)?.State == WebHostedWorkerState.WaitingForWorkspace);
        Assert.Contains(
            supervisor.Snapshot(runId)!.Log,
            value => value.Type == "workspace-busy");

        Assert.True(supervisor.RequestDrain(runId).Accepted);
        await WaitUntilAsync(() =>
            supervisor.Snapshot(runId)?.State == WebHostedWorkerState.Stopped);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition())
            await Task.Delay(10, timeout.Token);
    }

    public void Dispose()
    {
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }

    private sealed class FixedIdentity : IInstallationIdentityProvider
    {
        public Task<string> GetInstallationIdAsync(CancellationToken cancellationToken) =>
            Task.FromResult("web-hosted-worker-test");
    }

    private sealed class CurrentWorkspace : IWorkspaceManager
    {
        public Task<Workspace> PrepareAsync(
            WorkspaceRequest request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new Workspace(Path.GetFullPath(request.RepositoryPath)));
    }

    private sealed class FailIfRunAgent : IAgentProcessRunner
    {
        public Task<AgentRunResult> RunAsync(
            AgentInvocation invocation,
            IAgentAdapter adapter,
            TimeSpan timeout,
            IReadOnlyDictionary<string, string> grantEnvironment,
            Func<string, CancellationToken, Task>? sessionStarted,
            bool killOnCancellation,
            CancellationToken cancellationToken) =>
            throw new Xunit.Sdk.XunitException("No agent should run for an empty tracker.");
    }

    private sealed class AlwaysBusyWorkspaceLock : IWorkspaceExecutionLock
    {
        public ValueTask<IAsyncDisposable> AcquireAsync(
            string workspacePath,
            CancellationToken cancellationToken) =>
            ValueTask.FromException<IAsyncDisposable>(new Highbyte.Wrighty.Errors.TrackerException(
                "WORKSPACE_BUSY",
                "The workspace is busy.",
                7));
    }
}
