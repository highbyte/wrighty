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
    public void Operational_log_can_clear_and_bound_by_encoded_size()
    {
        var log = new HostedWorkerLogBuffer(maximumEntries: 10, maximumBytes: 1);
        log.Add(
            DateTimeOffset.Parse("2026-08-23T10:00:00Z"),
            "info",
            "started",
            "local:1",
            "codex",
            null,
            "Started");

        Assert.Empty(log.Snapshot(0));
        Assert.Equal(1, log.LatestSequence);

        log.Clear();

        Assert.Empty(log.Snapshot(0));
        Assert.Equal(0, log.LatestSequence);
    }

    [Theory]
    [InlineData(WebHostedWorkerState.Stopped, false)]
    [InlineData(WebHostedWorkerState.Starting, true)]
    [InlineData(WebHostedWorkerState.Running, true)]
    [InlineData(WebHostedWorkerState.WaitingForWorkspace, true)]
    [InlineData(WebHostedWorkerState.Draining, true)]
    [InlineData(WebHostedWorkerState.StoppingNow, false)]
    [InlineData(WebHostedWorkerState.Finalizing, false)]
    [InlineData(WebHostedWorkerState.Failed, false)]
    public void Snapshot_reports_whether_a_stop_can_be_requested(
        WebHostedWorkerState state,
        bool expected)
    {
        var snapshot = new HostedWorkerSnapshot(
            state, "run", null, null, null, null, null, 0, []);

        Assert.Equal(expected, snapshot.CanStop);
    }

    [Theory]
    [InlineData("started")]
    [InlineData("resumed")]
    [InlineData("running")]
    [InlineData("session")]
    public void Running_events_project_the_active_item(string type)
    {
        var projected = HostedWorkerEventProjection.Apply(
            WebHostedWorkerState.Stopped,
            null,
            null,
            new WorkerEvent(type, "local:42", "codex"));

        Assert.Equal(WebHostedWorkerState.Running, projected.State);
        Assert.Equal("local:42", projected.ItemId);
        Assert.Equal("codex", projected.Agent);
    }

    [Theory]
    [InlineData("finished")]
    [InlineData("needs-attention")]
    [InlineData("failed")]
    [InlineData("fenced")]
    [InlineData("timed-out")]
    [InlineData("rejected")]
    [InlineData("retry-scheduled")]
    [InlineData("interrupted")]
    public void Terminal_events_clear_the_active_item(string type)
    {
        var projected = HostedWorkerEventProjection.Apply(
            WebHostedWorkerState.Running,
            "local:42",
            "codex",
            new WorkerEvent(type, "local:42", "codex"));

        Assert.Equal(WebHostedWorkerState.Running, projected.State);
        Assert.Null(projected.ItemId);
        Assert.Null(projected.Agent);
    }

    [Fact]
    public void Event_projection_preserves_stopping_states_and_tracks_workspace_waiting()
    {
        var waiting = HostedWorkerEventProjection.Apply(
            WebHostedWorkerState.Running,
            null,
            null,
            new WorkerEvent("workspace-busy"));
        var ready = HostedWorkerEventProjection.Apply(
            waiting.State,
            null,
            null,
            new WorkerEvent("idle"));
        var draining = HostedWorkerEventProjection.Apply(
            WebHostedWorkerState.Draining,
            "local:42",
            "codex",
            new WorkerEvent("started", "local:43", "claude"));
        var finalizing = HostedWorkerEventProjection.Apply(
            WebHostedWorkerState.StoppingNow,
            "local:42",
            "codex",
            new WorkerEvent("interrupted"));
        var unchanged = HostedWorkerEventProjection.Apply(
            WebHostedWorkerState.Finalizing,
            "local:42",
            "codex",
            new WorkerEvent("unknown"));

        Assert.Equal(WebHostedWorkerState.WaitingForWorkspace, waiting.State);
        Assert.Equal(WebHostedWorkerState.Running, ready.State);
        Assert.Equal(WebHostedWorkerState.Draining, draining.State);
        Assert.Equal("local:43", draining.ItemId);
        Assert.Equal(WebHostedWorkerState.Finalizing, finalizing.State);
        Assert.Null(finalizing.ItemId);
        Assert.Equal(WebHostedWorkerState.Finalizing, unchanged.State);
        Assert.Equal("local:42", unchanged.ItemId);
    }

    [Theory]
    [InlineData("finished", "success")]
    [InlineData("workspace-busy", "warning")]
    [InlineData("failed", "danger")]
    [InlineData("idle", "muted")]
    [InlineData("started", "info")]
    [InlineData("unknown", "info")]
    public void Event_levels_use_the_shared_semantics(string type, string expected)
    {
        Assert.Equal(
            expected,
            HostedWorkerEventProjection.Level(new WorkerEvent(type)));
    }

    [Theory]
    [InlineData("idle", "  retry soon  ", "retry soon")]
    [InlineData("started", null, "The agent session started.")]
    [InlineData("resumed", null, "The retained agent session resumed.")]
    [InlineData("running", null, "The agent session is running.")]
    [InlineData("session", null, "The agent session is running.")]
    [InlineData("finished", null, "The item finished.")]
    [InlineData("needs-attention", null, "The item needs operator attention.")]
    [InlineData("failed", null, "The agent session failed.")]
    [InlineData("fenced", null, "The worker lost claim ownership.")]
    [InlineData("timed-out", null, "The agent session timed out.")]
    [InlineData("rejected", null, "The agent session was rejected.")]
    [InlineData("unknown", null, null)]
    public void Event_messages_retain_only_bounded_operational_detail(
        string type,
        string? message,
        string? expected)
    {
        Assert.Equal(
            expected,
            HostedWorkerEventProjection.SafeEventMessage(
                new WorkerEvent(type, Message: message)));
    }

    [Fact]
    public void Interrupted_event_message_distinguishes_operator_and_host()
    {
        var operatorMessage = HostedWorkerEventProjection.SafeEventMessage(
            new WorkerEvent("interrupted", Outcome: AgentOutcome.InterruptedByOperator));
        var hostMessage = HostedWorkerEventProjection.SafeEventMessage(
            new WorkerEvent("interrupted", Outcome: AgentOutcome.InterruptedByHostShutdown));

        Assert.Contains("operator", operatorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("web host", hostMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Log_sanitizers_remove_controls_and_bound_values()
    {
        Assert.Null(HostedWorkerEventProjection.SafeToken(" \t "));
        Assert.Null(HostedWorkerEventProjection.SafeMessage(null));
        Assert.Equal("ab", HostedWorkerEventProjection.SafeToken(" a\0b "));
        Assert.Equal("a\nb", HostedWorkerEventProjection.SafeMessage(" a\nb "));
        Assert.Equal(100, HostedWorkerEventProjection.SafeToken(new string('x', 101))!.Length);
        var longMessage = HostedWorkerEventProjection.SafeMessage(new string('x', 501));
        Assert.Equal(500, longMessage!.Length);
        Assert.EndsWith("…", longMessage, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("worktree", WorkspaceMode.Worktree)]
    [InlineData("WORKTREE", WorkspaceMode.Worktree)]
    [InlineData("shared", WorkspaceMode.Shared)]
    [InlineData("current", WorkspaceMode.Current)]
    [InlineData(null, WorkspaceMode.Current)]
    public void Workspace_mode_projection_handles_all_configured_values(
        string? value,
        WorkspaceMode expected)
    {
        Assert.Equal(expected, HostedWorkerEventProjection.WorkspaceMode(value));
    }

    [Fact]
    public async Task Unavailable_supervisor_rejects_commands_and_has_no_runs()
    {
        var state = new WebApplicationState(
            new TrackerConfig(),
            "token",
            directory);
        var supervisor = new WebHostedWorkerSupervisor(
            null,
            NoOpWorkerInstanceRegistry.Instance,
            state);

        var started = await supervisor.StartAsync();

        Assert.False(supervisor.Available);
        Assert.False(started.Accepted);
        Assert.Equal("HOSTED_WORKER_UNAVAILABLE", started.Code);
        Assert.Empty(supervisor.Snapshots());
        Assert.Null(supervisor.Snapshot("missing"));
        Assert.False(supervisor.Owns("missing"));
        Assert.False(supervisor.RequestDrain("missing").Accepted);
        Assert.False(supervisor.RequestInterrupt("missing").Accepted);
        await supervisor.StopForHostShutdownAsync(TimeSpan.FromMilliseconds(10));
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
                Statuses = ["Todo", "Worker queue", "In Progress", "Done"]
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
