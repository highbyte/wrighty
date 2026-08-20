using Highbyte.Wrighty.AgentContext;
using Highbyte.Wrighty.ApprovedContext;
using Highbyte.Wrighty.Claims;
using Highbyte.Wrighty.Configuration;
using Highbyte.Wrighty.Errors;
using Highbyte.Wrighty.Identity;
using Highbyte.Wrighty.LocalMarkdown;
using Highbyte.Wrighty.Models;
using Highbyte.Wrighty.Time;
using Highbyte.Wrighty.Backends;
using Highbyte.Wrighty.Workers;
using Highbyte.Wrighty;

namespace Highbyte.Wrighty.UnitTests.Workers;

public sealed class LocalDispatchStateTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), $"wrighty-worker-{Guid.NewGuid():N}");
    private readonly FakeClock clock = new(DateTimeOffset.Parse("2026-07-17T10:00:00Z"));

    [Fact]
    public async Task Managed_eligibility_and_fenced_renewal_persist_workspace_and_session()
    {
        var backend = new LocalMarkdownTrackerBackend(new FakeIdentity(), clock);
        var config = new TrackerConfig
        {
            Backend = "local-markdown",
            DefaultPickFrom = "Todo",
            Worker = new WorkerConfig { RequirementsAssessment = new WorkerRequirementsAssessmentConfig { Mode = "inline" }, UseWorkerQueue = false },
            SourcePath = Path.Combine(directory, ".wrighty.json"),
            LocalMarkdown = new LocalMarkdownBackendConfig(),
            LeaseMinutes = 60
        };
        await backend.InitializeAsync(config, false, CancellationToken.None);
        var created = await backend.CreateAsync(config, new CreateWorkItemOperation(
            new CreateWorkItemRequest("Automate me", "Body", "Todo", "P1",
                AutomaticExecutionAllowed: true, AgentPolicy: "claude"), false), CancellationToken.None);
        var detail = await backend.GetAsync(config, created.Id, CancellationToken.None);
        Assert.True(detail!.AutomaticExecutionAllowed);
        Assert.Equal("claude", detail.AgentPolicy);

        var context = new AgentExecutionContext("claude", null, AgentContextSource.ExplicitOption,
            ClaimantKind: ClaimantKind.Agent, ClaimantId: "agent:worker:test");
        var claim = await backend.TryClaimAsync(config, created.Id, context, CancellationToken.None);
        var handle = new ClaimHandle(context, claim.ClaimToken);
        clock.UtcNow = clock.UtcNow.AddMinutes(31);
        var renewed = await backend.RenewClaimAsync(config, created.Id, handle,
            "/tmp/wrighty-tree", "session-42", CancellationToken.None);

        Assert.Equal(claim.ClaimToken, renewed.ClaimToken);
        Assert.Equal("/tmp/wrighty-tree", renewed.WorkspacePath);
        Assert.Equal("session-42", renewed.SessionId);
        Assert.Equal(clock.UtcNow.AddMinutes(60), renewed.ExpiresAt);

        var stale = new ClaimHandle(context with { ClaimantId = "agent:other" }, claim.ClaimToken);
        var exception = await Assert.ThrowsAsync<Highbyte.Wrighty.Errors.TrackerException>(() =>
            backend.RenewClaimAsync(config, created.Id, stale, null, null, CancellationToken.None));
        Assert.Equal("CLAIM_STALE", exception.Code);
    }

    [Fact]
    public async Task Worker_passes_exact_grant_and_never_renews_past_fixed_timeout_budget()
    {
        var backend = new LocalMarkdownTrackerBackend(new FakeIdentity(), clock);
        var config = new TrackerConfig
        {
            Backend = "local-markdown",
            DefaultPickFrom = "Todo",
            Worker = new WorkerConfig { RequirementsAssessment = new WorkerRequirementsAssessmentConfig { Mode = "inline" }, UseWorkerQueue = false },
            SourcePath = Path.Combine(directory, ".wrighty.json"),
            LocalMarkdown = new LocalMarkdownBackendConfig(),
            LeaseMinutes = 60
        };
        await backend.InitializeAsync(config, false, CancellationToken.None);
        await backend.CreateAsync(config, new CreateWorkItemOperation(
            new CreateWorkItemRequest("Hung item", "Body", "Todo", "P1",
                AutomaticExecutionAllowed: true, AgentPolicy: "claude"), false), CancellationToken.None);
        var runner = new HungRunner();
        var delays = new List<TimeSpan>();
        var events = new List<WorkerEvent>();
        var tracker = new TrackerService(new TrackerBackendRegistry([backend]));
        var worker = new WorkerService(tracker, runner, new CurrentWorkspace(),
            [new ClaudeAgentAdapter()],
            (duration, _) =>
            {
                delays.Add(duration);
                clock.UtcNow += duration;
                return Task.CompletedTask;
            },
            () => clock.UtcNow);

        var summary = await worker.RunAsync(config,
            new WorkerOptions("claude", true, null, WorkspaceMode.Current,
                new Dictionary<string, string>(), null, TimeSpan.FromMinutes(10),
                FencedAction.Kill, null, "agent", false, false),
            directory, value =>
            {
                events.Add(value);
                return Task.CompletedTask;
            }, CancellationToken.None);

        Assert.Equal(1, summary.Processed);
        Assert.Equal(1, summary.Failed);
        Assert.Equal([TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5)], delays);
        var heartbeat = Assert.Single(events, value => value.Type == "running");
        Assert.Equal(TimeSpan.FromMinutes(5), heartbeat.Elapsed);
        Assert.Equal(TimeSpan.FromMinutes(5), heartbeat.TimeoutRemaining);
        Assert.Equal(DateTimeOffset.Parse("2026-07-17T10:10:00Z"), heartbeat.TimeoutAt);
        Assert.Equal(DateTimeOffset.Parse("2026-07-17T10:05:00Z"), heartbeat.OccurredAt);
        Assert.Equal("current", heartbeat.WorkspaceMode);
        Assert.Contains("5m elapsed", heartbeat.Message);
        Assert.Contains("timeout in 5m", heartbeat.Message);
        Assert.NotNull(runner.Environment);
        Assert.StartsWith("agent:worker:", runner.Environment!["WRIGHTY_CLAIMANT_ID"]);
        Assert.False(string.IsNullOrWhiteSpace(runner.Environment["WRIGHTY_CLAIM_TOKEN"]));
        Assert.Equal(
            Path.Combine(directory, ".wrighty.json"),
            runner.Environment[TrackerConfigLoader.ConfigPathEnvironmentVariable]);
        Assert.Equal(ClaimOwnershipState.Unclaimed,
            (await backend.GetClaimOwnershipAsync(config, new WorkItemId("local:1"),
                CancellationToken.None)).State);
    }

    [Fact]
    public async Task Reprocessing_same_item_uses_a_new_preassigned_session_handle()
    {
        var backend = new LocalMarkdownTrackerBackend(new FakeIdentity(), clock);
        var config = new TrackerConfig
        {
            Backend = "local-markdown",
            DefaultPickFrom = "Todo",
            Worker = new WorkerConfig { RequirementsAssessment = new WorkerRequirementsAssessmentConfig { Mode = "inline" }, UseWorkerQueue = false },
            SourcePath = Path.Combine(directory, ".wrighty.json"),
            LocalMarkdown = new LocalMarkdownBackendConfig(),
            LeaseMinutes = 60
        };
        await backend.InitializeAsync(config, false, CancellationToken.None);
        await backend.CreateAsync(config, new CreateWorkItemOperation(
            new CreateWorkItemRequest("Retry item", "Body", "Todo", "P1",
                AutomaticExecutionAllowed: true, AgentPolicy: "claude"), false), CancellationToken.None);
        var runner = new CapturingRejectedRunner();
        var worker = new WorkerService(
            new TrackerService(new TrackerBackendRegistry([backend])),
            runner,
            new CurrentWorkspace(),
            [new ClaudeAgentAdapter()],
            clock: () => clock.UtcNow);
        var options = new WorkerOptions(
            "claude",
            true,
            null,
            WorkspaceMode.Current,
            new Dictionary<string, string>(),
            null,
            TimeSpan.FromMinutes(10),
            FencedAction.Kill,
            "agent:stable-worker",
            "agent",
            false,
            false,
            "Todo",
            "Todo");

        var first = await worker.RunAsync(
            config, options, directory, _ => Task.CompletedTask, CancellationToken.None);
        var second = await worker.RunAsync(
            config, options, directory, _ => Task.CompletedTask, CancellationToken.None);

        Assert.Equal(1, first.Failed);
        Assert.Equal(1, second.Failed);
        Assert.Equal(2, runner.SessionIds.Count);
        Assert.NotEqual(runner.SessionIds[0], runner.SessionIds[1]);
        Assert.All(runner.SessionIds, value => Assert.True(Guid.TryParse(value, out _)));
    }

    [Fact]
    public async Task Process_start_failure_restores_status_cleans_workspace_and_releases_exact_claim()
    {
        var backend = new LocalMarkdownTrackerBackend(new FakeIdentity(), clock);
        var config = new TrackerConfig
        {
            Backend = "local-markdown",
            DefaultPickFrom = "Todo",
            Worker = new WorkerConfig { RequirementsAssessment = new WorkerRequirementsAssessmentConfig { Mode = "inline" }, UseWorkerQueue = false },
            SourcePath = Path.Combine(directory, ".wrighty.json"),
            LocalMarkdown = new LocalMarkdownBackendConfig(),
            LeaseMinutes = 60
        };
        await backend.InitializeAsync(config, false, CancellationToken.None);
        var created = await backend.CreateAsync(config, new CreateWorkItemOperation(
            new CreateWorkItemRequest(
                "Start failure",
                "Body",
                "Todo",
                "P1",
                AutomaticExecutionAllowed: true,
                AgentPolicy: "claude"),
            false),
            CancellationToken.None);
        var workspaces = new TrackingWorktree(Path.Combine(directory, "worktree"));
        var events = new List<WorkerEvent>();
        var worker = new WorkerService(
            new TrackerService(new TrackerBackendRegistry([backend])),
            new StartFailureRunner(),
            workspaces,
            [new ClaudeAgentAdapter()],
            clock: () => clock.UtcNow);
        var options = new WorkerOptions(
            "claude",
            true,
            null,
            WorkspaceMode.Worktree,
            new Dictionary<string, string>(),
            null,
            TimeSpan.FromMinutes(10),
            FencedAction.Kill,
            null,
            "agent",
            false,
            false);

        var summary = await worker.RunItemAsync(
            config,
            options,
            directory,
            created.Id,
            WorkerItemIntent.Fresh,
            null,
            value =>
            {
                events.Add(value);
                return Task.CompletedTask;
            },
            CancellationToken.None);

        Assert.Equal(1, summary.Failed);
        Assert.Equal(1, workspaces.CleanupCalls);
        var detail = await backend.GetAsync(config, created.Id, CancellationToken.None);
        Assert.Equal("Todo", detail!.Status);
        Assert.Equal(
            ClaimOwnershipState.Unclaimed,
            (await backend.GetClaimOwnershipAsync(
                config,
                created.Id,
                CancellationToken.None)).State);
        var failed = Assert.Single(events, value => value.Type == "failed");
        Assert.Contains("AGENT_START_FAILED", failed.Message);
        Assert.Contains("exact claim generation was released", failed.Message);
    }

    [Fact]
    public async Task General_worker_skips_an_unavailable_assignment_and_runs_a_later_compatible_item()
    {
        var backend = new LocalMarkdownTrackerBackend(new FakeIdentity(), clock);
        var config = new TrackerConfig
        {
            Backend = "local-markdown",
            DefaultPickFrom = "Todo",
            Worker = new WorkerConfig { RequirementsAssessment = new WorkerRequirementsAssessmentConfig { Mode = "inline" }, UseWorkerQueue = false },
            SourcePath = Path.Combine(directory, ".wrighty.json"),
            LocalMarkdown = new LocalMarkdownBackendConfig(),
            LeaseMinutes = 60
        };
        await backend.InitializeAsync(config, false, CancellationToken.None);
        await backend.CreateAsync(config, new CreateWorkItemOperation(
            new CreateWorkItemRequest(
                "Needs Claude", "Body", "Todo", "P1",
                AutomaticExecutionAllowed: true, AgentPolicy: "claude"),
            false), CancellationToken.None);
        await backend.CreateAsync(config, new CreateWorkItemOperation(
            new CreateWorkItemRequest(
                "Can use Codex", "Body", "Todo", "P1",
                AutomaticExecutionAllowed: true, AgentPolicy: "codex"),
            false), CancellationToken.None);
        var events = new List<WorkerEvent>();
        var runtimes = new MutableRuntimeCatalog("codex");
        var worker = new WorkerService(
            new TrackerService(new TrackerBackendRegistry([backend])),
            new SuccessfulRunner(),
            new CurrentWorkspace(),
            [new ClaudeAgentAdapter(), new CodexAgentAdapter()],
            clock: () => clock.UtcNow,
            runtimeCatalog: runtimes);
        var options = new WorkerOptions(
            null, true, null, WorkspaceMode.Current,
            new Dictionary<string, string>(), null, TimeSpan.FromMinutes(10),
            FencedAction.Kill, null, "agent", false, false);

        var summary = await worker.RunAsync(
            config,
            options,
            directory,
            value =>
            {
                events.Add(value);
                return Task.CompletedTask;
            },
            CancellationToken.None);

        Assert.Equal(1, summary.Processed);
        Assert.Equal("codex", Assert.Single(events, value => value.Type == "started").Agent);
        Assert.Equal(
            ClaimOwnershipState.Unclaimed,
            (await backend.GetClaimOwnershipAsync(
                config,
                new WorkItemId("local:1"),
                CancellationToken.None)).State);
    }

    [Fact]
    public async Task Worker_preflight_refreshes_a_previously_missing_executable()
    {
        var backend = new LocalMarkdownTrackerBackend(new FakeIdentity(), clock);
        var config = new TrackerConfig
        {
            Backend = "local-markdown",
            DefaultPickFrom = "Todo",
            Worker = new WorkerConfig { RequirementsAssessment = new WorkerRequirementsAssessmentConfig { Mode = "inline" }, UseWorkerQueue = false },
            SourcePath = Path.Combine(directory, ".wrighty.json"),
            LocalMarkdown = new LocalMarkdownBackendConfig()
        };
        await backend.InitializeAsync(config, false, CancellationToken.None);
        await backend.CreateAsync(config, new CreateWorkItemOperation(
            new CreateWorkItemRequest(
                "Needs Claude", "Body", "Todo", "P1",
                AutomaticExecutionAllowed: true, AgentPolicy: "claude"),
            false), CancellationToken.None);
        var runtimes = new MutableRuntimeCatalog("codex");
        var worker = new WorkerService(
            new TrackerService(new TrackerBackendRegistry([backend])),
            new FailIfRunRunner(),
            new CurrentWorkspace(),
            [new ClaudeAgentAdapter(), new CodexAgentAdapter()],
            clock: () => clock.UtcNow,
            runtimeCatalog: runtimes);
        var options = new WorkerOptions(
            null, true, null, WorkspaceMode.Current,
            new Dictionary<string, string>(), null, TimeSpan.FromMinutes(10),
            FencedAction.Kill, null, "agent", false, false);

        Assert.False(await worker.PreflightAsync(
            config, options, directory, _ => Task.CompletedTask, CancellationToken.None));
        runtimes.Installed.Add("claude");
        Assert.True(await worker.PreflightAsync(
            config, options, directory, _ => Task.CompletedTask, CancellationToken.None));
    }

    [Theory]
    [InlineData(AgentFailureKind.UsageExhausted, true)]
    [InlineData(AgentFailureKind.PermissionDenied, false)]
    public async Task Session_failing_after_the_agent_finished_reports_the_item_as_finished(
        AgentFailureKind kind,
        bool retryable)
    {
        var backend = new LocalMarkdownTrackerBackend(new FakeIdentity(), clock);
        var config = new TrackerConfig
        {
            Backend = "local-markdown",
            DefaultPickFrom = "Todo",
            Worker = new WorkerConfig { RequirementsAssessment = new WorkerRequirementsAssessmentConfig { Mode = "inline" }, UseWorkerQueue = false },
            SourcePath = Path.Combine(directory, ".wrighty.json"),
            LocalMarkdown = new LocalMarkdownBackendConfig(),
            LeaseMinutes = 60
        };
        await backend.InitializeAsync(config, false, CancellationToken.None);
        var created = await backend.CreateAsync(config, new CreateWorkItemOperation(
            new CreateWorkItemRequest("Finished then limited", "Body", "Todo", "P1",
                AutomaticExecutionAllowed: true, AgentPolicy: "claude"), false),
            CancellationToken.None);
        var tracker = new TrackerService(new TrackerBackendRegistry([backend]));
        var events = new List<WorkerEvent>();
        var failure = new AgentFailure(
            kind, "usage_exhausted", null, null, retryable,
            AgentFailureConfidence.Authoritative, "Agent usage is exhausted.");
        var worker = new WorkerService(
            tracker,
            new FinishThenFailRunner(tracker, config, created.Id, failure),
            new CurrentWorkspace(),
            [new ClaudeAgentAdapter()],
            clock: () => clock.UtcNow);
        var options = new WorkerOptions(
            "claude", true, null, WorkspaceMode.Current,
            new Dictionary<string, string>(), null, TimeSpan.FromMinutes(10),
            FencedAction.Kill, null, "agent", false, false);

        var summary = await worker.RunAsync(
            config, options, directory,
            value =>
            {
                events.Add(value);
                return Task.CompletedTask;
            },
            CancellationToken.None);

        // The tracked work landed, so the run must not be recovered through a claim the agent
        // already released — that write fails with CLAIM_REQUIRED and leaves a completed item
        // recorded as failed.
        Assert.Equal(0, summary.Failed);
        Assert.Equal(0, summary.NeedsAttention);
        var finished = Assert.Single(events, value => value.Type == "finished");
        Assert.DoesNotContain(events, value =>
            value.Type is "retry-scheduled" or "needs-attention" or "failed");
        // The capacity condition is still reported: it describes how the session ended, not the
        // item's outcome.
        Assert.Equal(kind, finished.Failure!.Kind);

        var item = await backend.GetAsync(config, created.Id, CancellationToken.None);
        Assert.Equal(config.DefaultFinishTo, item!.Status);
        Assert.Null(item.DispatchState);
    }

    [Fact]
    public async Task A_finish_whose_release_was_denied_is_completed_by_the_worker()
    {
        // The sandboxed-agent case (issue #85): the agent's `wrighty finish` applied the target
        // status but was denied the claim release, and the vendor process then exited normally.
        // Retaining that claim would leave a finished item claimed, needs-attention, and outside
        // the active status the continuation scan reads — so the worker must complete the finish
        // with its own handle instead.
        var backend = new LocalMarkdownTrackerBackend(new FakeIdentity(), clock);
        var config = new TrackerConfig
        {
            Backend = "local-markdown",
            DefaultPickFrom = "Todo",
            Worker = new WorkerConfig { RequirementsAssessment = new WorkerRequirementsAssessmentConfig { Mode = "inline" }, UseWorkerQueue = false },
            SourcePath = Path.Combine(directory, ".wrighty.json"),
            LocalMarkdown = new LocalMarkdownBackendConfig(),
            LeaseMinutes = 60
        };
        await backend.InitializeAsync(config, false, CancellationToken.None);
        var created = await backend.CreateAsync(config, new CreateWorkItemOperation(
            new CreateWorkItemRequest("Finished but still claimed", "Body", "Todo", "P1",
                AutomaticExecutionAllowed: true, AgentPolicy: "claude"), false),
            CancellationToken.None);
        var tracker = new TrackerService(new TrackerBackendRegistry([backend]));
        var events = new List<WorkerEvent>();
        var worker = new WorkerService(
            tracker,
            new StatusOnlyFinishRunner(tracker, config, created.Id),
            new CurrentWorkspace(),
            [new ClaudeAgentAdapter()],
            clock: () => clock.UtcNow);
        var options = new WorkerOptions(
            "claude", true, null, WorkspaceMode.Current,
            new Dictionary<string, string>(), null, TimeSpan.FromMinutes(10),
            FencedAction.Kill, null, "agent", false, false);

        var summary = await worker.RunAsync(
            config, options, directory,
            value =>
            {
                events.Add(value);
                return Task.CompletedTask;
            },
            CancellationToken.None);

        Assert.Equal(0, summary.NeedsAttention);
        Assert.Single(events, value => value.Type == "finished");
        Assert.DoesNotContain(events, value => value.Type == "needs-attention");

        var item = await backend.GetAsync(config, created.Id, CancellationToken.None);
        Assert.Equal(config.DefaultFinishTo, item!.Status);
        Assert.Null(item.DispatchState);
        // The load-bearing assertion: the residual claim was released, not retained for the lease.
        Assert.Equal(
            ClaimOwnershipState.Unclaimed,
            (await backend.GetClaimOwnershipAsync(config, created.Id, CancellationToken.None)).State);
    }

    [Theory]
    [InlineData(AgentFailureKind.PermissionDenied)]
    [InlineData(AgentFailureKind.Authentication)]
    [InlineData(AgentFailureKind.BillingUnavailable)]
    public async Task Unrecoverable_failure_stops_at_needs_attention_instead_of_returning_to_the_pool(
        AgentFailureKind kind)
    {
        var backend = new LocalMarkdownTrackerBackend(new FakeIdentity(), clock);
        var config = new TrackerConfig
        {
            Backend = "local-markdown",
            DefaultPickFrom = "Todo",
            Worker = new WorkerConfig { RequirementsAssessment = new WorkerRequirementsAssessmentConfig { Mode = "inline" }, UseWorkerQueue = false },
            SourcePath = Path.Combine(directory, ".wrighty.json"),
            LocalMarkdown = new LocalMarkdownBackendConfig(),
            LeaseMinutes = 60
        };
        await backend.InitializeAsync(config, false, CancellationToken.None);
        var created = await backend.CreateAsync(config, new CreateWorkItemOperation(
            new CreateWorkItemRequest("Denied item", "Body", "Todo", "P1",
                AutomaticExecutionAllowed: true, AgentPolicy: "claude"), false),
            CancellationToken.None);
        var events = new List<WorkerEvent>();
        var worker = new WorkerService(
            new TrackerService(new TrackerBackendRegistry([backend])),
            new FailingRunner(new AgentFailure(
                kind, "permission_denied", null, null, false,
                AgentFailureConfidence.Authoritative, "Sandbox denied the write.")),
            new CurrentWorkspace(),
            [new ClaudeAgentAdapter()],
            clock: () => clock.UtcNow);
        var options = new WorkerOptions(
            "claude", true, null, WorkspaceMode.Current,
            new Dictionary<string, string>(), null, TimeSpan.FromMinutes(10),
            FencedAction.Kill, null, "agent", false, false);

        var summary = await worker.RunAsync(
            config, options, directory,
            value =>
            {
                events.Add(value);
                return Task.CompletedTask;
            },
            CancellationToken.None);

        Assert.Equal(1, summary.NeedsAttention);
        Assert.Equal(0, summary.Failed);
        var attention = Assert.Single(events, value => value.Type == "needs-attention");
        Assert.Equal(kind, attention.Failure!.Kind);
        // The operator must be able to read why without opening the vendor session.
        Assert.Equal("Sandbox denied the write.", attention.Message);
        Assert.NotEmpty(attention.OperatorActions!);
        Assert.DoesNotContain(events, value => value.Type == "failed");

        // A bare release would put the item straight back in the claimable pool, so the next poll
        // would spawn the same agent and fail identically.
        var item = await backend.GetAsync(config, created.Id, CancellationToken.None);
        Assert.Equal(DispatchStates.NeedsAttention, item!.DispatchState);
    }

    [Fact]
    public async Task An_unrecoverable_failures_event_reason_quotes_the_agent_without_its_report_block()
    {
        // The failure path quotes the agent's closing words as the event reason when the failure
        // carries no sanitized message of its own. Those words can end with a report block, and an
        // event message is truncated for terminals — a fenced block cut mid-JSON never closes. The
        // quote must be the prose alone, like every other surface that quotes an agent.
        var backend = new LocalMarkdownTrackerBackend(new FakeIdentity(), clock);
        var config = new TrackerConfig
        {
            Backend = "local-markdown",
            DefaultPickFrom = "Todo",
            Worker = new WorkerConfig { RequirementsAssessment = new WorkerRequirementsAssessmentConfig { Mode = "inline" }, UseWorkerQueue = false },
            SourcePath = Path.Combine(directory, ".wrighty.json"),
            LocalMarkdown = new LocalMarkdownBackendConfig(),
            LeaseMinutes = 60
        };
        await backend.InitializeAsync(config, false, CancellationToken.None);
        var created = await backend.CreateAsync(config, new CreateWorkItemOperation(
            new CreateWorkItemRequest("Denied item", "Body", "Todo", "P1",
                AutomaticExecutionAllowed: true, AgentPolicy: "claude"), false),
            CancellationToken.None);
        var events = new List<WorkerEvent>();
        var worker = new WorkerService(
            new TrackerService(new TrackerBackendRegistry([backend])),
            new FailingRunner(
                new AgentFailure(
                    AgentFailureKind.Authentication, "auth_failed", null, null, false,
                    AgentFailureConfidence.Authoritative, SanitizedMessage: null),
                "I could not authenticate and stopped.\n\n" +
                "```wrighty-report\n" +
                """{"summary":"Stopped before doing any work."}""" +
                "\n```"),
            new CurrentWorkspace(),
            [new ClaudeAgentAdapter()],
            clock: () => clock.UtcNow);
        var options = new WorkerOptions(
            "claude", true, null, WorkspaceMode.Current,
            new Dictionary<string, string>(), null, TimeSpan.FromMinutes(10),
            FencedAction.Kill, null, "agent", false, false);

        await worker.RunAsync(
            config, options, directory,
            value =>
            {
                events.Add(value);
                return Task.CompletedTask;
            },
            CancellationToken.None);

        var attention = Assert.Single(events, value => value.Type == "needs-attention");
        Assert.Equal("I could not authenticate and stopped.", attention.Message);
        Assert.DoesNotContain("wrighty-report", attention.Message!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Needs_attention_presents_the_stop_reason_for_the_dispatch_detail_projection()
    {
        // A board column showing why an item stopped is only as good as the presentation call
        // behind it: marking needs-attention must present a dispatch record carrying the reason
        // and the session agent, so a backend with a presentation surface (the GitHub dispatch
        // fields) can show them at a glance.
        var inner = new LocalMarkdownTrackerBackend(new FakeIdentity(), clock);
        var backend = new DispatchPresentationRecordingBackend(inner);
        var config = new TrackerConfig
        {
            Backend = "local-markdown",
            DefaultPickFrom = "Todo",
            Worker = new WorkerConfig { RequirementsAssessment = new WorkerRequirementsAssessmentConfig { Mode = "inline" }, UseWorkerQueue = false },
            SourcePath = Path.Combine(directory, ".wrighty.json"),
            LocalMarkdown = new LocalMarkdownBackendConfig(),
            LeaseMinutes = 60
        };
        await backend.InitializeAsync(config, false, CancellationToken.None);
        await backend.CreateAsync(config, new CreateWorkItemOperation(
            new CreateWorkItemRequest("Denied item", "Body", "Todo", "P1",
                AutomaticExecutionAllowed: true, AgentPolicy: "claude"), false),
            CancellationToken.None);
        var worker = new WorkerService(
            new TrackerService(new TrackerBackendRegistry([backend])),
            new FailingRunner(new AgentFailure(
                AgentFailureKind.PermissionDenied, "permission_denied", null, null, false,
                AgentFailureConfidence.Authoritative, "Sandbox denied the write.")),
            new CurrentWorkspace(),
            [new ClaudeAgentAdapter()],
            clock: () => clock.UtcNow);
        var options = new WorkerOptions(
            "claude", true, null, WorkspaceMode.Current,
            new Dictionary<string, string>(), null, TimeSpan.FromMinutes(10),
            FencedAction.Kill, null, "agent", false, false);

        await worker.RunAsync(
            config, options, directory, _ => Task.CompletedTask, CancellationToken.None);

        var presented = Assert.Single(backend.PresentedDispatches);
        Assert.Equal(DispatchStates.NeedsAttention, presented.State);
        Assert.Equal("Sandbox denied the write.", presented.Reason);
        Assert.Equal("claude", presented.SessionAgent);
    }

    [Fact]
    public async Task Policy_change_after_claim_releases_claim_and_skips_before_workspace_or_vendor()
    {
        var inner = new LocalMarkdownTrackerBackend(new FakeIdentity(), clock);
        var backend = new PolicyChangingAfterClaimBackend(inner);
        var config = new TrackerConfig
        {
            Backend = "local-markdown",
            DefaultPickFrom = "Todo",
            Worker = new WorkerConfig { RequirementsAssessment = new WorkerRequirementsAssessmentConfig { Mode = "inline" }, UseWorkerQueue = false },
            SourcePath = Path.Combine(directory, ".wrighty.json"),
            LocalMarkdown = new LocalMarkdownBackendConfig(),
            LeaseMinutes = 60
        };
        await backend.InitializeAsync(config, false, CancellationToken.None);
        var created = await backend.CreateAsync(config, new CreateWorkItemOperation(
            new CreateWorkItemRequest("Policy changes", "Body", "Todo", "P1",
                AutomaticExecutionAllowed: true, AgentPolicy: "claude"), false),
            CancellationToken.None);
        var events = new List<WorkerEvent>();
        var worker = new WorkerService(
            new TrackerService(new TrackerBackendRegistry([backend])),
            new FailIfRunRunner(),
            new FailIfPrepareWorkspace(),
            [new ClaudeAgentAdapter()],
            clock: () => clock.UtcNow);
        var options = new WorkerOptions(
            "claude",
            true,
            null,
            WorkspaceMode.Current,
            new Dictionary<string, string>(),
            null,
            TimeSpan.FromMinutes(10),
            FencedAction.Kill,
            null,
            "agent",
            false,
            false);

        var result = await worker.RunItemAsync(
            config,
            options,
            directory,
            created.Id,
            WorkerItemIntent.Fresh,
            null,
            value =>
            {
                events.Add(value);
                return Task.CompletedTask;
            },
            CancellationToken.None);

        Assert.Equal(1, result.Processed);
        Assert.Equal(0, result.Failed);
        var item = await inner.GetAsync(config, created.Id, CancellationToken.None);
        Assert.NotNull(item);
        Assert.False(item.AutomaticExecutionAllowed);
        Assert.Equal("Todo", item.Status);
        Assert.Contains(events, value => value.Type == "skipped-policy");
        Assert.Equal(
            ClaimOwnershipState.Unclaimed,
            (await inner.GetClaimOwnershipAsync(
                config, created.Id, CancellationToken.None)).State);
    }

    [Fact]
    public async Task Busy_current_workspace_is_rejected_before_claim_or_vendor_spawn()
    {
        var backend = new LocalMarkdownTrackerBackend(new FakeIdentity(), clock);
        var config = new TrackerConfig
        {
            Backend = "local-markdown",
            DefaultPickFrom = "Todo",
            Worker = new WorkerConfig { RequirementsAssessment = new WorkerRequirementsAssessmentConfig { Mode = "inline" }, UseWorkerQueue = false },
            SourcePath = Path.Combine(directory, ".wrighty.json"),
            LocalMarkdown = new LocalMarkdownBackendConfig(),
            LeaseMinutes = 60
        };
        await backend.InitializeAsync(config, false, CancellationToken.None);
        var created = await backend.CreateAsync(config, new CreateWorkItemOperation(
            new CreateWorkItemRequest("Do not claim me", "Body", "Todo", "P1",
                AutomaticExecutionAllowed: true, AgentPolicy: "claude"), false), CancellationToken.None);
        var workspaceLock = new RejectingWorkspaceLock();
        var worker = new WorkerService(
            new TrackerService(new TrackerBackendRegistry([backend])),
            new FailIfRunRunner(),
            new CurrentWorkspace(),
            [new ClaudeAgentAdapter()],
            clock: () => clock.UtcNow,
            workspaceExecutionLock: workspaceLock);

        var exception = await Assert.ThrowsAsync<TrackerException>(() => worker.RunAsync(
            config,
            new WorkerOptions("claude", true, null, WorkspaceMode.Current,
                new Dictionary<string, string>(), null, TimeSpan.FromMinutes(10),
                FencedAction.Kill, null, "agent", false, false),
            directory,
            _ => Task.CompletedTask,
            CancellationToken.None));

        Assert.Equal("WORKSPACE_BUSY", exception.Code);
        Assert.Equal([Path.GetFullPath(directory)], workspaceLock.Attempts);
        Assert.Equal(ClaimOwnershipState.Unclaimed,
            (await backend.GetClaimOwnershipAsync(config, created.Id, CancellationToken.None)).State);
        Assert.Equal("Todo", (await backend.GetAsync(
            config, created.Id, CancellationToken.None))!.Status);
    }

    [Fact]
    public async Task Worktree_mode_does_not_take_the_shared_current_workspace_lock()
    {
        var backend = new LocalMarkdownTrackerBackend(new FakeIdentity(), clock);
        var config = new TrackerConfig
        {
            Backend = "local-markdown",
            DefaultPickFrom = "Todo",
            Worker = new WorkerConfig { RequirementsAssessment = new WorkerRequirementsAssessmentConfig { Mode = "inline" }, UseWorkerQueue = false },
            SourcePath = Path.Combine(directory, ".wrighty.json"),
            LocalMarkdown = new LocalMarkdownBackendConfig(),
            LeaseMinutes = 60
        };
        await backend.InitializeAsync(config, false, CancellationToken.None);
        await backend.CreateAsync(config, new CreateWorkItemOperation(
            new CreateWorkItemRequest("Isolated work", "Body", "Todo", "P1",
                AutomaticExecutionAllowed: true, AgentPolicy: "claude"), false), CancellationToken.None);
        var workspaceLock = new RejectingWorkspaceLock();
        var worker = new WorkerService(
            new TrackerService(new TrackerBackendRegistry([backend])),
            new CapturingRejectedRunner(),
            new TrackingWorktree(directory),
            [new ClaudeAgentAdapter()],
            clock: () => clock.UtcNow,
            workspaceExecutionLock: workspaceLock);

        var summary = await worker.RunAsync(
            config,
            new WorkerOptions("claude", true, null, WorkspaceMode.Worktree,
                new Dictionary<string, string>(), null, TimeSpan.FromMinutes(10),
                FencedAction.Kill, null, "agent", false, false),
            directory,
            _ => Task.CompletedTask,
            CancellationToken.None);

        Assert.Equal(1, summary.Failed);
        Assert.Empty(workspaceLock.Attempts);
    }

    [Fact]
    public async Task Worktree_mode_rejects_an_unavailable_agent_skill_before_claim_or_workspace_creation()
    {
        var backend = new LocalMarkdownTrackerBackend(new FakeIdentity(), clock);
        var config = new TrackerConfig
        {
            Backend = "local-markdown",
            DefaultPickFrom = "Todo",
            Worker = new WorkerConfig { RequirementsAssessment = new WorkerRequirementsAssessmentConfig { Mode = "inline" }, UseWorkerQueue = false },
            SourcePath = Path.Combine(directory, ".wrighty.json"),
            LocalMarkdown = new LocalMarkdownBackendConfig(),
            LeaseMinutes = 60
        };
        await backend.InitializeAsync(config, false, CancellationToken.None);
        var created = await backend.CreateAsync(config, new CreateWorkItemOperation(
            new CreateWorkItemRequest("Needs a skill", "Body", "Todo", "P1",
                AutomaticExecutionAllowed: true, AgentPolicy: "claude"), false), CancellationToken.None);
        var skillAvailability = new RejectingSkillAvailability();
        var worker = new WorkerService(
            new TrackerService(new TrackerBackendRegistry([backend])),
            new FailIfRunRunner(),
            new FailIfPrepareWorkspace(),
            [new ClaudeAgentAdapter()],
            clock: () => clock.UtcNow,
            skillAvailability: skillAvailability);

        var exception = await Assert.ThrowsAsync<TrackerException>(() => worker.RunAsync(
            config,
            new WorkerOptions("claude", true, null, WorkspaceMode.Worktree,
                new Dictionary<string, string>(), null, TimeSpan.FromMinutes(10),
                FencedAction.Kill, null, "agent", false, false),
            directory,
            _ => Task.CompletedTask,
            CancellationToken.None));

        Assert.Equal("WORKER_SKILL_UNAVAILABLE", exception.Code);
        Assert.Equal([("claude", Path.GetFullPath(directory))], skillAvailability.Attempts);
        Assert.Equal(ClaimOwnershipState.Unclaimed,
            (await backend.GetClaimOwnershipAsync(config, created.Id, CancellationToken.None)).State);
        Assert.Equal("Todo", (await backend.GetAsync(
            config, created.Id, CancellationToken.None))!.Status);
    }

    [Fact]
    public async Task Shared_mode_uses_current_workspace_without_taking_the_exclusive_lock()
    {
        var backend = new LocalMarkdownTrackerBackend(new FakeIdentity(), clock);
        var config = new TrackerConfig
        {
            Backend = "local-markdown",
            DefaultPickFrom = "Todo",
            Worker = new WorkerConfig { RequirementsAssessment = new WorkerRequirementsAssessmentConfig { Mode = "inline" }, UseWorkerQueue = false },
            SourcePath = Path.Combine(directory, ".wrighty.json"),
            LocalMarkdown = new LocalMarkdownBackendConfig(),
            LeaseMinutes = 60
        };
        await backend.InitializeAsync(config, false, CancellationToken.None);
        await backend.CreateAsync(config, new CreateWorkItemOperation(
            new CreateWorkItemRequest("Shared work", "Body", "Todo", "P1",
                AutomaticExecutionAllowed: true, AgentPolicy: "claude"), false), CancellationToken.None);
        var workspaceLock = new RejectingWorkspaceLock();
        var workspaceManager = new RecordingWorkspaceMode();
        var worker = new WorkerService(
            new TrackerService(new TrackerBackendRegistry([backend])),
            new CapturingRejectedRunner(),
            workspaceManager,
            [new ClaudeAgentAdapter()],
            clock: () => clock.UtcNow,
            workspaceExecutionLock: workspaceLock);

        var summary = await worker.RunAsync(
            config,
            new WorkerOptions("claude", true, null, WorkspaceMode.Shared,
                new Dictionary<string, string>(), null, TimeSpan.FromMinutes(10),
                FencedAction.Kill, null, "agent", false, false),
            directory,
            _ => Task.CompletedTask,
            CancellationToken.None);

        Assert.Equal(1, summary.Failed);
        Assert.Empty(workspaceLock.Attempts);
        Assert.Equal(WorkspaceMode.Shared, workspaceManager.Mode);
        Assert.Equal(Path.GetFullPath(directory), workspaceManager.RepositoryPath);
    }

    [Fact]
    public async Task Successful_process_with_residual_claim_reports_attention_and_keeps_resume_address()
    {
        var backend = new LocalMarkdownTrackerBackend(new FakeIdentity(), clock);
        var config = new TrackerConfig
        {
            Backend = "local-markdown",
            DefaultPickFrom = "Todo",
            Worker = new WorkerConfig { RequirementsAssessment = new WorkerRequirementsAssessmentConfig { Mode = "inline" }, UseWorkerQueue = false },
            SourcePath = Path.Combine(directory, ".wrighty.json"),
            LocalMarkdown = new LocalMarkdownBackendConfig(),
            LeaseMinutes = 60
        };
        await backend.InitializeAsync(config, false, CancellationToken.None);
        await backend.CreateAsync(config, new CreateWorkItemOperation(
            new CreateWorkItemRequest("Needs clarification", "...", "Todo", "P1",
                AutomaticExecutionAllowed: true, AgentPolicy: "claude"), false), CancellationToken.None);
        var events = new List<WorkerEvent>();
        var tracker = new TrackerService(new TrackerBackendRegistry([backend]));
        var worker = new WorkerService(tracker, new SuccessfulRunner(), new CurrentWorkspace(),
            [new ClaudeAgentAdapter()],
            (_, token) => Task.Delay(Timeout.InfiniteTimeSpan, token),
            () => clock.UtcNow);

        var summary = await worker.RunAsync(config,
            new WorkerOptions("claude", true, null, WorkspaceMode.Current,
                new Dictionary<string, string>(), null, TimeSpan.FromMinutes(10),
                FencedAction.Kill, null, "agent", false, false),
            directory, value =>
            {
                events.Add(value);
                return Task.CompletedTask;
            }, CancellationToken.None);

        Assert.Equal(new WorkerRunSummary(1, 1, 0), summary);
        Assert.Equal(10, summary.ExitCode);
        var attention = Assert.Single(events, value => value.Type == "needs-attention");
        Assert.NotNull(attention.SessionId);
        Assert.NotNull(attention.ClaimExpiresAt);
        Assert.Collection(
            Assert.IsAssignableFrom<IReadOnlyList<WorkerOperatorAction>>(attention.OperatorActions),
            action =>
            {
                Assert.Contains("web UI", action.Scenario);
                Assert.Equal(["wrighty web"], action.Commands);
                Assert.Contains("Save and resume automatically", action.Description);
                Assert.Contains("Save and show manual Claude resume command", action.Description);
                Assert.Contains("Finish when complete", action.Description);
                Assert.Contains("Archive", action.Description);
            },
            action =>
            {
                // Two commands, not one with --requeue: see
                // No_suggested_action_rewrites_the_description_and_queues_it_unattended.
                Assert.Contains("Clarify the requirements", action.Scenario);
                Assert.Equal(
                    [
                        "wrighty edit local:1 --takeover --yes --body-file requirements.md",
                        "wrighty worker --item local:1 --yes"
                    ],
                    action.Commands);
                Assert.Contains("because you named the item", action.Description);
            },
            // No separate "continue with the agent" action: on this backend its command is already
            // the second line above, and the duplication read as two different things to do.
            action =>
            {
                Assert.Contains("Take the item over", action.Scenario);
                Assert.Equal(
                    [
                        "wrighty edit local:1 --takeover",
                        "wrighty edit local:1 --takeover --yes --title \"Clear title\" " +
                        "--body-file requirements.md"
                    ],
                    action.Commands);
                Assert.Contains($"{attention.ClaimExpiresAt:O}", action.Description);
                Assert.Contains("edit --takeover works before or after that time", action.Description);
                Assert.Contains("after expiry, it acquires", action.Description);
                Assert.Contains("session is preserved in either case", action.Description);
                Assert.Contains("retain the claim handle inside Wrighty", action.Description);
                // Local Markdown has nothing to append to, so editing is the only way to clarify
                // and must not be discouraged here.
                Assert.DoesNotContain("prefer a comment", action.Description);
            });
        var ownership = await backend.GetClaimOwnershipAsync(config, new WorkItemId("local:1"),
            CancellationToken.None);
        Assert.Equal(ClaimOwnershipState.OwnedByCurrent, ownership.State);
        Assert.Equal(directory, ownership.WorkspacePath);
        Assert.Equal(attention.SessionId, ownership.SessionId);
        Assert.Equal("In Progress", (await backend.GetAsync(config, new WorkItemId("local:1"),
            CancellationToken.None))!.Status);
        Assert.Equal(
            DispatchStates.NeedsAttention,
            (await backend.GetAsync(config, new WorkItemId("local:1"),
                CancellationToken.None))!.DispatchState);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Paused_item_can_be_queued_without_a_claim_handle(bool expireClaim)
    {
        var backend = new LocalMarkdownTrackerBackend(new FakeIdentity(), clock);
        var config = new TrackerConfig
        {
            Backend = "local-markdown",
            DefaultPickFrom = "Todo",
            Worker = new WorkerConfig { RequirementsAssessment = new WorkerRequirementsAssessmentConfig { Mode = "inline" }, UseWorkerQueue = false },
            SourcePath = Path.Combine(directory, ".wrighty.json"),
            LocalMarkdown = new LocalMarkdownBackendConfig(),
            LeaseMinutes = 60
        };
        await backend.InitializeAsync(config, false, CancellationToken.None);
        var created = await backend.CreateAsync(config, new CreateWorkItemOperation(
            new CreateWorkItemRequest(
                "Queue paused session",
                "Body",
                "In Progress",
                "P1",
                AutomaticExecutionAllowed: true,
                AgentPolicy: "codex"),
            false), CancellationToken.None);
        var context = new AgentExecutionContext(
            "codex",
            "paused-session",
            AgentContextSource.ExplicitOption,
            ClaimantKind: ClaimantKind.Agent,
            ClaimantId: "agent:worker:paused");
        var claim = await backend.TryClaimAsync(
            config, created.Id, context, CancellationToken.None);
        var handle = new ClaimHandle(context, claim.ClaimToken);
        await backend.RenewClaimAsync(
            config, created.Id, handle, directory, "paused-session", CancellationToken.None);
        await backend.UpdateAsync(
            config,
            created.Id,
            new UpdateWorkItemOperation(
                new WorkItemPatch(
                    OptionalValue<string>.Unspecified,
                    OptionalValue<string>.Unspecified,
                    OptionalValue<string>.Unspecified,
                    OptionalValue<string?>.Unspecified,
                    DispatchState: OptionalValue<string?>.From(
                        DispatchStates.NeedsAttention)),
                false,
                ClaimHandle: handle),
            CancellationToken.None);
        if (expireClaim)
            clock.UtcNow = clock.UtcNow.AddMinutes(61);

        await backend.QueuePausedAsync(config, created.Id, CancellationToken.None);

        Assert.Equal(
            ClaimOwnershipState.Unclaimed,
            (await backend.GetClaimOwnershipAsync(
                config, created.Id, CancellationToken.None)).State);
        Assert.Equal(
            DispatchStates.Queued,
            (await backend.GetAsync(config, created.Id, CancellationToken.None))!.DispatchState);
        var session = await backend.GetAgentSessionAsync(
            config, created.Id, CancellationToken.None);
        Assert.Equal("codex", session!.Agent);
        Assert.Equal("paused-session", session.SessionId);
        Assert.Equal(directory, session.WorkspacePath);
    }

    [Fact]
    public async Task Queueing_a_paused_session_keeps_what_the_session_already_learned()
    {
        // Queueing changes claim ownership and the pending dispatch. Everything else the record
        // holds describes the session, not the queueing, and has to survive it.
        //
        // It did not: the record was rebuilt from a subset of its members, so the approved-context
        // manifest and the last report were silently dropped. The next resume then refused for want
        // of a manifest — with the item's own report gone from the panel that would have explained
        // why — and a refused resume leaves the item outside needs-attention, where the queue action
        // that got it here is no longer offered.
        var backend = new LocalMarkdownTrackerBackend(new FakeIdentity(), clock);
        var config = new TrackerConfig
        {
            Backend = "local-markdown",
            DefaultPickFrom = "Todo",
            Worker = new WorkerConfig { RequirementsAssessment = new WorkerRequirementsAssessmentConfig { Mode = "inline" }, UseWorkerQueue = false },
            SourcePath = Path.Combine(directory, ".wrighty.json"),
            LocalMarkdown = new LocalMarkdownBackendConfig(),
            LeaseMinutes = 60
        };
        await backend.InitializeAsync(config, false, CancellationToken.None);
        var created = await backend.CreateAsync(config, new CreateWorkItemOperation(
            new CreateWorkItemRequest("Queue paused session", "Body", "In Progress", "P1",
                AutomaticExecutionAllowed: true, AgentPolicy: "codex"), false),
            CancellationToken.None);

        var context = new AgentExecutionContext(
            "codex", "paused-session", AgentContextSource.ExplicitOption,
            ClaimantKind: ClaimantKind.Agent, ClaimantId: "agent:worker:paused");
        var claim = await backend.TryClaimAsync(config, created.Id, context, CancellationToken.None);
        var handle = new ClaimHandle(context, claim.ClaimToken);
        await backend.RenewClaimAsync(
            config, created.Id, handle, directory, "paused-session", CancellationToken.None);
        await backend.UpdateAsync(
            config,
            created.Id,
            new UpdateWorkItemOperation(
                new WorkItemPatch(
                    OptionalValue<string>.Unspecified,
                    OptionalValue<string>.Unspecified,
                    OptionalValue<string>.Unspecified,
                    OptionalValue<string?>.Unspecified,
                    DispatchState: OptionalValue<string?>.From(DispatchStates.NeedsAttention)),
                false,
                ClaimHandle: handle),
            CancellationToken.None);

        var captured = new DateTimeOffset(2026, 7, 29, 12, 0, 0, TimeSpan.Zero);
        var recorded = new SessionContextMetadata(
            new ContextManifest(
                2, "sha256:abc", "sha256:title", "sha256:body",
                [new ContextManifestEntry("c1", "sha256:c1", captured)],
                captured),
            BaseApprovedAt: captured,
            ApprovalSource: ContextApprovalSource.BackendLocal,
            CapturedAt: captured);
        await backend.RecordSessionContextAsync(
            config, created.Id, recorded, CancellationToken.None);
        await backend.RecordRunReportAsync(
            config,
            created.Id,
            new AgentRunReport("run-1", "report-1", "codex",
                RunReportDisposition.NeedsAttention, AgentOutcome.Succeeded, captured,
                Summary: "Blocked on an unclear requirement."),
            CancellationToken.None);

        await backend.QueuePausedAsync(config, created.Id, CancellationToken.None);

        var session = await backend.GetAgentSessionAsync(
            config, created.Id, CancellationToken.None);

        // Without the manifest the next resume cannot establish what the agent already holds and
        // refuses outright, which is what made the item unrunnable.
        Assert.Equal("sha256:abc", session!.Context?.SuppliedDigest);
        Assert.Equal(2, session.Context?.Manifest?.FormatVersion);
        Assert.Equal("Blocked on an unclear requirement.", session.LastReport?.Summary);

        // ...and the queueing itself still did what it is for.
        Assert.Equal("paused-session", session.SessionId);
        Assert.Equal(
            DispatchStates.Queued,
            (await backend.GetAsync(config, created.Id, CancellationToken.None))!.DispatchState);
        Assert.Equal(
            ClaimOwnershipState.Unclaimed,
            (await backend.GetClaimOwnershipAsync(
                config, created.Id, CancellationToken.None)).State);
    }

    [Fact]
    public async Task Queueing_after_release_takes_its_address_from_the_session_record()
    {
        // The other source of the queued address. With a live claim the address comes from the
        // claim record; after a release that preserved the dispatch state there is no claim left,
        // and everything — agent, session, workspace, and what the session was given — must come
        // from the runtime record instead.
        var backend = new LocalMarkdownTrackerBackend(new FakeIdentity(), clock);
        var config = new TrackerConfig
        {
            Backend = "local-markdown",
            DefaultPickFrom = "Todo",
            Worker = new WorkerConfig { RequirementsAssessment = new WorkerRequirementsAssessmentConfig { Mode = "inline" }, UseWorkerQueue = false },
            SourcePath = Path.Combine(directory, ".wrighty.json"),
            LocalMarkdown = new LocalMarkdownBackendConfig(),
            LeaseMinutes = 60
        };
        await backend.InitializeAsync(config, false, CancellationToken.None);
        var created = await backend.CreateAsync(config, new CreateWorkItemOperation(
            new CreateWorkItemRequest("Queue paused session", "Body", "In Progress", "P1",
                AutomaticExecutionAllowed: true, AgentPolicy: "codex"), false),
            CancellationToken.None);

        var context = new AgentExecutionContext(
            "codex", "released-session", AgentContextSource.ExplicitOption,
            ClaimantKind: ClaimantKind.Agent, ClaimantId: "agent:worker:released");
        var claim = await backend.TryClaimAsync(config, created.Id, context, CancellationToken.None);
        var handle = new ClaimHandle(context, claim.ClaimToken);
        await backend.RenewClaimAsync(
            config, created.Id, handle, directory, "released-session", CancellationToken.None);
        await backend.UpdateAsync(
            config,
            created.Id,
            new UpdateWorkItemOperation(
                new WorkItemPatch(
                    OptionalValue<string>.Unspecified,
                    OptionalValue<string>.Unspecified,
                    OptionalValue<string>.Unspecified,
                    OptionalValue<string?>.Unspecified,
                    DispatchState: OptionalValue<string?>.From(DispatchStates.NeedsAttention)),
                false,
                ClaimHandle: handle),
            CancellationToken.None);

        var captured = new DateTimeOffset(2026, 7, 29, 12, 0, 0, TimeSpan.Zero);
        await backend.RecordSessionContextAsync(
            config,
            created.Id,
            new SessionContextMetadata(
                new ContextManifest(2, "sha256:def", "sha256:t", "sha256:b", [], captured),
                ApprovalSource: ContextApprovalSource.BackendLocal,
                CapturedAt: captured),
            CancellationToken.None);
        await backend.ReleaseAsync(config, created.Id, handle, false, DispatchStateOnRelease.Preserve,
            CancellationToken.None);

        await backend.QueuePausedAsync(config, created.Id, CancellationToken.None);

        var session = await backend.GetAgentSessionAsync(
            config, created.Id, CancellationToken.None);
        Assert.Equal("codex", session!.Agent);
        Assert.Equal("released-session", session.SessionId);
        Assert.Equal(directory, session.WorkspacePath);
        Assert.Equal("sha256:def", session.Context?.SuppliedDigest);
        Assert.Equal(
            DispatchStates.Queued,
            (await backend.GetAsync(config, created.Id, CancellationToken.None))!.DispatchState);
    }

    [Fact]
    public async Task Queue_paused_rejects_item_after_worker_state_changes()
    {
        var backend = new LocalMarkdownTrackerBackend(new FakeIdentity(), clock);
        var config = new TrackerConfig
        {
            Backend = "local-markdown",
            DefaultPickFrom = "Todo",
            Worker = new WorkerConfig { RequirementsAssessment = new WorkerRequirementsAssessmentConfig { Mode = "inline" }, UseWorkerQueue = false },
            SourcePath = Path.Combine(directory, ".wrighty.json"),
            LocalMarkdown = new LocalMarkdownBackendConfig(),
            LeaseMinutes = 60
        };
        await backend.InitializeAsync(config, false, CancellationToken.None);
        var created = await backend.CreateAsync(config, new CreateWorkItemOperation(
            new CreateWorkItemRequest(
                "Already resumed",
                "Body",
                "In Progress",
                "P1",
                AutomaticExecutionAllowed: true,
                AgentPolicy: "codex"),
            false), CancellationToken.None);
        var context = new AgentExecutionContext(
            "codex",
            "running-session",
            AgentContextSource.ExplicitOption,
            ClaimantKind: ClaimantKind.Agent,
            ClaimantId: "agent:worker:running");
        var claim = await backend.TryClaimAsync(
            config, created.Id, context, CancellationToken.None);
        await backend.RenewClaimAsync(
            config,
            created.Id,
            new ClaimHandle(context, claim.ClaimToken),
            directory,
            "running-session",
            CancellationToken.None);

        var exception = await Assert.ThrowsAsync<TrackerException>(() =>
            backend.QueuePausedAsync(config, created.Id, CancellationToken.None));

        Assert.Equal("WORKER_ITEM_NOT_PAUSED", exception.Code);
        Assert.Equal(
            ClaimOwnershipState.OwnedByCurrent,
            (await backend.GetClaimOwnershipAsync(
                config, created.Id, CancellationToken.None)).State);
    }

    [Fact]
    public async Task Queue_paused_rejects_archived_item()
    {
        var (backend, config, id, handle) = await CreatePausedItemAsync();
        await backend.UpdateAsync(
            config,
            id,
            new UpdateWorkItemOperation(
                WorkItemPatch.StatusOnly("Done"),
                true,
                ClaimHandle: handle),
            CancellationToken.None);

        var exception = await Assert.ThrowsAsync<TrackerException>(() =>
            backend.QueuePausedAsync(config, id, CancellationToken.None));

        Assert.Equal("WORK_ITEM_ARCHIVED", exception.Code);
    }

    [Fact]
    public async Task Queue_paused_rejects_item_with_worker_eligibility_disabled()
    {
        var (backend, config, id, handle) = await CreatePausedItemAsync();
        await backend.UpdateAsync(
            config,
            id,
            new UpdateWorkItemOperation(
                new WorkItemPatch(
                    OptionalValue<string>.Unspecified,
                    OptionalValue<string>.Unspecified,
                    OptionalValue<string>.Unspecified,
                    OptionalValue<string?>.Unspecified,
                    AutomaticExecutionAllowed: OptionalValue<bool>.From(false)),
                false,
                ClaimHandle: handle),
            CancellationToken.None);

        var exception = await Assert.ThrowsAsync<TrackerException>(() =>
            backend.QueuePausedAsync(config, id, CancellationToken.None));

        Assert.Equal("WORKER_ITEM_INELIGIBLE", exception.Code);
    }

    [Fact]
    public async Task Queue_paused_rejects_item_outside_worker_in_progress_status()
    {
        var (backend, config, id, handle) = await CreatePausedItemAsync();
        await backend.UpdateAsync(
            config,
            id,
            new UpdateWorkItemOperation(
                WorkItemPatch.StatusOnly("Todo"),
                false,
                ClaimHandle: handle),
            CancellationToken.None);

        var exception = await Assert.ThrowsAsync<TrackerException>(() =>
            backend.QueuePausedAsync(config, id, CancellationToken.None));

        Assert.Equal("WORKER_ITEM_INELIGIBLE", exception.Code);
    }

    [Theory]
    [InlineData(false, "CLAIM_NOT_OWNER")]
    [InlineData(true, "RESUME_ADDRESS_NOT_LOCAL")]
    public async Task Queue_paused_rejects_session_owned_by_another_installation(
        bool expireClaim,
        string expectedCode)
    {
        var (ownerBackend, config, id, _) = await CreatePausedItemAsync("worker-other");
        if (expireClaim)
            clock.UtcNow = clock.UtcNow.AddMinutes(61);
        var currentBackend = new LocalMarkdownTrackerBackend(
            new FakeIdentity(),
            clock);

        var exception = await Assert.ThrowsAsync<TrackerException>(() =>
            currentBackend.QueuePausedAsync(config, id, CancellationToken.None));

        Assert.Equal(expectedCode, exception.Code);
        Assert.Equal(
            DispatchStates.NeedsAttention,
            (await ownerBackend.GetAsync(config, id, CancellationToken.None))!.DispatchState);
    }

    [Fact]
    public async Task Queue_paused_rejects_item_without_a_complete_resume_address()
    {
        var backend = new LocalMarkdownTrackerBackend(new FakeIdentity(), clock);
        var config = WorkerConfig();
        await backend.InitializeAsync(config, false, CancellationToken.None);
        var created = await backend.CreateAsync(
            config,
            new CreateWorkItemOperation(
                new CreateWorkItemRequest(
                    "Missing resume address",
                    "Body",
                    "In Progress",
                    "P1",
                    AutomaticExecutionAllowed: true,
                    AgentPolicy: "codex"),
                false),
            CancellationToken.None);
        var context = new AgentExecutionContext(
            "codex",
            "incomplete-session",
            AgentContextSource.ExplicitOption,
            ClaimantKind: ClaimantKind.Agent,
            ClaimantId: "agent:worker:incomplete");
        var claim = await backend.TryClaimAsync(
            config,
            created.Id,
            context,
            CancellationToken.None);
        var handle = new ClaimHandle(context, claim.ClaimToken);
        await backend.UpdateAsync(
            config,
            created.Id,
            new UpdateWorkItemOperation(
                new WorkItemPatch(
                    OptionalValue<string>.Unspecified,
                    OptionalValue<string>.Unspecified,
                    OptionalValue<string>.Unspecified,
                    OptionalValue<string?>.Unspecified,
                    DispatchState: OptionalValue<string?>.From(
                        DispatchStates.NeedsAttention)),
                false,
                ClaimHandle: handle),
            CancellationToken.None);

        var exception = await Assert.ThrowsAsync<TrackerException>(() =>
            backend.QueuePausedAsync(config, created.Id, CancellationToken.None));

        Assert.Equal("RESUME_ADDRESS_UNAVAILABLE", exception.Code);
    }

    [Fact]
    public async Task Requeued_clarification_is_unclaimed_and_continuous_worker_resumes_recorded_session()
    {
        var backend = new LocalMarkdownTrackerBackend(new FakeIdentity(), clock);
        var config = new TrackerConfig
        {
            Backend = "local-markdown",
            DefaultPickFrom = "Todo",
            Worker = new WorkerConfig { RequirementsAssessment = new WorkerRequirementsAssessmentConfig { Mode = "inline" }, UseWorkerQueue = false },
            SourcePath = Path.Combine(directory, ".wrighty.json"),
            LocalMarkdown = new LocalMarkdownBackendConfig(),
            LeaseMinutes = 60
        };
        await backend.InitializeAsync(config, false, CancellationToken.None);
        var created = await backend.CreateAsync(config, new CreateWorkItemOperation(
            new CreateWorkItemRequest(
                "Clarify me",
                "...",
                "In Progress",
                "P1",
                AutomaticExecutionAllowed: true,
                AgentPolicy: "claude"),
            false), CancellationToken.None);
        var agentContext = new AgentExecutionContext(
            "claude",
            "session-to-resume",
            AgentContextSource.ExplicitOption,
            ClaimantKind: ClaimantKind.Agent,
            ClaimantId: "agent:original");
        var agentClaim = await backend.TryClaimAsync(
            config, created.Id, agentContext, CancellationToken.None);
        await backend.RenewClaimAsync(
            config,
            created.Id,
            new ClaimHandle(agentContext, agentClaim.ClaimToken),
            directory,
            "session-to-resume",
            CancellationToken.None);
        var humanContext = new AgentExecutionContext(
            null,
            null,
            AgentContextSource.ExplicitOption,
            ClaimantKind: ClaimantKind.Human,
            ClaimantId: "human-cli");
        var humanClaim = await backend.TakeoverAsync(
            config,
            created.Id,
            humanContext,
            agentClaim.ClaimToken,
            CancellationToken.None);
        var humanHandle = new ClaimHandle(
            humanContext with { ClaimToken = humanClaim.ClaimToken },
            humanClaim.ClaimToken);
        await backend.UpdateAsync(
            config,
            created.Id,
            new UpdateWorkItemOperation(
                new WorkItemPatch(
                    OptionalValue<string>.Unspecified,
                    OptionalValue<string>.From("Actionable requirements"),
                    OptionalValue<string>.Unspecified,
                    OptionalValue<string?>.Unspecified),
                false,
                ClaimHandle: humanHandle),
            CancellationToken.None);
        await backend.RequeueAsync(
            config, created.Id, humanHandle, CancellationToken.None);

        Assert.Equal(
            ClaimOwnershipState.Unclaimed,
            (await backend.GetClaimOwnershipAsync(
                config, created.Id, CancellationToken.None)).State);
        Assert.Equal(
            DispatchStates.Queued,
            (await backend.GetAsync(config, created.Id, CancellationToken.None))!.DispatchState);
        var retained = await backend.GetAgentSessionAsync(
            config, created.Id, CancellationToken.None);
        Assert.Equal("session-to-resume", retained!.SessionId);
        Assert.Equal(directory, retained.WorkspacePath);

        var runner = new CapturingResumeRunner();
        var events = new List<WorkerEvent>();
        var worker = new WorkerService(
            new TrackerService(new TrackerBackendRegistry([backend])),
            runner,
            new CurrentWorkspace(),
            [new ClaudeAgentAdapter()],
            (_, token) => Task.Delay(Timeout.InfiniteTimeSpan, token),
            () => clock.UtcNow);
        var summary = await worker.RunAsync(
            config,
            new WorkerOptions(
                null,
                true,
                null,
                WorkspaceMode.Current,
                new Dictionary<string, string>(),
                null,
                TimeSpan.FromMinutes(10),
                FencedAction.Kill,
                null,
                "agent",
                false,
                false),
            directory,
            value =>
            {
                events.Add(value);
                return Task.CompletedTask;
            },
            CancellationToken.None);

        Assert.Equal(new WorkerRunSummary(1, 1, 0), summary);
        Assert.Contains("--resume", runner.Invocation!.Arguments);
        Assert.Contains("session-to-resume", runner.Invocation.Arguments);
        Assert.Contains(events, value =>
            value.Type == "resumed" && value.SessionId == "session-to-resume");
        Assert.Equal(
            DispatchStates.NeedsAttention,
            (await backend.GetAsync(config, created.Id, CancellationToken.None))!.DispatchState);
    }

    [Fact]
    public async Task Fresh_run_reclaims_exact_expired_active_item_with_a_new_session()
    {
        var backend = new LocalMarkdownTrackerBackend(new FakeIdentity(), clock);
        var config = new TrackerConfig
        {
            Backend = "local-markdown",
            DefaultPickFrom = "Todo",
            Worker = new WorkerConfig { RequirementsAssessment = new WorkerRequirementsAssessmentConfig { Mode = "inline" }, UseWorkerQueue = false },
            SourcePath = Path.Combine(directory, ".wrighty.json"),
            LocalMarkdown = new LocalMarkdownBackendConfig(),
            LeaseMinutes = 60
        };
        await backend.InitializeAsync(config, false, CancellationToken.None);
        var created = await backend.CreateAsync(config, new CreateWorkItemOperation(
            new CreateWorkItemRequest("Retry me", "Body", "Todo", "P1",
                AutomaticExecutionAllowed: true, AgentPolicy: "claude"), false), CancellationToken.None);
        var oldContext = new AgentExecutionContext(
            "claude",
            "old-session",
            AgentContextSource.ExplicitOption,
            ClaimantKind: ClaimantKind.Agent,
            ClaimantId: "agent:worker:old");
        var oldClaim = await backend.TryClaimAsync(
            config, created.Id, oldContext, CancellationToken.None);
        await backend.UpdateAsync(config, created.Id, new UpdateWorkItemOperation(
            WorkItemPatch.StatusOnly("In Progress"),
            false,
            ClaimHandle: new ClaimHandle(oldContext, oldClaim.ClaimToken)),
            CancellationToken.None);
        clock.UtcNow = clock.UtcNow.AddMinutes(61);

        var runner = new CapturingRejectedRunner();
        var worker = new WorkerService(
            new TrackerService(new TrackerBackendRegistry([backend])),
            runner,
            new CurrentWorkspace(),
            [new ClaudeAgentAdapter()],
            clock: () => clock.UtcNow);
        var options = new WorkerOptions(
            "claude",
            true,
            null,
            WorkspaceMode.Current,
            new Dictionary<string, string>(),
            null,
            TimeSpan.FromMinutes(10),
            FencedAction.Kill,
            null,
            "agent",
            false,
            false);

        await worker.PreflightItemAsync(
            config, options, directory, created.Id, WorkerItemIntent.Fresh,
            _ => Task.CompletedTask,
            CancellationToken.None);
        var result = await worker.RunItemAsync(
            config, options, directory, created.Id, WorkerItemIntent.Fresh, null,
            _ => Task.CompletedTask,
            CancellationToken.None);

        Assert.Equal(1, result.Processed);
        Assert.Equal(1, result.Failed);
        Assert.Single(runner.SessionIds);
        Assert.NotEqual("old-session", runner.SessionIds[0]);
        Assert.Equal("In Progress", (await backend.GetAsync(
            config, created.Id, CancellationToken.None))!.Status);
    }

    [Fact]
    public async Task Exact_item_auto_recovers_expired_claim_and_resumes_existing_session()
    {
        var backend = new LocalMarkdownTrackerBackend(new FakeIdentity(), clock);
        var config = new TrackerConfig
        {
            Backend = "local-markdown",
            DefaultPickFrom = "Todo",
            Worker = new WorkerConfig { RequirementsAssessment = new WorkerRequirementsAssessmentConfig { Mode = "inline" }, UseWorkerQueue = false },
            SourcePath = Path.Combine(directory, ".wrighty.json"),
            LocalMarkdown = new LocalMarkdownBackendConfig(),
            LeaseMinutes = 60
        };
        await backend.InitializeAsync(config, false, CancellationToken.None);
        var created = await backend.CreateAsync(config, new CreateWorkItemOperation(
            new CreateWorkItemRequest("Continue me", "Clarified body", "In Progress", "P1",
                AutomaticExecutionAllowed: true, AgentPolicy: "claude"), false), CancellationToken.None);
        var oldContext = new AgentExecutionContext(
            "claude",
            "session-to-preserve",
            AgentContextSource.ExplicitOption,
            ClaimantKind: ClaimantKind.Agent,
            ClaimantId: "agent:worker:expired");
        var oldClaim = await backend.TryClaimAsync(
            config, created.Id, oldContext, CancellationToken.None);
        await backend.RenewClaimAsync(
            config,
            created.Id,
            new ClaimHandle(oldContext, oldClaim.ClaimToken),
            directory,
            "session-to-preserve",
            CancellationToken.None);
        clock.UtcNow = clock.UtcNow.AddMinutes(61);

        var runner = new CapturingResumeRunner();
        var events = new List<WorkerEvent>();
        var worker = new WorkerService(
            new TrackerService(new TrackerBackendRegistry([backend])),
            runner,
            new CurrentWorkspace(),
            [new ClaudeAgentAdapter()],
            (_, token) => Task.Delay(Timeout.InfiniteTimeSpan, token),
            () => clock.UtcNow);
        var options = new WorkerOptions(
            null,
            true,
            null,
            WorkspaceMode.Current,
            new Dictionary<string, string>(),
            null,
            TimeSpan.FromMinutes(10),
            FencedAction.Kill,
            null,
            "agent",
            false,
            false);

        await worker.PreflightItemAsync(
            config, options, directory, created.Id, WorkerItemIntent.Auto,
            value =>
            {
                events.Add(value);
                return Task.CompletedTask;
            },
            CancellationToken.None);
        var result = await worker.RunItemAsync(
            config, options, directory, created.Id, WorkerItemIntent.Auto, null,
            value =>
            {
                events.Add(value);
                return Task.CompletedTask;
            },
            CancellationToken.None);

        Assert.Equal(1, result.Processed);
        Assert.Contains("--resume", runner.Invocation!.Arguments);
        Assert.Contains("session-to-preserve", runner.Invocation.Arguments);
        Assert.DoesNotContain("--session-id", runner.Invocation.Arguments);
        Assert.StartsWith("agent:worker:", runner.Environment!["WRIGHTY_CLAIMANT_ID"]);
        Assert.NotEqual(oldClaim.ClaimToken, runner.Environment["WRIGHTY_CLAIM_TOKEN"]);
        Assert.Contains(events, value =>
            value.Type == "ready" &&
            value.Message!.Contains("prior claim expired", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(events, value =>
            value.Type == "resumed" &&
            value.SessionId == "session-to-preserve" &&
            value.Message!.Contains("new claim generation", StringComparison.OrdinalIgnoreCase));
        var ownership = await backend.GetClaimOwnershipAsync(
            config, created.Id, CancellationToken.None);
        Assert.Equal("session-to-preserve", ownership.SessionId);
        Assert.Equal(directory, ownership.WorkspacePath);
        Assert.NotEqual(oldClaim.ClaimToken, runner.Environment["WRIGHTY_CLAIM_TOKEN"]);
    }

    [Fact]
    public async Task Finished_item_emits_direct_claimless_interactive_review_command()
    {
        var backend = new LocalMarkdownTrackerBackend(new FakeIdentity(), clock);
        var config = new TrackerConfig
        {
            Backend = "local-markdown",
            DefaultPickFrom = "Todo",
            Worker = new WorkerConfig { RequirementsAssessment = new WorkerRequirementsAssessmentConfig { Mode = "inline" }, UseWorkerQueue = false },
            SourcePath = Path.Combine(directory, ".wrighty.json"),
            LocalMarkdown = new LocalMarkdownBackendConfig(),
            LeaseMinutes = 60
        };
        await backend.InitializeAsync(config, false, CancellationToken.None);
        var created = await backend.CreateAsync(config, new CreateWorkItemOperation(
            new CreateWorkItemRequest("Finish me", "Body", "Todo", "P1",
                AutomaticExecutionAllowed: true, AgentPolicy: "claude"), false), CancellationToken.None);
        var tracker = new TrackerService(new TrackerBackendRegistry([backend]));
        var runner = new FinishingRunner(async (environment, sessionId) =>
        {
            var claimant = new AgentExecutionContext(
                "claude",
                sessionId,
                AgentContextSource.ExplicitOption,
                ClaimantKind: ClaimantKind.Agent,
                ClaimantId: environment["WRIGHTY_CLAIMANT_ID"],
                ClaimToken: environment["WRIGHTY_CLAIM_TOKEN"]);
            await tracker.FinishAsync(
                config,
                created.Id,
                null,
                new ClaimHandle(claimant, environment["WRIGHTY_CLAIM_TOKEN"]),
                CancellationToken.None);
        });
        var events = new List<WorkerEvent>();
        var worker = new WorkerService(
            tracker,
            runner,
            new CurrentWorkspace(),
            [new ClaudeAgentAdapter()],
            (_, token) => Task.Delay(Timeout.InfiniteTimeSpan, token),
            () => clock.UtcNow);

        var summary = await worker.RunAsync(config,
            new WorkerOptions("claude", true, null, WorkspaceMode.Current,
                new Dictionary<string, string>(), null, TimeSpan.FromMinutes(10),
                FencedAction.Kill, null, "agent", false, false),
            directory, value =>
            {
                events.Add(value);
                return Task.CompletedTask;
            }, CancellationToken.None);

        Assert.Equal(new WorkerRunSummary(1), summary);
        var finished = Assert.Single(events, value => value.Type == "finished");
        Assert.Equal(
            $"cd '{directory}' && claude --resume '{finished.SessionId}'",
            finished.ReviewCommand);
        Assert.Equal("Completed the item.", finished.Message);
        Assert.DoesNotContain("WRIGHTY_CLAIMANT_ID", finished.ReviewCommand);
        Assert.DoesNotContain("WRIGHTY_CLAIM_TOKEN", finished.ReviewCommand);
        Assert.Equal(ClaimOwnershipState.Unclaimed,
            (await backend.GetClaimOwnershipAsync(config, created.Id, CancellationToken.None)).State);
    }

    [Fact]
    public async Task Finished_event_prefers_the_structured_summary_over_closing_prose()
    {
        var backend = new LocalMarkdownTrackerBackend(new FakeIdentity(), clock);
        var config = new TrackerConfig
        {
            Backend = "local-markdown",
            DefaultPickFrom = "Todo",
            Worker = new WorkerConfig
            {
                RequirementsAssessment = new WorkerRequirementsAssessmentConfig { Mode = "inline" },
                UseWorkerQueue = false
            },
            SourcePath = Path.Combine(directory, ".wrighty.json"),
            LocalMarkdown = new LocalMarkdownBackendConfig(),
            LeaseMinutes = 60
        };
        await backend.InitializeAsync(config, false, CancellationToken.None);
        var created = await backend.CreateAsync(config, new CreateWorkItemOperation(
            new CreateWorkItemRequest("Finish me", "Body", "Todo", "P1",
                AutomaticExecutionAllowed: true, AgentPolicy: "claude"), false),
            CancellationToken.None);
        var tracker = new TrackerService(new TrackerBackendRegistry([backend]));
        var runner = new FinishingRunner(async (environment, sessionId) =>
        {
            var claimant = new AgentExecutionContext(
                "claude", sessionId, AgentContextSource.ExplicitOption,
                ClaimantKind: ClaimantKind.Agent,
                ClaimantId: environment["WRIGHTY_CLAIMANT_ID"],
                ClaimToken: environment["WRIGHTY_CLAIM_TOKEN"]);
            await tracker.FinishAsync(
                config, created.Id, null,
                new ClaimHandle(claimant, environment["WRIGHTY_CLAIM_TOKEN"]),
                CancellationToken.None);
        },
        "A long explanation about how the work was done and several unrelated caveats.\n\n" +
        "```wrighty-report\n" +
        "{\"summary\":\"Updated the README with a repository description.\"}" +
        "\n```");
        var events = new List<WorkerEvent>();
        var worker = new WorkerService(
            tracker, runner, new CurrentWorkspace(), [new ClaudeAgentAdapter()],
            (_, token) => Task.Delay(Timeout.InfiniteTimeSpan, token),
            () => clock.UtcNow);

        await worker.RunAsync(
            config,
            new WorkerOptions(
                "claude", true, null, WorkspaceMode.Current,
                new Dictionary<string, string>(), null, TimeSpan.FromMinutes(10),
                FencedAction.Kill, null, "agent", false, false),
            directory,
            value =>
            {
                events.Add(value);
                return Task.CompletedTask;
            },
            CancellationToken.None);

        var finished = Assert.Single(events, value => value.Type == "finished");
        Assert.Equal("Updated the README with a repository description.", finished.Message);
        Assert.DoesNotContain("long explanation", finished.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("agent", false, 1, false, "push-pr")]
    [InlineData("agent", true, 0, true, null)]
    [InlineData("inspect", false, 0, true, "merge-local")]
    public async Task Keep_workspace_and_commit_policy_control_successful_worktree_cleanup(
        string commitPolicy,
        bool keepWorkspace,
        int expectedCleanupCalls,
        bool expectReviewCommand,
        string? integration)
    {
        var backend = new LocalMarkdownTrackerBackend(new FakeIdentity(), clock);
        var config = new TrackerConfig
        {
            Backend = "local-markdown",
            DefaultPickFrom = "Todo",
            SourcePath = Path.Combine(directory, ".wrighty.json"),
            LocalMarkdown = new LocalMarkdownBackendConfig(),
            LeaseMinutes = 60,
            Worker = new WorkerConfig
            {
                RequirementsAssessment = new WorkerRequirementsAssessmentConfig { Mode = "inline" },
                UseWorkerQueue = false,
                Completion = new WorkerCompletionConfig
                {
                    Commit = commitPolicy,
                    Integration = integration
                }
            }
        };
        await backend.InitializeAsync(config, false, CancellationToken.None);
        var created = await backend.CreateAsync(config, new CreateWorkItemOperation(
            new CreateWorkItemRequest("Finish worktree", "Body", "Todo", "P1",
                AutomaticExecutionAllowed: true, AgentPolicy: "claude"), false), CancellationToken.None);
        var tracker = new TrackerService(new TrackerBackendRegistry([backend]));
        var runner = new FinishingRunner(async (environment, sessionId) =>
        {
            var claimant = new AgentExecutionContext(
                "claude",
                sessionId,
                AgentContextSource.ExplicitOption,
                ClaimantKind: ClaimantKind.Agent,
                ClaimantId: environment["WRIGHTY_CLAIMANT_ID"],
                ClaimToken: environment["WRIGHTY_CLAIM_TOKEN"]);
            await tracker.FinishAsync(
                config,
                created.Id,
                null,
                new ClaimHandle(claimant, environment["WRIGHTY_CLAIM_TOKEN"]),
                CancellationToken.None);
        });
        var workspaces = new TrackingWorktree(directory);
        var events = new List<WorkerEvent>();
        var worker = new WorkerService(
            tracker,
            runner,
            workspaces,
            [new ClaudeAgentAdapter()],
            (_, token) => Task.Delay(Timeout.InfiniteTimeSpan, token),
            () => clock.UtcNow);

        await worker.RunAsync(config,
            new WorkerOptions("claude", true, null, WorkspaceMode.Worktree,
                new Dictionary<string, string>(), null, TimeSpan.FromMinutes(10),
                FencedAction.Kill, null, "agent", false, false,
                KeepWorkspace: keepWorkspace),
            directory, value =>
            {
                events.Add(value);
                return Task.CompletedTask;
            }, CancellationToken.None);

        Assert.Equal(expectedCleanupCalls, workspaces.CleanupCalls);
        var finished = Assert.Single(events, value => value.Type == "finished");
        Assert.Equal(expectReviewCommand, finished.ReviewCommand is not null);
        Assert.Equal(expectedCleanupCalls == 1,
            events.Any(value => value.Type == "workspace-removed"));
        Assert.NotNull(finished.Branch);
        if (commitPolicy == "inspect")
        {
            var actions = finished.OperatorActions!;
            Assert.Contains(actions, action =>
                action.Scenario.Contains("Review the uncommitted changes") &&
                action.Description.Contains("worker.completion.commit=inspect"));
            // The guided-completion action keeps the terminal command and the agent-session prompt
            // in separate fields (Commands vs AgentPrompt) so each renders as its own copy block in
            // the right order — the prompt is no longer buried in the description prose.
            Assert.Contains(actions, action =>
                action.Scenario.Contains("Guided completion") &&
                action.Commands.Any(command => command.Contains("wrighty resume-command")) &&
                action.AgentPrompt is { } prompt && prompt.Contains("/wrighty Complete item"));
        }
        switch (integration)
        {
            case "merge-local":
                var merge = Assert.Single(
                    finished.OperatorActions!,
                    action => action.Scenario.Contains("Merge into the main checkout"));
                Assert.Contains(merge.Commands, command => command.Contains("git add -A"));
                Assert.Contains(merge.Commands,
                    command => command.Contains($"git merge --ff-only '{finished.Branch}'"));
                Assert.Contains(merge.Commands, command => command.Contains("git worktree remove"));
                // git refuses to delete a branch checked out in a worktree, so the worktree must
                // be removed before the branch is deleted.
                var mergeCommands = merge.Commands.ToList();
                var removeIndex = mergeCommands.FindIndex(command => command.Contains("git worktree remove"));
                var deleteIndex = mergeCommands.FindIndex(command => command.Contains("git branch -d"));
                Assert.True(removeIndex >= 0 && deleteIndex > removeIndex,
                    "git worktree remove must precede git branch -d");
                break;
            case "push-pr":
                var push = Assert.Single(
                    finished.OperatorActions ?? [],
                    action => action.Scenario.Contains("pull request"));
                Assert.Contains(push.Commands,
                    command => command.Contains($"git push -u origin '{finished.Branch}'"));
                Assert.DoesNotContain(push.Commands, command => command.Contains("git add -A"));
                break;
        }
    }

    [Fact]
    public async Task Agent_policy_cleanup_refused_by_git_retains_worktree_and_explains_why()
    {
        var backend = new LocalMarkdownTrackerBackend(new FakeIdentity(), clock);
        var config = new TrackerConfig
        {
            Backend = "local-markdown",
            DefaultPickFrom = "Todo",
            SourcePath = Path.Combine(directory, ".wrighty.json"),
            LocalMarkdown = new LocalMarkdownBackendConfig(),
            LeaseMinutes = 60,
            Worker = new WorkerConfig
            {
                RequirementsAssessment = new WorkerRequirementsAssessmentConfig { Mode = "inline" },
                UseWorkerQueue = false,
                Completion = new WorkerCompletionConfig { Commit = "agent" }
            }
        };
        await backend.InitializeAsync(config, false, CancellationToken.None);
        var created = await backend.CreateAsync(config, new CreateWorkItemOperation(
            new CreateWorkItemRequest("Finish worktree", "Body", "Todo", "P1",
                AutomaticExecutionAllowed: true, AgentPolicy: "claude"), false), CancellationToken.None);
        var tracker = new TrackerService(new TrackerBackendRegistry([backend]));
        var runner = new FinishingRunner(async (environment, sessionId) =>
        {
            var claimant = new AgentExecutionContext(
                "claude", sessionId, AgentContextSource.ExplicitOption,
                ClaimantKind: ClaimantKind.Agent,
                ClaimantId: environment["WRIGHTY_CLAIMANT_ID"],
                ClaimToken: environment["WRIGHTY_CLAIM_TOKEN"]);
            await tracker.FinishAsync(config, created.Id, null,
                new ClaimHandle(claimant, environment["WRIGHTY_CLAIM_TOKEN"]),
                CancellationToken.None);
        });
        // git refuses to remove the worktree (e.g. untracked tool artifacts remain).
        var workspaces = new TrackingWorktree(directory, cleanupSucceeds: false);
        var events = new List<WorkerEvent>();
        var worker = new WorkerService(
            tracker, runner, workspaces, [new ClaudeAgentAdapter()],
            (_, token) => Task.Delay(Timeout.InfiniteTimeSpan, token),
            () => clock.UtcNow);

        await worker.RunAsync(config,
            new WorkerOptions("claude", true, null, WorkspaceMode.Worktree,
                new Dictionary<string, string>(), null, TimeSpan.FromMinutes(10),
                FencedAction.Kill, null, "agent", false, false),
            directory, value =>
            {
                events.Add(value);
                return Task.CompletedTask;
            }, CancellationToken.None);

        Assert.Equal(1, workspaces.CleanupCalls);
        Assert.DoesNotContain(events, value => value.Type == "workspace-removed");
        var finished = Assert.Single(events, value => value.Type == "finished");
        var retained = Assert.Single(finished.OperatorActions!,
            action => action.Scenario.Contains("Worktree retained"));
        Assert.Contains("uncommitted or untracked files", retained.Description);
        Assert.Contains($"wrighty workspaces cleanup {created.Id.Value}", retained.Description);
    }

    [Fact]
    public async Task Explicit_resume_hands_human_claim_back_and_runs_recorded_session_headlessly()
    {
        var backend = new LocalMarkdownTrackerBackend(new FakeIdentity(), clock);
        var config = new TrackerConfig
        {
            Backend = "local-markdown",
            DefaultPickFrom = "Todo",
            Worker = new WorkerConfig { RequirementsAssessment = new WorkerRequirementsAssessmentConfig { Mode = "inline" }, UseWorkerQueue = false },
            SourcePath = Path.Combine(directory, ".wrighty.json"),
            LocalMarkdown = new LocalMarkdownBackendConfig(),
            LeaseMinutes = 60
        };
        await backend.InitializeAsync(config, false, CancellationToken.None);
        var created = await backend.CreateAsync(config, new CreateWorkItemOperation(
            new CreateWorkItemRequest("Clarified item", "Actionable body", "In Progress", "P1",
                AutomaticExecutionAllowed: true, AgentPolicy: "claude"), false), CancellationToken.None);
        var originalContext = new AgentExecutionContext("claude", "session-original",
            AgentContextSource.ExplicitOption, ClaimantKind: ClaimantKind.Agent,
            ClaimantId: "agent:original");
        var original = await backend.TryClaimAsync(config, created.Id, originalContext,
            CancellationToken.None);
        await backend.RenewClaimAsync(config, created.Id,
            new ClaimHandle(originalContext, original.ClaimToken), directory, "session-original",
            CancellationToken.None);
        var human = await backend.TakeoverAsync(config, created.Id,
            new AgentExecutionContext(null, null, AgentContextSource.ExplicitOption,
                ClaimantKind: ClaimantKind.Human, ClaimantId: "human-cli"),
            original.ClaimToken, CancellationToken.None);
        var runner = new CapturingResumeRunner();
        var events = new List<WorkerEvent>();
        var worker = new WorkerService(
            new TrackerService(new TrackerBackendRegistry([backend])),
            runner,
            new CurrentWorkspace(),
            [new ClaudeAgentAdapter()],
            (_, token) => Task.Delay(Timeout.InfiniteTimeSpan, token),
            () => clock.UtcNow);

        await worker.PreflightResumeAsync(
            config,
            new WorkerOptions(null, true, null, WorkspaceMode.Current,
                new Dictionary<string, string>(), null, TimeSpan.FromMinutes(10),
                FencedAction.Kill, null, "agent", false, false),
            directory,
            created.Id,
            human.ClaimToken,
            value =>
            {
                events.Add(value);
                return Task.CompletedTask;
            },
            CancellationToken.None);
        var summary = await worker.ResumeAsync(config,
            new WorkerOptions(null, true, null, WorkspaceMode.Current,
                new Dictionary<string, string>(), null, TimeSpan.FromMinutes(10),
                FencedAction.Kill, null, "agent", false, false),
            directory, created.Id, human.ClaimToken, value =>
            {
                events.Add(value);
                return Task.CompletedTask;
            }, CancellationToken.None);

        Assert.Equal(new WorkerRunSummary(1, 1, 0), summary);
        Assert.Equal("claude", runner.Invocation!.Executable);
        Assert.Contains("--resume", runner.Invocation.Arguments);
        Assert.Contains("session-original", runner.Invocation.Arguments);
        Assert.DoesNotContain("--session-id", runner.Invocation.Arguments);
        Assert.Contains("Item local:1 has been clarified", runner.Invocation.Arguments[1]);
        Assert.StartsWith("agent:worker:", runner.Environment!["WRIGHTY_CLAIMANT_ID"]);
        Assert.NotEqual(human.ClaimToken, runner.Environment["WRIGHTY_CLAIM_TOKEN"]);
        Assert.Contains(events, value => value.Type == "ready" &&
                                         value.Message!.Contains(
                                             "current workspace",
                                             StringComparison.OrdinalIgnoreCase));
        Assert.Contains(events, value => value.Type == "resumed" &&
                                         value.SessionId == "session-original");
        var ownership = await backend.GetClaimOwnershipAsync(config, created.Id,
            CancellationToken.None);
        Assert.Equal("agent", ownership.ClaimantKind);
        Assert.Equal("claude", ownership.Agent);
        Assert.Equal("session-original", ownership.SessionId);
    }

    [Theory]
    [InlineData("session-replacement", "SESSION_ID_CHANGED")]
    [InlineData(null, "SESSION_ID_MISSING")]
    public async Task Explicit_resume_rejects_an_invalid_vendor_session_identity_without_replacing_the_address(
        string? returnedSessionId,
        string expectedFailureCode)
    {
        var backend = new LocalMarkdownTrackerBackend(new FakeIdentity(), clock);
        var config = new TrackerConfig
        {
            Backend = "local-markdown",
            DefaultPickFrom = "Todo",
            Worker = new WorkerConfig { RequirementsAssessment = new WorkerRequirementsAssessmentConfig { Mode = "inline" }, UseWorkerQueue = false },
            SourcePath = Path.Combine(directory, ".wrighty.json"),
            LocalMarkdown = new LocalMarkdownBackendConfig(),
            LeaseMinutes = 60
        };
        await backend.InitializeAsync(config, false, CancellationToken.None);
        var created = await backend.CreateAsync(config, new CreateWorkItemOperation(
            new CreateWorkItemRequest("Clarified item", "Actionable body", "In Progress", "P1",
                AutomaticExecutionAllowed: true, AgentPolicy: "claude"), false), CancellationToken.None);
        var originalContext = new AgentExecutionContext("claude", "session-original",
            AgentContextSource.ExplicitOption, ClaimantKind: ClaimantKind.Agent,
            ClaimantId: "agent:original");
        var original = await backend.TryClaimAsync(
            config, created.Id, originalContext, CancellationToken.None);
        await backend.RenewClaimAsync(
            config, created.Id, new ClaimHandle(originalContext, original.ClaimToken),
            directory, "session-original", CancellationToken.None);
        var human = await backend.TakeoverAsync(
            config,
            created.Id,
            new AgentExecutionContext(null, null, AgentContextSource.ExplicitOption,
                ClaimantKind: ClaimantKind.Human, ClaimantId: "human-cli"),
            original.ClaimToken,
            CancellationToken.None);
        var events = new List<WorkerEvent>();
        var worker = new WorkerService(
            new TrackerService(new TrackerBackendRegistry([backend])),
            new ChangedSessionRunner(returnedSessionId),
            new CurrentWorkspace(),
            [new ClaudeAgentAdapter()],
            (_, token) => Task.Delay(Timeout.InfiniteTimeSpan, token),
            () => clock.UtcNow);

        var summary = await worker.ResumeAsync(
            config,
            new WorkerOptions(null, true, null, WorkspaceMode.Current,
                new Dictionary<string, string>(), null, TimeSpan.FromMinutes(10),
                FencedAction.Kill, null, "agent", false, false),
            directory,
            created.Id,
            human.ClaimToken,
            value =>
            {
                events.Add(value);
                return Task.CompletedTask;
            },
            CancellationToken.None);

        Assert.Equal(new WorkerRunSummary(1, 1, 0), summary);
        var attention = Assert.Single(events, value => value.Type == "needs-attention");
        Assert.Equal(AgentOutcome.Rejected, attention.Outcome);
        Assert.Equal("session-original", attention.SessionId);
        Assert.Equal(expectedFailureCode, attention.Failure?.ProviderCode);
        var session = await backend.GetAgentSessionAsync(
            config, created.Id, CancellationToken.None);
        Assert.Equal("session-original", session!.SessionId);
    }

    [Fact]
    public async Task Busy_resume_workspace_is_rejected_before_human_claim_rotates_to_agent()
    {
        var backend = new LocalMarkdownTrackerBackend(new FakeIdentity(), clock);
        var config = new TrackerConfig
        {
            Backend = "local-markdown",
            DefaultPickFrom = "Todo",
            Worker = new WorkerConfig { RequirementsAssessment = new WorkerRequirementsAssessmentConfig { Mode = "inline" }, UseWorkerQueue = false },
            SourcePath = Path.Combine(directory, ".wrighty.json"),
            LocalMarkdown = new LocalMarkdownBackendConfig(),
            LeaseMinutes = 60
        };
        await backend.InitializeAsync(config, false, CancellationToken.None);
        var created = await backend.CreateAsync(config, new CreateWorkItemOperation(
            new CreateWorkItemRequest("Clarified item", "Actionable body", "In Progress", "P1",
                AutomaticExecutionAllowed: true, AgentPolicy: "claude"), false), CancellationToken.None);
        var originalContext = new AgentExecutionContext("claude", "session-original",
            AgentContextSource.ExplicitOption, ClaimantKind: ClaimantKind.Agent,
            ClaimantId: "agent:original");
        var original = await backend.TryClaimAsync(config, created.Id, originalContext,
            CancellationToken.None);
        await backend.RenewClaimAsync(config, created.Id,
            new ClaimHandle(originalContext, original.ClaimToken), directory, "session-original",
            CancellationToken.None);
        var human = await backend.TakeoverAsync(config, created.Id,
            new AgentExecutionContext(null, null, AgentContextSource.ExplicitOption,
                ClaimantKind: ClaimantKind.Human, ClaimantId: "human-cli"),
            original.ClaimToken, CancellationToken.None);
        var workspaceLock = new RejectingWorkspaceLock();
        var worker = new WorkerService(
            new TrackerService(new TrackerBackendRegistry([backend])),
            new FailIfRunRunner(),
            new CurrentWorkspace(),
            [new ClaudeAgentAdapter()],
            clock: () => clock.UtcNow,
            workspaceExecutionLock: workspaceLock);

        var exception = await Assert.ThrowsAsync<TrackerException>(() => worker.ResumeAsync(
            config,
            new WorkerOptions(null, true, null, WorkspaceMode.Current,
                new Dictionary<string, string>(), null, TimeSpan.FromMinutes(10),
                FencedAction.Kill, null, "agent", false, false),
            directory,
            created.Id,
            human.ClaimToken,
            _ => Task.CompletedTask,
            CancellationToken.None));

        Assert.Equal("WORKSPACE_BUSY", exception.Code);
        var ownership = await backend.GetClaimOwnershipAsync(
            config, created.Id, CancellationToken.None);
        Assert.Equal("human", ownership.ClaimantKind);
        Assert.Equal("human-cli", ownership.ClaimantId);
        Assert.Equal("session-original", ownership.SessionId);
    }

    [Fact]
    public async Task Worker_once_with_only_claimed_eligible_work_emits_no_item()
    {
        var backend = new LocalMarkdownTrackerBackend(new FakeIdentity(), clock);
        var config = new TrackerConfig
        {
            Backend = "local-markdown",
            DefaultPickFrom = "Todo",
            Worker = new WorkerConfig { RequirementsAssessment = new WorkerRequirementsAssessmentConfig { Mode = "inline" }, UseWorkerQueue = false },
            SourcePath = Path.Combine(directory, ".wrighty.json"),
            LocalMarkdown = new LocalMarkdownBackendConfig(),
            LeaseMinutes = 60
        };
        await backend.InitializeAsync(config, false, CancellationToken.None);
        var created = await backend.CreateAsync(config, new CreateWorkItemOperation(
            new CreateWorkItemRequest("Claimed item", "Body", "Todo", "P1",
                AutomaticExecutionAllowed: true, AgentPolicy: "claude"), false), CancellationToken.None);
        await backend.TryClaimAsync(config, created.Id,
            new AgentExecutionContext(null, null, AgentContextSource.None,
                ClaimantKind: ClaimantKind.Human, ClaimantId: "web:test"),
            CancellationToken.None);
        var events = new List<WorkerEvent>();
        var worker = new WorkerService(
            new TrackerService(new TrackerBackendRegistry([backend])),
            new FailIfRunRunner(),
            new CurrentWorkspace(),
            [new ClaudeAgentAdapter()],
            clock: () => clock.UtcNow);

        var summary = await worker.RunAsync(config,
            new WorkerOptions(null, true, null, WorkspaceMode.Current,
                new Dictionary<string, string>(), null, TimeSpan.FromMinutes(10),
                FencedAction.Kill, null, "agent", false, false),
            directory, value =>
            {
                events.Add(value);
                return Task.CompletedTask;
            }, CancellationToken.None);

        Assert.Equal(new WorkerRunSummary(0), summary);
        var noItem = Assert.Single(events);
        Assert.Equal("no-item", noItem.Type);
        Assert.Contains("No worker item could be claimed", noItem.Message);
        Assert.Equal(1, noItem.Candidates!.Eligible);
    }

    [Fact]
    public async Task Worker_no_item_reports_status_auto_and_agent_candidate_counts()
    {
        var backend = new LocalMarkdownTrackerBackend(new FakeIdentity(), clock);
        var config = new TrackerConfig
        {
            Backend = "local-markdown",
            DefaultPickFrom = "Todo",
            Worker = new WorkerConfig { RequirementsAssessment = new WorkerRequirementsAssessmentConfig { Mode = "inline" }, UseWorkerQueue = false },
            SourcePath = Path.Combine(directory, ".wrighty.json"),
            LocalMarkdown = new LocalMarkdownBackendConfig(),
            LeaseMinutes = 60
        };
        await backend.InitializeAsync(config, false, CancellationToken.None);
        await backend.CreateAsync(config, new CreateWorkItemOperation(
            new CreateWorkItemRequest("Missing both", "Body", "Todo", "P1"),
            false), CancellationToken.None);
        await backend.CreateAsync(config, new CreateWorkItemOperation(
            new CreateWorkItemRequest("Missing auto", "Body", "Todo", "P2",
                AgentPolicy: "claude"),
            false), CancellationToken.None);
        await backend.CreateAsync(config, new CreateWorkItemOperation(
            new CreateWorkItemRequest("Missing agent", "Body", "Todo", "P3",
                AutomaticExecutionAllowed: true),
            false), CancellationToken.None);
        var events = new List<WorkerEvent>();
        var worker = new WorkerService(
            new TrackerService(new TrackerBackendRegistry([backend])),
            new FailIfRunRunner(),
            new CurrentWorkspace(),
            [new ClaudeAgentAdapter()],
            clock: () => clock.UtcNow);

        var summary = await worker.RunAsync(config,
            new WorkerOptions(null, true, null, WorkspaceMode.Current,
                new Dictionary<string, string>(), null, TimeSpan.FromMinutes(10),
                FencedAction.Kill, null, "agent", false, false),
            directory, value =>
            {
                events.Add(value);
                return Task.CompletedTask;
            }, CancellationToken.None);

        Assert.Equal(new WorkerRunSummary(0), summary);
        var noItem = Assert.Single(events);
        var candidates = Assert.IsType<WorkerCandidateSummary>(noItem.Candidates);
        Assert.Equal("Todo", candidates.Status);
        Assert.Equal(3, candidates.StatusItems);
        Assert.Equal(2, candidates.MissingAuto);
        Assert.Equal(2, candidates.MissingItemAgent);
        Assert.Equal(0, candidates.FilteredOut);
        Assert.Equal(1, candidates.UnresolvedAgent);
        Assert.Equal(0, candidates.Eligible);
        Assert.Contains("3 active items", noItem.Message);
        Assert.Contains("2 manual-only", noItem.Message);
        Assert.Contains("2 missing an item agent policy", noItem.Message);
        Assert.Contains("--agent > agent policy > worker.defaultAgent", noItem.Message);
    }

    [Fact]
    public async Task Continuous_worker_names_the_unavailable_agent_executable()
    {
        var backend = new LocalMarkdownTrackerBackend(new FakeIdentity(), clock);
        var config = new TrackerConfig
        {
            Backend = "local-markdown",
            DefaultPickFrom = "Todo",
            Worker = new WorkerConfig { RequirementsAssessment = new WorkerRequirementsAssessmentConfig { Mode = "inline" }, UseWorkerQueue = false },
            SourcePath = Path.Combine(directory, ".wrighty.json"),
            LocalMarkdown = new LocalMarkdownBackendConfig(),
            LeaseMinutes = 60
        };
        await backend.InitializeAsync(config, false, CancellationToken.None);
        await backend.CreateAsync(config, new CreateWorkItemOperation(
            new CreateWorkItemRequest("Needs claude", "Body", "Todo", "P1",
                AutomaticExecutionAllowed: true, AgentPolicy: "claude"),
            false), CancellationToken.None);
        var events = new List<WorkerEvent>();
        var worker = new WorkerService(
            new TrackerService(new TrackerBackendRegistry([backend])),
            new FailIfRunRunner(),
            new CurrentWorkspace(),
            [new ClaudeAgentAdapter()],
            delay: (_, _) =>
            {
                clock.UtcNow = clock.UtcNow.AddMinutes(2);
                return Task.CompletedTask;
            },
            clock: () => clock.UtcNow,
            runtimeCatalog: new MissingClaudeRuntimeCatalog());

        await worker.RunAsync(config,
            new WorkerOptions(null, false, null, WorkspaceMode.Current,
                new Dictionary<string, string>(), TimeSpan.FromMinutes(1),
                TimeSpan.FromMinutes(10), FencedAction.Kill, null, "agent", false, false),
            directory, value =>
            {
                events.Add(value);
                return Task.CompletedTask;
            }, CancellationToken.None);

        var unavailable = Assert.Single(events, value => value.Type == "agent-unavailable");
        Assert.Contains(
            "1 otherwise eligible item requires an unavailable local agent executable: claude (1)",
            unavailable.Message);
    }

    private sealed class MissingClaudeRuntimeCatalog : IAgentRuntimeCatalog
    {
        private readonly AgentRuntimeSnapshot snapshot = new(
        [
            new AgentRuntime("claude", "claude", Supported: true,
                AgentInstallationState.Missing, null),
            new AgentRuntime("codex", "codex", Supported: true,
                AgentInstallationState.Installed, "/tools/codex")
        ]);

        public AgentRuntimeSnapshot Snapshot() => snapshot;
    }

    [Fact]
    public async Task Worker_does_not_claim_a_candidate_with_unapproved_projected_context()
    {
        var inner = new LocalMarkdownTrackerBackend(new FakeIdentity(), clock);
        var config = new TrackerConfig
        {
            Backend = "local-markdown",
            DefaultPickFrom = "Todo",
            Worker = new WorkerConfig { RequirementsAssessment = new WorkerRequirementsAssessmentConfig { Mode = "inline" }, UseWorkerQueue = false },
            SourcePath = Path.Combine(directory, ".wrighty.json"),
            LocalMarkdown = new LocalMarkdownBackendConfig(),
            LeaseMinutes = 60
        };
        await inner.InitializeAsync(config, false, CancellationToken.None);
        var created = await inner.CreateAsync(config, new CreateWorkItemOperation(
            new CreateWorkItemRequest(
                "Needs context approval",
                "Body",
                "Todo",
                "P1",
                AutomaticExecutionAllowed: true,
                AgentPolicy: "claude"),
            false), CancellationToken.None);
        var backend = new ProjectedContextApprovalBackend(
            inner,
            new Dictionary<WorkItemId, bool> { [created.Id] = false });
        var events = new List<WorkerEvent>();
        var worker = new WorkerService(
            new TrackerService(new TrackerBackendRegistry([backend])),
            new FailIfRunRunner(),
            new CurrentWorkspace(),
            [new ClaudeAgentAdapter()],
            clock: () => clock.UtcNow);

        var summary = await worker.RunAsync(
            config,
            new WorkerOptions(
                null,
                true,
                null,
                WorkspaceMode.Current,
                new Dictionary<string, string>(),
                null,
                TimeSpan.FromMinutes(10),
                FencedAction.Kill,
                null,
                "agent",
                false,
                false),
            directory,
            value =>
            {
                events.Add(value);
                return Task.CompletedTask;
            },
            CancellationToken.None);

        Assert.Equal(new WorkerRunSummary(0), summary);
        var noItem = Assert.Single(events);
        Assert.Equal("no-item", noItem.Type);
        Assert.Equal(1, noItem.Candidates?.ContextNotApproved);
        Assert.Contains("1 without approved projected context", noItem.Message);
        Assert.Equal("Todo",
            (await inner.GetAsync(config, created.Id, CancellationToken.None))?.Status);
        Assert.Equal(
            ClaimOwnershipState.Unclaimed,
            (await inner.GetClaimOwnershipAsync(config, created.Id, CancellationToken.None)).State);
    }

    [Fact]
    public async Task Worker_preflight_continues_to_a_later_context_approved_candidate()
    {
        var inner = new LocalMarkdownTrackerBackend(new FakeIdentity(), clock);
        var config = new TrackerConfig
        {
            Backend = "local-markdown",
            DefaultPickFrom = "Todo",
            Worker = new WorkerConfig { RequirementsAssessment = new WorkerRequirementsAssessmentConfig { Mode = "inline" }, UseWorkerQueue = false },
            SourcePath = Path.Combine(directory, ".wrighty.json"),
            LocalMarkdown = new LocalMarkdownBackendConfig(),
            LeaseMinutes = 60
        };
        await inner.InitializeAsync(config, false, CancellationToken.None);
        var unapproved = await inner.CreateAsync(config, new CreateWorkItemOperation(
            new CreateWorkItemRequest(
                "Unapproved first",
                "Body",
                "Todo",
                "P1",
                AutomaticExecutionAllowed: true,
                AgentPolicy: "claude"),
            false), CancellationToken.None);
        var approved = await inner.CreateAsync(config, new CreateWorkItemOperation(
            new CreateWorkItemRequest(
                "Approved second",
                "Body",
                "Todo",
                "P2",
                AutomaticExecutionAllowed: true,
                AgentPolicy: "claude"),
            false), CancellationToken.None);
        var backend = new ProjectedContextApprovalBackend(
            inner,
            new Dictionary<WorkItemId, bool>
            {
                [unapproved.Id] = false,
                [approved.Id] = true
            });
        var events = new List<WorkerEvent>();
        var worker = new WorkerService(
            new TrackerService(new TrackerBackendRegistry([backend])),
            new FailIfRunRunner(),
            new CurrentWorkspace(),
            [new ClaudeAgentAdapter()],
            clock: () => clock.UtcNow);

        var hasWork = await worker.PreflightAsync(
            config,
            new WorkerOptions(
                null,
                true,
                null,
                WorkspaceMode.Current,
                new Dictionary<string, string>(),
                null,
                TimeSpan.FromMinutes(10),
                FencedAction.Kill,
                null,
                "agent",
                false,
                false),
            directory,
            value =>
            {
                events.Add(value);
                return Task.CompletedTask;
            },
            CancellationToken.None);

        Assert.True(hasWork);
        var ready = Assert.Single(events);
        Assert.Equal("ready", ready.Type);
        Assert.Equal(approved.Id.Value, ready.ItemId);
        Assert.Equal(1, ready.Candidates?.ContextNotApproved);
        Assert.Equal(1, ready.Candidates?.Claimable);
        Assert.Equal(
            ClaimOwnershipState.Unclaimed,
            (await inner.GetClaimOwnershipAsync(config, unapproved.Id, CancellationToken.None)).State);
    }

    [Fact]
    public async Task Worker_preflight_reports_claimable_count_and_first_available_candidate_without_claiming()
    {
        var backend = new LocalMarkdownTrackerBackend(new FakeIdentity(), clock);
        var config = new TrackerConfig
        {
            Backend = "local-markdown",
            DefaultPickFrom = "Todo",
            Worker = new WorkerConfig { RequirementsAssessment = new WorkerRequirementsAssessmentConfig { Mode = "inline" }, UseWorkerQueue = false },
            SourcePath = Path.Combine(directory, ".wrighty.json"),
            LocalMarkdown = new LocalMarkdownBackendConfig(),
            LeaseMinutes = 60
        };
        await backend.InitializeAsync(config, false, CancellationToken.None);
        var claimed = await backend.CreateAsync(config, new CreateWorkItemOperation(
            new CreateWorkItemRequest("Claimed first", "Body", "Todo", "P1",
                AutomaticExecutionAllowed: true, AgentPolicy: "claude"),
            false), CancellationToken.None);
        var available = await backend.CreateAsync(config, new CreateWorkItemOperation(
            new CreateWorkItemRequest("Available second", "Body", "Todo", "P2",
                AutomaticExecutionAllowed: true, AgentPolicy: "claude"),
            false), CancellationToken.None);
        await backend.CreateAsync(config, new CreateWorkItemOperation(
            new CreateWorkItemRequest("Not opted in", "Body", "Todo", "P3"),
            false), CancellationToken.None);
        await backend.TryClaimAsync(config, claimed.Id,
            new AgentExecutionContext(
                null,
                null,
                AgentContextSource.None,
                ClaimantKind: ClaimantKind.Human,
                ClaimantId: "web:test"),
            CancellationToken.None);
        var events = new List<WorkerEvent>();
        var worker = new WorkerService(
            new TrackerService(new TrackerBackendRegistry([backend])),
            new FailIfRunRunner(),
            new CurrentWorkspace(),
            [new ClaudeAgentAdapter()],
            clock: () => clock.UtcNow);

        var hasWork = await worker.PreflightAsync(
            config,
            new WorkerOptions(null, true, null, WorkspaceMode.Current,
                new Dictionary<string, string>(), null, TimeSpan.FromMinutes(10),
                FencedAction.Kill, null, "agent", false, false),
            directory,
            value =>
            {
                events.Add(value);
                return Task.CompletedTask;
            },
            CancellationToken.None);

        Assert.True(hasWork);
        var ready = Assert.Single(events);
        Assert.Equal("ready", ready.Type);
        Assert.Equal(available.Id.Value, ready.ItemId);
        Assert.Equal("claude", ready.Agent);
        Assert.Equal(3, ready.Candidates!.StatusItems);
        Assert.Equal(1, ready.Candidates.MissingAuto);
        Assert.Equal(2, ready.Candidates.Eligible);
        Assert.Equal(1, ready.Candidates.Claimed);
        Assert.Equal(1, ready.Candidates.Claimable);
        Assert.Contains("1 currently claimable worker item", ready.Message);
        Assert.Equal(ClaimOwnershipState.Unclaimed,
            (await backend.GetClaimOwnershipAsync(config, available.Id, CancellationToken.None)).State);
    }

    [Fact]
    public async Task Continuous_worker_uses_compact_backoff_aware_idle_messages()
    {
        var backend = new LocalMarkdownTrackerBackend(new FakeIdentity(), clock);
        var config = new TrackerConfig
        {
            Backend = "local-markdown",
            DefaultPickFrom = "Todo",
            Worker = new WorkerConfig { RequirementsAssessment = new WorkerRequirementsAssessmentConfig { Mode = "inline" }, UseWorkerQueue = false },
            SourcePath = Path.Combine(directory, ".wrighty.json"),
            LocalMarkdown = new LocalMarkdownBackendConfig(),
            LeaseMinutes = 60
        };
        await backend.InitializeAsync(config, false, CancellationToken.None);
        await backend.CreateAsync(config, new CreateWorkItemOperation(
            new CreateWorkItemRequest("Not opted in", "Body", "Todo", "P1"),
            false), CancellationToken.None);
        var events = new List<WorkerEvent>();
        using var cancellation = new CancellationTokenSource();
        var delays = new List<TimeSpan>();
        var worker = new WorkerService(
            new TrackerService(new TrackerBackendRegistry([backend])),
            new FailIfRunRunner(),
            new CurrentWorkspace(),
            [new ClaudeAgentAdapter()],
            (delay, _) =>
            {
                delays.Add(delay);
                if (delays.Count == 3) cancellation.Cancel();
                return Task.CompletedTask;
            },
            () => clock.UtcNow);

        var summary = await worker.RunAsync(
            config,
            new WorkerOptions(null, false, null, WorkspaceMode.Current,
                new Dictionary<string, string>(), null, TimeSpan.FromMinutes(10),
                FencedAction.Kill, null, "agent", false, false),
            directory,
            value =>
            {
                events.Add(value);
                return Task.CompletedTask;
            },
            cancellation.Token);

        Assert.Equal(new WorkerRunSummary(0), summary);
        Assert.Equal(
            [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(8)],
            delays);
        Assert.Equal(
            [
                "Waiting for queued resumable sessions or claimable items in 'Todo'; retrying in 2s.",
                "Waiting for queued resumable sessions or claimable items in 'Todo'; retrying in 4s.",
                "Waiting for queued resumable sessions or claimable items in 'Todo'; retrying in 8s."
            ],
            events.Select(value => Assert.IsType<string>(value.Message)).ToArray());
        Assert.All(events, value =>
        {
            Assert.Equal("idle", value.Type);
            Assert.DoesNotContain("Candidates must", value.Message);
            Assert.NotNull(value.Candidates);
        });
    }

    [Fact]
    public async Task Continuous_worker_reports_when_new_opted_in_items_need_an_agent_once()
    {
        var backend = new LocalMarkdownTrackerBackend(new FakeIdentity(), clock);
        var config = new TrackerConfig
        {
            Backend = "local-markdown",
            DefaultPickFrom = "Todo",
            Worker = new WorkerConfig { RequirementsAssessment = new WorkerRequirementsAssessmentConfig { Mode = "inline" }, UseWorkerQueue = false },
            SourcePath = Path.Combine(directory, ".wrighty.json"),
            LocalMarkdown = new LocalMarkdownBackendConfig(),
            LeaseMinutes = 60
        };
        await backend.InitializeAsync(config, false, CancellationToken.None);
        var events = new List<WorkerEvent>();
        using var cancellation = new CancellationTokenSource();
        var delays = new List<TimeSpan>();
        var worker = new WorkerService(
            new TrackerService(new TrackerBackendRegistry([backend])),
            new FailIfRunRunner(),
            new CurrentWorkspace(),
            [new ClaudeAgentAdapter()],
            async (delay, _) =>
            {
                delays.Add(delay);
                if (delays.Count == 1)
                {
                    await backend.CreateAsync(config, new CreateWorkItemOperation(
                        new CreateWorkItemRequest(
                            "Needs an agent",
                            "Body",
                            "Todo",
                            "P1",
                            AutomaticExecutionAllowed: true),
                        false),
                        CancellationToken.None);
                }
                if (delays.Count == 3)
                    cancellation.Cancel();
            },
            () => clock.UtcNow);

        var summary = await worker.RunAsync(
            config,
            new WorkerOptions(null, false, null, WorkspaceMode.Current,
                new Dictionary<string, string>(), null, TimeSpan.FromMinutes(10),
                FencedAction.Kill, null, "agent", false, false),
            directory,
            value =>
            {
                events.Add(value);
                return Task.CompletedTask;
            },
            cancellation.Token);

        Assert.Equal(new WorkerRunSummary(0), summary);
        Assert.Equal(3, events.Count);
        Assert.Equal(
            "Waiting for queued resumable sessions or claimable items in 'Todo'; retrying in 2s.",
            events[0].Message);
        Assert.Equal(
            "1 automation-enabled item needs an agent; set agent policy, --agent, " +
            "or worker.defaultAgent.",
            events[1].Message);
        Assert.Equal(
            "Waiting for queued resumable sessions or claimable items in 'Todo'; retrying in 8s.",
            events[2].Message);
        Assert.All(events, value => Assert.Equal("idle", value.Type));
        Assert.Equal(1, events[1].Candidates!.UnresolvedAgent);
    }

    [Fact]
    public async Task Worker_agent_resolution_prefers_item_over_config_and_option_over_item()
    {
        var backend = new LocalMarkdownTrackerBackend(new FakeIdentity(), clock);
        var config = new TrackerConfig
        {
            Backend = "local-markdown",
            DefaultPickFrom = "Todo",
            SourcePath = Path.Combine(directory, ".wrighty.json"),
            LocalMarkdown = new LocalMarkdownBackendConfig(),
            Worker = new WorkerConfig { RequirementsAssessment = new WorkerRequirementsAssessmentConfig { Mode = "inline" }, UseWorkerQueue = false, DefaultAgent = "codex" },
            LeaseMinutes = 60
        };
        await backend.InitializeAsync(config, false, CancellationToken.None);
        await backend.CreateAsync(config, new CreateWorkItemOperation(
            new CreateWorkItemRequest("Pinned item", "Body", "Todo", "P1",
                AutomaticExecutionAllowed: true, AgentPolicy: "claude"),
            false), CancellationToken.None);
        var worker = new WorkerService(
            new TrackerService(new TrackerBackendRegistry([backend])),
            new FailIfRunRunner(),
            new CurrentWorkspace(),
            [new ClaudeAgentAdapter(), new CodexAgentAdapter(), new CopilotAgentAdapter()],
            clock: () => clock.UtcNow);

        var itemEvents = new List<WorkerEvent>();
        await worker.RunAsync(config,
            new WorkerOptions(null, true, null, WorkspaceMode.Current,
                new Dictionary<string, string>(), null, TimeSpan.FromMinutes(10),
                FencedAction.Kill, null, "agent", true, false),
            directory, value =>
            {
                itemEvents.Add(value);
                return Task.CompletedTask;
            }, CancellationToken.None);
        Assert.Equal("claude", Assert.Single(itemEvents, value => value.Type == "dry-run").Agent);

        var optionEvents = new List<WorkerEvent>();
        await worker.RunAsync(config,
            new WorkerOptions("copilot", true, null, WorkspaceMode.Current,
                new Dictionary<string, string>(), null, TimeSpan.FromMinutes(10),
                FencedAction.Kill, null, "agent", true, false),
            directory, value =>
            {
                optionEvents.Add(value);
                return Task.CompletedTask;
            }, CancellationToken.None);
        Assert.Equal("copilot", Assert.Single(optionEvents, value => value.Type == "dry-run").Agent);
    }

    [Fact]
    public async Task Fresh_worker_defaults_to_enforced_assessment_with_inline_and_off_fallbacks()
    {
        var backend = new LocalMarkdownTrackerBackend(new FakeIdentity(), clock);
        var config = new TrackerConfig
        {
            Backend = "local-markdown",
            DefaultPickFrom = "Todo",
            SourcePath = Path.Combine(directory, ".wrighty.json"),
            LocalMarkdown = new LocalMarkdownBackendConfig(),
            Worker = new WorkerConfig { RequirementsAssessment = new WorkerRequirementsAssessmentConfig { Mode = "enforced" }, UseWorkerQueue = false },
            LeaseMinutes = 60
        };
        await backend.InitializeAsync(config, false, CancellationToken.None);
        await backend.CreateAsync(config, new CreateWorkItemOperation(
            new CreateWorkItemRequest("Needs a semantic gate", "Body", "Todo", "P1",
                AutomaticExecutionAllowed: true, AgentPolicy: "claude"),
            false), CancellationToken.None);
        var worker = new WorkerService(
            new TrackerService(new TrackerBackendRegistry([backend])),
            new FailIfRunRunner(),
            new CurrentWorkspace(),
            [new ClaudeAgentAdapter()],
            clock: () => clock.UtcNow);
        var options = new WorkerOptions(null, true, null, WorkspaceMode.Current,
            new Dictionary<string, string>(), null, TimeSpan.FromMinutes(10),
            FencedAction.Kill, null, "agent", true, false);

        var enforcedEvents = new List<WorkerEvent>();
        await worker.RunAsync(config, options, directory, value =>
        {
            enforcedEvents.Add(value);
            return Task.CompletedTask;
        }, CancellationToken.None);
        var enforcedLaunch = Assert.Single(enforcedEvents, value => value.Type == "dry-run");
        Assert.Equal("read-only", enforcedLaunch.Permissions!.ProfileName);
        Assert.Contains("Restricted requirements assessment", enforcedLaunch.Message);
        Assert.DoesNotContain("WRIGHTY_CLAIM_TOKEN", enforcedLaunch.Message);
        Assert.DoesNotContain(
            enforcedEvents,
            value => value.Type == "requirements-assessment-disabled");

        var inlineConfig = config with
        {
            Worker = config.Worker! with
            {
                RequirementsAssessment = new WorkerRequirementsAssessmentConfig { Mode = "inline" }
            }
        };
        var inlineEvents = new List<WorkerEvent>();
        await worker.RunAsync(inlineConfig, options, directory, value =>
        {
            inlineEvents.Add(value);
            return Task.CompletedTask;
        }, CancellationToken.None);
        var inlineLaunch = Assert.Single(inlineEvents, value => value.Type == "dry-run");
        Assert.Equal("workspace", inlineLaunch.Permissions!.ProfileName);
        Assert.Contains(
            "Requirements readiness comes first",
            string.Join(' ', inlineLaunch.Arguments!));

        var disabledConfig = config with
        {
            Worker = config.Worker! with
            {
                RequirementsAssessment = new WorkerRequirementsAssessmentConfig { Mode = "off" }
            }
        };
        var disabledEvents = new List<WorkerEvent>();
        await worker.RunAsync(disabledConfig, options, directory, value =>
        {
            disabledEvents.Add(value);
            return Task.CompletedTask;
        }, CancellationToken.None);
        var disabledLaunch = Assert.Single(disabledEvents, value => value.Type == "dry-run");
        Assert.DoesNotContain(
            "Requirements readiness comes first",
            string.Join(' ', disabledLaunch.Arguments!));
        var warning = Assert.Single(
            disabledEvents,
            value => value.Type == "requirements-assessment-disabled");
        Assert.Contains("ordinary blocker handling remains active", warning.Message);
    }

    [Fact]
    public async Task Enforced_assessment_resumes_the_same_session_only_after_a_ready_verdict()
    {
        var backend = new LocalMarkdownTrackerBackend(new FakeIdentity(), clock);
        var config = new TrackerConfig
        {
            Backend = "local-markdown",
            DefaultPickFrom = "Todo",
            SourcePath = Path.Combine(directory, ".wrighty.json"),
            LocalMarkdown = new LocalMarkdownBackendConfig(),
            Worker = new WorkerConfig { RequirementsAssessment = new WorkerRequirementsAssessmentConfig { Mode = "enforced" }, UseWorkerQueue = false },
            LeaseMinutes = 60
        };
        await backend.InitializeAsync(config, false, CancellationToken.None);
        await backend.CreateAsync(config, new CreateWorkItemOperation(
            new CreateWorkItemRequest(
                "Ready item", "Create result.txt containing OK and verify its bytes.",
                "Todo", "P1", AutomaticExecutionAllowed: true, AgentPolicy: "claude"),
            false), CancellationToken.None);
        var runner = new ReadyThenImplementationRunner();
        var events = new List<WorkerEvent>();
        var worker = new WorkerService(
            new TrackerService(new TrackerBackendRegistry([backend])),
            runner,
            new CurrentWorkspace(),
            [new ClaudeAgentAdapter()],
            clock: () => clock.UtcNow);

        var summary = await worker.RunAsync(
            config,
            new WorkerOptions(null, true, null, WorkspaceMode.Current,
                new Dictionary<string, string>(), null, TimeSpan.FromMinutes(10),
                FencedAction.Kill, null, "agent", false, false),
            directory,
            value =>
            {
                events.Add(value);
                return Task.CompletedTask;
            },
            CancellationToken.None);

        Assert.Equal(1, summary.NeedsAttention);
        Assert.Equal(2, runner.Invocations.Count);
        Assert.Empty(runner.Environments[0]);
        Assert.DoesNotContain("WRIGHTY_CLAIM_TOKEN", runner.Invocations[0].Environment.Keys);
        Assert.Contains("WRIGHTY_CLAIMANT_ID",
            runner.Invocations[0].EnvironmentVariablesToRemove!);
        Assert.Contains("WRIGHTY_CLAIM_TOKEN",
            runner.Invocations[0].EnvironmentVariablesToRemove!);
        Assert.Contains(TrackerConfigLoader.ConfigPathEnvironmentVariable,
            runner.Invocations[0].EnvironmentVariablesToRemove!);
        Assert.Contains("WRIGHTY_CLAIM_TOKEN", runner.Environments[1].Keys);
        Assert.Contains("dontAsk", runner.Invocations[0].Arguments);
        Assert.Contains("Read Glob Grep", runner.Invocations[0].Arguments);
        Assert.Contains("--resume", runner.Invocations[1].Arguments);
        Assert.Contains("acceptEdits", runner.Invocations[1].Arguments);
        Assert.Contains("Your only task in this turn", runner.Invocations[0].StandardInput);
        Assert.Contains("implementation admitted", runner.Invocations[1].StandardInput);
        Assert.Single(events, value => value.Type == "requirements-assessment-started");
        var ready = Assert.Single(events, value => value.Type == "requirements-assessment-ready");
        var started = Assert.Single(events, value => value.Type == "started");
        Assert.Equal(ready.SessionId, started.SessionId);
    }

    [Fact]
    public async Task Enforced_assessment_retains_needs_clarification_without_a_privileged_turn()
    {
        var backend = new LocalMarkdownTrackerBackend(new FakeIdentity(), clock);
        var config = new TrackerConfig
        {
            Backend = "local-markdown",
            DefaultPickFrom = "Todo",
            SourcePath = Path.Combine(directory, ".wrighty.json"),
            LocalMarkdown = new LocalMarkdownBackendConfig(),
            Worker = new WorkerConfig { RequirementsAssessment = new WorkerRequirementsAssessmentConfig { Mode = "enforced" }, UseWorkerQueue = false },
            LeaseMinutes = 60
        };
        await backend.InitializeAsync(config, false, CancellationToken.None);
        await backend.CreateAsync(config, new CreateWorkItemOperation(
            new CreateWorkItemRequest(
                "Ambiguous item", "Use either BLUE or GREEN, but the user has not selected one.",
                "Todo", "P1", AutomaticExecutionAllowed: true, AgentPolicy: "claude"),
            false), CancellationToken.None);
        var runner = new NeedsClarificationRunner();
        var events = new List<WorkerEvent>();
        var worker = new WorkerService(
            new TrackerService(new TrackerBackendRegistry([backend])),
            runner,
            new CurrentWorkspace(),
            [new ClaudeAgentAdapter()],
            clock: () => clock.UtcNow);

        var summary = await worker.RunAsync(
            config,
            new WorkerOptions(null, true, null, WorkspaceMode.Current,
                new Dictionary<string, string>(), null, TimeSpan.FromMinutes(10),
                FencedAction.Kill, null, "agent", false, false),
            directory,
            value =>
            {
                events.Add(value);
                return Task.CompletedTask;
            },
            CancellationToken.None);

        Assert.Equal(1, summary.NeedsAttention);
        Assert.Equal(1, runner.Calls);
        Assert.Empty(runner.Environment!);
        Assert.Single(events,
            value => value.Type == "requirements-assessment-needs-clarification");
        Assert.DoesNotContain(events, value => value.Type == "started");
        Assert.Contains("BLUE or GREEN", Assert.Single(
            events, value => value.Type == "needs-attention").Message);
    }

    [Theory]
    [InlineData(false, "requirements-assessment-invalid")]
    [InlineData(true, "requirements-assessment-unavailable")]
    public async Task Enforced_assessment_fails_closed_for_invalid_or_timed_out_results(
        bool timesOut,
        string expectedEvent)
    {
        var backend = new LocalMarkdownTrackerBackend(new FakeIdentity(), clock);
        var config = new TrackerConfig
        {
            Backend = "local-markdown",
            DefaultPickFrom = "Todo",
            SourcePath = Path.Combine(directory, ".wrighty.json"),
            LocalMarkdown = new LocalMarkdownBackendConfig(),
            Worker = new WorkerConfig
            {
                RequirementsAssessment = new WorkerRequirementsAssessmentConfig
                {
                    Mode = "enforced"
                },
                UseWorkerQueue = false
            },
            LeaseMinutes = 60
        };
        await backend.InitializeAsync(config, false, CancellationToken.None);
        await backend.CreateAsync(config, new CreateWorkItemOperation(
            new CreateWorkItemRequest(
                "Assessment protocol", "Create a result with an observable verification.",
                "Todo", "P1", AutomaticExecutionAllowed: true, AgentPolicy: "claude"),
            false), CancellationToken.None);
        var runner = new InvalidAssessmentRunner(timesOut);
        var events = new List<WorkerEvent>();
        var worker = new WorkerService(
            new TrackerService(new TrackerBackendRegistry([backend])),
            runner,
            new CurrentWorkspace(),
            [new ClaudeAgentAdapter()],
            clock: () => clock.UtcNow);

        var summary = await worker.RunAsync(
            config,
            new WorkerOptions(null, true, null, WorkspaceMode.Current,
                new Dictionary<string, string>(), null, TimeSpan.FromMinutes(10),
                FencedAction.Kill, null, "agent", false, false),
            directory,
            value =>
            {
                events.Add(value);
                return Task.CompletedTask;
            },
            CancellationToken.None);

        Assert.Equal(1, summary.NeedsAttention);
        Assert.Equal(1, runner.Calls);
        Assert.Empty(runner.Environment!);
        Assert.Single(events, value => value.Type == expectedEvent);
        Assert.DoesNotContain(events, value => value.Type == "started");
    }

    [Fact]
    public async Task Enforced_assessment_without_a_session_restores_and_releases_the_item()
    {
        var backend = new LocalMarkdownTrackerBackend(new FakeIdentity(), clock);
        var config = new TrackerConfig
        {
            Backend = "local-markdown",
            DefaultPickFrom = "Todo",
            DefaultPickTo = "In Progress",
            SourcePath = Path.Combine(directory, ".wrighty.json"),
            LocalMarkdown = new LocalMarkdownBackendConfig(),
            Worker = new WorkerConfig
            {
                RequirementsAssessment = new WorkerRequirementsAssessmentConfig
                {
                    Mode = "enforced"
                },
                UseWorkerQueue = false
            },
            LeaseMinutes = 60
        };
        await backend.InitializeAsync(config, false, CancellationToken.None);
        var created = await backend.CreateAsync(config, new CreateWorkItemOperation(
            new CreateWorkItemRequest(
                "Provider failed before session creation",
                "Create result.txt containing OK and verify its bytes.",
                "Todo", "P1", AutomaticExecutionAllowed: true, AgentPolicy: "codex"),
            false), CancellationToken.None);
        var events = new List<WorkerEvent>();
        var worker = new WorkerService(
            new TrackerService(new TrackerBackendRegistry([backend])),
            new AssessmentFailureWithoutSessionRunner(),
            new CurrentWorkspace(),
            [new CodexAgentAdapter()],
            clock: () => clock.UtcNow);

        var summary = await worker.RunAsync(
            config,
            new WorkerOptions(null, true, null, WorkspaceMode.Current,
                new Dictionary<string, string>(), null, TimeSpan.FromMinutes(10),
                FencedAction.Kill, null, "agent", false, false),
            directory,
            value =>
            {
                events.Add(value);
                return Task.CompletedTask;
            },
            CancellationToken.None);

        Assert.Equal(1, summary.Failed);
        Assert.Equal("Todo", (await backend.GetAsync(
            config, created.Id, CancellationToken.None))!.Status);
        Assert.Equal(ClaimOwnershipState.Unclaimed,
            (await backend.GetClaimOwnershipAsync(
                config, created.Id, CancellationToken.None)).State);
        Assert.Null((await backend.GetAgentSessionAsync(
            config, created.Id, CancellationToken.None))?.SessionId);
        Assert.Single(events, value => value.Type == "requirements-assessment-unavailable");
        Assert.DoesNotContain(events, value => value.Type == "started");
        Assert.DoesNotContain(events, value => value.Type == "needs-attention");
    }

    [Fact]
    public async Task Dry_run_reports_claimed_item_and_continues_to_next_claimable_item()
    {
        var backend = new LocalMarkdownTrackerBackend(new FakeIdentity(), clock);
        var config = new TrackerConfig
        {
            Backend = "local-markdown",
            DefaultPickFrom = "Todo",
            Worker = new WorkerConfig { RequirementsAssessment = new WorkerRequirementsAssessmentConfig { Mode = "inline" }, UseWorkerQueue = false },
            SourcePath = Path.Combine(directory, ".wrighty.json"),
            LocalMarkdown = new LocalMarkdownBackendConfig(),
            LeaseMinutes = 60
        };
        await backend.InitializeAsync(config, false, CancellationToken.None);
        var claimed = await backend.CreateAsync(config, new CreateWorkItemOperation(
            new CreateWorkItemRequest("Claimed item", "Body", "Todo", "P1",
                AutomaticExecutionAllowed: true, AgentPolicy: "claude"), false), CancellationToken.None);
        await backend.CreateAsync(config, new CreateWorkItemOperation(
            new CreateWorkItemRequest("Available item", "Body", "Todo", "P2",
                AutomaticExecutionAllowed: true, AgentPolicy: "claude"), false), CancellationToken.None);
        await backend.TryClaimAsync(config, claimed.Id,
            new AgentExecutionContext(null, null, AgentContextSource.None,
                ClaimantKind: ClaimantKind.Human, ClaimantId: "web:test"),
            CancellationToken.None);
        var events = new List<WorkerEvent>();
        var worker = new WorkerService(
            new TrackerService(new TrackerBackendRegistry([backend])),
            new FailIfRunRunner(),
            new CurrentWorkspace(),
            [new ClaudeAgentAdapter()],
            clock: () => clock.UtcNow);

        var summary = await worker.RunAsync(config,
            new WorkerOptions(null, true, null, WorkspaceMode.Current,
                new Dictionary<string, string>(), null, TimeSpan.FromMinutes(10),
                FencedAction.Kill, null, "agent", true, false),
            directory, value =>
            {
                events.Add(value);
                return Task.CompletedTask;
            }, CancellationToken.None);

        Assert.Equal(new WorkerRunSummary(1), summary);
        var skipped = Assert.Single(events, value => value.Type == "skipped-claimed");
        Assert.Equal("local:1", skipped.ItemId);
        Assert.Equal(clock.UtcNow.AddMinutes(60), skipped.ClaimExpiresAt);
        var runnable = Assert.Single(events, value => value.Type == "dry-run");
        Assert.Equal("local:2", runnable.ItemId);
        Assert.DoesNotContain(events, value => value.Type == "no-item");
    }

    [Fact]
    public async Task Dry_run_with_only_claimed_eligible_work_emits_no_item()
    {
        var backend = new LocalMarkdownTrackerBackend(new FakeIdentity(), clock);
        var config = new TrackerConfig
        {
            Backend = "local-markdown",
            DefaultPickFrom = "Todo",
            Worker = new WorkerConfig { RequirementsAssessment = new WorkerRequirementsAssessmentConfig { Mode = "inline" }, UseWorkerQueue = false },
            SourcePath = Path.Combine(directory, ".wrighty.json"),
            LocalMarkdown = new LocalMarkdownBackendConfig(),
            LeaseMinutes = 60
        };
        await backend.InitializeAsync(config, false, CancellationToken.None);
        var created = await backend.CreateAsync(config, new CreateWorkItemOperation(
            new CreateWorkItemRequest("Claimed item", "Body", "Todo", "P1",
                AutomaticExecutionAllowed: true, AgentPolicy: "claude"), false), CancellationToken.None);
        await backend.TryClaimAsync(config, created.Id,
            new AgentExecutionContext(null, null, AgentContextSource.None,
                ClaimantKind: ClaimantKind.Human, ClaimantId: "web:test"),
            CancellationToken.None);
        var events = new List<WorkerEvent>();
        var worker = new WorkerService(
            new TrackerService(new TrackerBackendRegistry([backend])),
            new FailIfRunRunner(),
            new CurrentWorkspace(),
            [new ClaudeAgentAdapter()],
            clock: () => clock.UtcNow);

        var summary = await worker.RunAsync(config,
            new WorkerOptions(null, true, null, WorkspaceMode.Current,
                new Dictionary<string, string>(), null, TimeSpan.FromMinutes(10),
                FencedAction.Kill, null, "agent", true, false),
            directory, value =>
            {
                events.Add(value);
                return Task.CompletedTask;
            }, CancellationToken.None);

        Assert.Equal(new WorkerRunSummary(0), summary);
        Assert.Contains(events, value => value.Type == "skipped-claimed");
        Assert.Contains(events, value => value.Type == "no-item");
        Assert.DoesNotContain(events, value => value.Type == "dry-run");
    }

    [Fact]
    public async Task Run_outcome_and_structured_failure_are_recorded_and_survive_release()
    {
        var backend = new LocalMarkdownTrackerBackend(new FakeIdentity(), clock);
        var config = WorkerConfig();
        await backend.InitializeAsync(config, false, CancellationToken.None);
        var created = await backend.CreateAsync(config, new CreateWorkItemOperation(
            new CreateWorkItemRequest("Automate me", "Body", "Todo", "P1",
                AutomaticExecutionAllowed: true, AgentPolicy: "claude"), false), CancellationToken.None);
        var context = new AgentExecutionContext("claude", null, AgentContextSource.ExplicitOption,
            ClaimantKind: ClaimantKind.Agent, ClaimantId: "agent:worker:test");
        var claim = await backend.TryClaimAsync(config, created.Id, context, CancellationToken.None);
        var handle = new ClaimHandle(context, claim.ClaimToken);
        await backend.RenewClaimAsync(config, created.Id, handle,
            "/tmp/wrighty-tree", "session-42", CancellationToken.None);

        var endedAt = clock.UtcNow.AddMinutes(5);
        var failure = new AgentFailure(
            AgentFailureKind.UsageExhausted,
            "usage_limit_reached",
            endedAt.AddHours(2),
            null,
            true,
            AgentFailureConfidence.Authoritative,
            "Usage limit reached.");
        await backend.RecordRunOutcomeAsync(
            config, created.Id, RunOutcome.Failed, "The run stopped.", endedAt, failure,
            CancellationToken.None);

        var session = await backend.GetAgentSessionAsync(config, created.Id, CancellationToken.None);
        Assert.Equal(RunOutcome.Failed, session!.Outcome);
        Assert.Equal("The run stopped.", session.FinalMessage);
        Assert.Equal(endedAt, session.EndedAt);
        Assert.Equal(failure, session.Failure);

        // Releasing the claim preserves the session record, including the captured outcome.
        await backend.ReleaseAsync(config, created.Id, handle, false, DispatchStateOnRelease.Clear, CancellationToken.None);
        var afterRelease = await backend.GetAgentSessionAsync(
            config, created.Id, CancellationToken.None);
        Assert.Equal(RunOutcome.Failed, afterRelease!.Outcome);
        Assert.Equal("The run stopped.", afterRelease.FinalMessage);
        Assert.Equal(failure, afterRelease.Failure);
        Assert.Equal("session-42", afterRelease.SessionId);
    }

    [Fact]
    public async Task A_run_report_is_stored_even_though_the_local_backend_publishes_nothing()
    {
        // Local Markdown has no comment surface, so publishing is a no-op there. If storing were
        // tied to publishing, every local run would compute the agent's report and discard it —
        // the decisions, requested input and remaining work would exist only in the vendor's own
        // transcript, which Wrighty does not keep.
        var backend = new LocalMarkdownTrackerBackend(new FakeIdentity(), clock);
        var config = WorkerConfig();
        await backend.InitializeAsync(config, false, CancellationToken.None);
        var created = await backend.CreateAsync(config, new CreateWorkItemOperation(
            new CreateWorkItemRequest("Automate me", "Body", "Todo", "P1",
                AutomaticExecutionAllowed: true, AgentPolicy: "claude"), false), CancellationToken.None);
        var agentContext = new AgentExecutionContext("claude", null, AgentContextSource.ExplicitOption,
            ClaimantKind: ClaimantKind.Agent, ClaimantId: "agent:worker:test");
        var claim = await backend.TryClaimAsync(config, created.Id, agentContext, CancellationToken.None);
        var handle = new ClaimHandle(agentContext, claim.ClaimToken);
        await backend.RenewClaimAsync(config, created.Id, handle,
            "/tmp/wrighty-tree", "session-42", CancellationToken.None);

        var report = RunReportRenderer.Build(
            new RunIdentity(created.Id, "session-42", "claude"),
            RunReportDisposition.NeedsAttention,
            AgentOutcome.Succeeded, clock.UtcNow,
            new AgentReportContent("Did some of it.", RequestedInput: ["Which cap applies?"]));
        await backend.RecordRunReportAsync(config, created.Id, report, CancellationToken.None);

        var session = await backend.GetAgentSessionAsync(config, created.Id, CancellationToken.None);

        Assert.Equal("Did some of it.", session!.LastReport?.Summary);
        Assert.Equal(["Which cap applies?"], session.LastReport!.RequestedInput);
        Assert.Equal(RunReportDisposition.NeedsAttention, session.LastReport.ObservedDisposition);

        // And it survives the claim renewals a run performs while it is still going.
        await backend.RenewClaimAsync(config, created.Id, handle,
            "/tmp/wrighty-tree", "session-42", CancellationToken.None);
        var afterRenewal = await backend.GetAgentSessionAsync(
            config, created.Id, CancellationToken.None);
        Assert.Equal("Did some of it.", afterRenewal!.LastReport?.Summary);
    }

    [Fact]
    public async Task A_worker_event_quotes_the_agents_words_without_its_report_block()
    {
        // The block's content reaches an operator as structured fields on every surface that renders
        // a run, so an event repeating it says the same thing twice — and an event message is
        // truncated for a terminal, so a block cut mid-JSON never closes its own fence. Seen in the
        // GitHub report walkthrough, where a needs-attention line printed half a JSON object.
        var backend = new LocalMarkdownTrackerBackend(new FakeIdentity(), clock);
        var config = WorkerConfig();
        await backend.InitializeAsync(config, false, CancellationToken.None);
        var created = await backend.CreateAsync(config, new CreateWorkItemOperation(
            new CreateWorkItemRequest("Automate me", "Body", "Todo", "P1",
                AutomaticExecutionAllowed: true, AgentPolicy: "claude"), false),
            CancellationToken.None);

        var events = new List<WorkerEvent>();
        var worker = new WorkerService(
            new TrackerService(new TrackerBackendRegistry([backend])),
            new ReportingRunner(),
            new CurrentWorkspace(),
            [new ClaudeAgentAdapter()],
            clock: () => clock.UtcNow);

        await worker.RunItemAsync(
            config,
            new WorkerOptions(
                "claude", true, null, WorkspaceMode.Current, new Dictionary<string, string>(),
                null, TimeSpan.FromMinutes(10), FencedAction.Kill, null, "agent", false, false),
            directory, created.Id, WorkerItemIntent.Fresh, null,
            value =>
            {
                events.Add(value);
                return Task.CompletedTask;
            },
            CancellationToken.None);

        var attention = Assert.Single(events, value => value.Type == "needs-attention");
        Assert.Equal("I need one decision before finishing.", attention.Message);

        // The durable record is stripped on the way in, not left whole for readers to strip later.
        // Storing the block meant a message bounded mid-JSON could keep an opening fence with
        // nothing closing it, which no reader could then remove. Nothing is lost: the structured
        // report is parsed from the complete response and stored beside this.
        var session = await backend.GetAgentSessionAsync(config, created.Id, CancellationToken.None);
        Assert.DoesNotContain("wrighty-report", session!.FinalMessage!, StringComparison.Ordinal);
        Assert.Equal("I need one decision before finishing.", session.FinalMessage);
        Assert.Equal("Did the work.", session.LastReport?.Summary);
    }

    [Fact]
    public async Task A_long_agent_message_is_bounded_without_leaving_a_partial_report_block()
    {
        // The live failure: an agent long-winded enough that the durable cap landed inside its
        // report block. The block lost its closing fence, so no reader could strip it, and half a
        // JSON object was shown to an operator as the agent's closing words.
        var backend = new LocalMarkdownTrackerBackend(new FakeIdentity(), clock);
        var config = WorkerConfig();
        await backend.InitializeAsync(config, false, CancellationToken.None);
        var created = await backend.CreateAsync(config, new CreateWorkItemOperation(
            new CreateWorkItemRequest("Automate me", "Body", "Todo", "P1",
                AutomaticExecutionAllowed: true, AgentPolicy: "claude"), false),
            CancellationToken.None);

        // 52 + 1,900 characters of prose: under the cap on its own, over it once the block is
        // appended. Bounding before stripping would cut a few dozen characters into the JSON.
        var session = await RunVerboseAgentAsync(backend, config, created.Id, 1_900);
        var stored = session.FinalMessage!;

        // Not a trace of the block, opening fence included — the thing that could not be stripped
        // once a bound had removed its terminator.
        Assert.DoesNotContain("wrighty-report", stored, StringComparison.Ordinal);
        Assert.DoesNotContain("```", stored, StringComparison.Ordinal);
        Assert.DoesNotContain("\"summary\"", stored, StringComparison.Ordinal);

        // Removing the block first also means nothing had to be cut: the prose fits, so an operator
        // reads the agent's closing words in full rather than a bounded version of them.
        Assert.StartsWith(VerboseReportingRunner.Opening, stored, StringComparison.Ordinal);
        Assert.DoesNotContain("(truncated)", stored, StringComparison.Ordinal);

        // The report is parsed from the complete response, so none of this costs it anything.
        Assert.Equal("Did the work.", session.LastReport?.Summary);
        Assert.Equal("Which cap applies?", Assert.Single(session.LastReport!.RequestedInput!));
    }

    [Fact]
    public async Task Prose_that_exceeds_the_cap_on_its_own_is_bounded_and_says_so()
    {
        // The other half: once the block is gone the prose can still be too long, and a message
        // that merely stops is indistinguishable from an agent that stopped.
        var backend = new LocalMarkdownTrackerBackend(new FakeIdentity(), clock);
        var config = WorkerConfig();
        await backend.InitializeAsync(config, false, CancellationToken.None);
        var created = await backend.CreateAsync(config, new CreateWorkItemOperation(
            new CreateWorkItemRequest("Automate me", "Body", "Todo", "P1",
                AutomaticExecutionAllowed: true, AgentPolicy: "claude"), false),
            CancellationToken.None);

        var session = await RunVerboseAgentAsync(backend, config, created.Id, 2_500);
        var stored = session.FinalMessage!;

        Assert.EndsWith("… (truncated)", stored, StringComparison.Ordinal);
        Assert.DoesNotContain("wrighty-report", stored, StringComparison.Ordinal);
        // Bounded to the cap plus the marker, not merely "shorter than the input".
        Assert.True(stored.Length < 2_100, $"stored length was {stored.Length}");
        Assert.Equal("Did the work.", session.LastReport?.Summary);
    }

    private async Task<AgentSessionRecord> RunVerboseAgentAsync(
        LocalMarkdownTrackerBackend backend, TrackerConfig config, WorkItemId id, int proseFiller)
    {
        var worker = new WorkerService(
            new TrackerService(new TrackerBackendRegistry([backend])),
            new VerboseReportingRunner(proseFiller),
            new CurrentWorkspace(),
            [new ClaudeAgentAdapter()],
            clock: () => clock.UtcNow);

        await worker.RunItemAsync(
            config,
            new WorkerOptions(
                "claude", true, null, WorkspaceMode.Current, new Dictionary<string, string>(),
                null, TimeSpan.FromMinutes(10), FencedAction.Kill, null, "agent", false, false),
            directory, id, WorkerItemIntent.Fresh, null,
            _ => Task.CompletedTask,
            CancellationToken.None);

        return (await backend.GetAgentSessionAsync(config, id, CancellationToken.None))!;
    }

    [Fact]
    public async Task No_suggested_action_rewrites_the_description_and_queues_it_unattended()
    {
        // These two are individually fine and fatal together. Rewriting the description supersedes
        // the approved context the paused session holds; a continuous worker refuses to resume
        // across a change nobody named the item to approve. Suggesting both in one command queues a
        // run that cannot start — and the operator only finds out when the refusal appears.
        //
        // Either half alone works: a named run carries the operator's judgement, and an additive
        // clarification needs no judgement to carry.
        var backend = new LocalMarkdownTrackerBackend(new FakeIdentity(), clock);
        var config = WorkerConfig();
        await backend.InitializeAsync(config, false, CancellationToken.None);
        var created = await backend.CreateAsync(config, new CreateWorkItemOperation(
            new CreateWorkItemRequest("Automate me", "Body", "Todo", "P1",
                AutomaticExecutionAllowed: true, AgentPolicy: "claude"), false),
            CancellationToken.None);

        var events = new List<WorkerEvent>();
        var worker = new WorkerService(
            new TrackerService(new TrackerBackendRegistry([backend])),
            new ReportingRunner(),
            new CurrentWorkspace(),
            [new ClaudeAgentAdapter()],
            clock: () => clock.UtcNow);

        await worker.RunItemAsync(
            config,
            new WorkerOptions(
                "claude", true, null, WorkspaceMode.Current, new Dictionary<string, string>(),
                null, TimeSpan.FromMinutes(10), FencedAction.Kill, null, "agent", false, false),
            directory, created.Id, WorkerItemIntent.Fresh, null,
            value =>
            {
                events.Add(value);
                return Task.CompletedTask;
            },
            CancellationToken.None);

        var attention = Assert.Single(events, value => value.Type == "needs-attention");
        var commands = attention.OperatorActions!.SelectMany(action => action.Commands).ToList();
        Assert.NotEmpty(commands);
        Assert.DoesNotContain(commands, command =>
            command.Contains("--body-file", StringComparison.Ordinal) &&
            command.Contains("--requeue", StringComparison.Ordinal));

        // And the operator is still told how to get the session running again, so removing the
        // broken suggestion cannot have left a dead end.
        Assert.Contains(commands, command =>
            command.Contains("wrighty worker --item", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Recorded_session_context_survives_the_claim_renewals_a_launch_performs()
    {
        // The launch records the context between the pre-spawn check and the spawn, and the worker
        // renews the claim repeatedly for as long as the run lasts. A renewal rebuilds the session
        // record, so a carry-forward that was missed here would quietly discard the only thing a
        // later resume can compare against — and the loss would only surface as an unexplained
        // refusal much later.
        var backend = new LocalMarkdownTrackerBackend(new FakeIdentity(), clock);
        var config = WorkerConfig();
        await backend.InitializeAsync(config, false, CancellationToken.None);
        var created = await backend.CreateAsync(config, new CreateWorkItemOperation(
            new CreateWorkItemRequest("Automate me", "Body", "Todo", "P1",
                AutomaticExecutionAllowed: true, AgentPolicy: "claude"), false), CancellationToken.None);
        var agentContext = new AgentExecutionContext("claude", null, AgentContextSource.ExplicitOption,
            ClaimantKind: ClaimantKind.Agent, ClaimantId: "agent:worker:test");
        var claim = await backend.TryClaimAsync(config, created.Id, agentContext, CancellationToken.None);
        var handle = new ClaimHandle(agentContext, claim.ClaimToken);
        await backend.RenewClaimAsync(config, created.Id, handle,
            "/tmp/wrighty-tree", "session-42", CancellationToken.None);

        var capturedAt = clock.UtcNow;
        var supplied = new SessionContextMetadata(
            new ContextManifest(
                1, "sha256:supplied", "sha256:title", "sha256:body",
                [new ContextManifestEntry("c1", "sha256:c1", capturedAt)],
                capturedAt),
            BaseApprovedAt: capturedAt,
            ApprovalSource: ContextApprovalSource.BackendLocal,
            CapturedAt: capturedAt);
        await backend.RecordSessionContextAsync(
            config, created.Id, supplied, CancellationToken.None);

        Assert.Equal("sha256:supplied",
            (await backend.GetAgentSessionAsync(config, created.Id, CancellationToken.None))!
                .Context?.SuppliedDigest);

        await backend.RenewClaimAsync(config, created.Id, handle,
            "/tmp/wrighty-tree", "session-42", CancellationToken.None);
        var afterRenewal = await backend.GetAgentSessionAsync(
            config, created.Id, CancellationToken.None);

        Assert.Equal("sha256:supplied", afterRenewal!.Context?.SuppliedDigest);
        Assert.Equal("c1", Assert.Single(afterRenewal.Context!.Manifest!.Included).CommentId);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Release_preserves_or_clears_scheduled_dispatch_with_worker_state(
        bool preserveDispatchState)
    {
        var backend = new LocalMarkdownTrackerBackend(new FakeIdentity(), clock);
        var config = WorkerConfig();
        await backend.InitializeAsync(config, false, CancellationToken.None);
        var created = await backend.CreateAsync(config, new CreateWorkItemOperation(
            new CreateWorkItemRequest(
                "Scheduled release",
                "Body",
                config.DefaultPickTo,
                "P1",
                AutomaticExecutionAllowed: true,
                AgentPolicy: "claude"),
            false), CancellationToken.None);
        var context = new AgentExecutionContext(
            "claude",
            "session-42",
            AgentContextSource.ExplicitOption,
            ClaimantKind: ClaimantKind.Agent,
            ClaimantId: "agent:worker:test");
        var claim = await backend.TryClaimAsync(
            config, created.Id, context, CancellationToken.None);
        var handle = new ClaimHandle(context, claim.ClaimToken);
        await backend.RenewClaimAsync(
            config, created.Id, handle, directory, "session-42", CancellationToken.None);
        await backend.RecordPendingDispatchAsync(
            config,
            created.Id,
            Dispatch(created.Id),
            CancellationToken.None);
        await backend.UpdateAsync(
            config,
            created.Id,
            new UpdateWorkItemOperation(
                new WorkItemPatch(
                    OptionalValue<string>.Unspecified,
                    OptionalValue<string>.Unspecified,
                    OptionalValue<string>.Unspecified,
                    OptionalValue<string?>.Unspecified,
                    DispatchState: OptionalValue<string?>.From(
                        DispatchStates.RetryScheduled)),
                false,
                ClaimHandle: handle),
            CancellationToken.None);

        if (preserveDispatchState)
        {
            await backend.ReleaseAsync(config, created.Id, handle, false, DispatchStateOnRelease.Preserve,
                CancellationToken.None);
        }
        else
        {
            await backend.ReleaseAsync(
                config, created.Id, handle, false, DispatchStateOnRelease.Clear,
                CancellationToken.None);
        }

        var item = await backend.GetAsync(config, created.Id, CancellationToken.None);
        var session = await backend.GetAgentSessionAsync(
            config, created.Id, CancellationToken.None);
        Assert.Equal(
            preserveDispatchState ? DispatchStates.RetryScheduled : null,
            item?.DispatchState);
        Assert.Equal(
            preserveDispatchState ? DispatchStates.RetryScheduled : null,
            session?.Dispatch?.State);
    }

    [Fact]
    public async Task Requeue_overrides_scheduled_retry_and_clears_deferred_dispatch()
    {
        var backend = new LocalMarkdownTrackerBackend(new FakeIdentity(), clock);
        var config = WorkerConfig();
        await backend.InitializeAsync(config, false, CancellationToken.None);
        var created = await backend.CreateAsync(config, new CreateWorkItemOperation(
            new CreateWorkItemRequest(
                "Retry now",
                "Body",
                config.DefaultPickTo,
                "P1",
                AutomaticExecutionAllowed: true,
                AgentPolicy: "claude"),
            false), CancellationToken.None);
        var context = new AgentExecutionContext(
            "claude",
            "session-42",
            AgentContextSource.ExplicitOption,
            ClaimantKind: ClaimantKind.Agent,
            ClaimantId: "agent:worker:test");
        var claim = await backend.TryClaimAsync(
            config, created.Id, context, CancellationToken.None);
        var handle = new ClaimHandle(context, claim.ClaimToken);
        await backend.RenewClaimAsync(
            config, created.Id, handle, directory, "session-42", CancellationToken.None);
        await backend.RecordPendingDispatchAsync(
            config,
            created.Id,
            Dispatch(created.Id),
            CancellationToken.None);
        await backend.UpdateAsync(
            config,
            created.Id,
            new UpdateWorkItemOperation(
                new WorkItemPatch(
                    OptionalValue<string>.Unspecified,
                    OptionalValue<string>.Unspecified,
                    OptionalValue<string>.Unspecified,
                    OptionalValue<string?>.Unspecified,
                    DispatchState: OptionalValue<string?>.From(
                        DispatchStates.RetryScheduled)),
                false,
                ClaimHandle: handle),
            CancellationToken.None);

        await backend.RequeueAsync(
            config, created.Id, handle, CancellationToken.None);

        var item = await backend.GetAsync(config, created.Id, CancellationToken.None);
        var session = await backend.GetAgentSessionAsync(
            config, created.Id, CancellationToken.None);
        var ownership = await backend.GetClaimOwnershipAsync(
            config, created.Id, CancellationToken.None);
        Assert.Equal(DispatchStates.Queued, item?.DispatchState);
        Assert.Null(session?.Dispatch);
        Assert.Equal(ClaimOwnershipState.Unclaimed, ownership.State);
    }

    [Fact]
    public async Task Corrupt_local_dispatch_fails_closed()
    {
        var backend = new LocalMarkdownTrackerBackend(new FakeIdentity(), clock);
        var config = WorkerConfig();
        await backend.InitializeAsync(config, false, CancellationToken.None);
        var created = await backend.CreateAsync(config, new CreateWorkItemOperation(
            new CreateWorkItemRequest("Retain address", "Body", "Todo", "P1",
                AutomaticExecutionAllowed: true, AgentPolicy: "claude"), false), CancellationToken.None);
        var context = new AgentExecutionContext("claude", null, AgentContextSource.ExplicitOption,
            ClaimantKind: ClaimantKind.Agent, ClaimantId: "agent:worker:test");
        var claim = await backend.TryClaimAsync(config, created.Id, context, CancellationToken.None);
        var handle = new ClaimHandle(context, claim.ClaimToken);
        await backend.RenewClaimAsync(
            config, created.Id, handle, directory, "session-42", CancellationToken.None);
        await backend.RecordPendingDispatchAsync(
            config,
            created.Id,
            new PendingDispatch(
                created.Id.Value,
                DispatchStates.RetryScheduled,
                "Usage limit reached.",
                "claude",
                "session-42",
                null,
                clock.UtcNow.AddHours(1),
                1,
                5,
                AgentFailureConfidence.Authoritative,
                clock.UtcNow),
            CancellationToken.None);
        var runtimePath = Path.Combine(
            directory, ".wrighty", ".wrighty-runtime-v1.json");
        var json = await File.ReadAllTextAsync(runtimePath);
        await File.WriteAllTextAsync(
            runtimePath,
            json.Replace(
                "\"failureConfidence\": \"authoritative\"",
                "\"failureConfidence\": \"not-a-confidence\"",
                StringComparison.Ordinal));

        var exception = await Assert.ThrowsAsync<TrackerException>(
            () => backend.GetAgentSessionAsync(
                config, created.Id, CancellationToken.None));

        Assert.Equal("LOCAL_STORE_INVALID", exception.Code);
        Assert.Contains(".wrighty-runtime-v1.json", exception.Message);
    }

    [Fact]
    public async Task Usage_failure_schedules_retry_releases_claim_and_waits_until_due()
    {
        var backend = new LocalMarkdownTrackerBackend(new FakeIdentity(), clock);
        var config = WorkerConfig() with
        {
            Worker = new WorkerConfig
            {
                RequirementsAssessment = new WorkerRequirementsAssessmentConfig { Mode = "inline" },
                UseWorkerQueue = false,
                UsageFailure = new WorkerUsageFailureConfig
                {
                    InitialRetryMinutes = 30,
                    MaxAttempts = 5,
                    ResetGraceMinutes = 2
                }
            }
        };
        await backend.InitializeAsync(config, false, CancellationToken.None);
        var created = await backend.CreateAsync(config, new CreateWorkItemOperation(
            new CreateWorkItemRequest("Capacity wait", "Body", "Todo", "P1",
                AutomaticExecutionAllowed: true, AgentPolicy: "claude"), false), CancellationToken.None);
        var events = new List<WorkerEvent>();
        var worker = new WorkerService(
            new TrackerService(new TrackerBackendRegistry([backend])),
            new UsageFailureRunner(clock.UtcNow.AddHours(2)),
            new CurrentWorkspace(),
            [new ClaudeAgentAdapter()],
            clock: () => clock.UtcNow);

        var summary = await worker.RunAsync(
            config,
            Options(),
            directory,
            value =>
            {
                events.Add(value);
                return Task.CompletedTask;
            },
            CancellationToken.None);

        Assert.Equal(new WorkerRunSummary(1), summary);
        var scheduled = Assert.Single(events, value => value.Type == "retry-scheduled");
        Assert.Equal(1, scheduled.Dispatch?.Attempt);
        Assert.Equal(DispatchStates.RetryScheduled, scheduled.Dispatch?.State);
        Assert.Equal(ClaimOwnershipState.Unclaimed,
            (await backend.GetClaimOwnershipAsync(config, created.Id, CancellationToken.None)).State);
        Assert.Equal(
            DispatchStates.RetryScheduled,
            (await backend.GetAsync(config, created.Id, CancellationToken.None))?.DispatchState);
        var session = await backend.GetAgentSessionAsync(
            config, created.Id, CancellationToken.None);
        Assert.Equal(1, session?.Dispatch?.Attempt);
        Assert.Equal(scheduled.Dispatch?.NotBefore, session?.Dispatch?.NotBefore);

        var waitingEvents = new List<WorkerEvent>();
        var waitingWorker = new WorkerService(
            new TrackerService(new TrackerBackendRegistry([backend])),
            new FailIfRunRunner(),
            new CurrentWorkspace(),
            [new ClaudeAgentAdapter()],
            clock: () => clock.UtcNow);
        var waiting = await waitingWorker.RunAsync(
            config,
            Options(),
            directory,
            value =>
            {
                waitingEvents.Add(value);
                return Task.CompletedTask;
            },
            CancellationToken.None);

        Assert.Equal(new WorkerRunSummary(0), waiting);
        Assert.Contains(waitingEvents, value => value.Type == "no-item");
    }

    [Fact]
    public async Task Due_retry_reacquires_then_clears_schedule_and_increments_attempt_on_failure()
    {
        var backend = new LocalMarkdownTrackerBackend(new FakeIdentity(), clock);
        var config = WorkerConfig() with
        {
            Worker = new WorkerConfig
            {
                RequirementsAssessment = new WorkerRequirementsAssessmentConfig { Mode = "inline" },
                UseWorkerQueue = false,
                UsageFailure = new WorkerUsageFailureConfig
                {
                    InitialRetryMinutes = 1,
                    MaxAttempts = 3,
                    ResetGraceMinutes = 0
                }
            }
        };
        await backend.InitializeAsync(config, false, CancellationToken.None);
        var created = await backend.CreateAsync(config, new CreateWorkItemOperation(
            new CreateWorkItemRequest("Retry twice", "Body", "Todo", "P1",
                AutomaticExecutionAllowed: true, AgentPolicy: "claude"), false), CancellationToken.None);
        var tracker = new TrackerService(new TrackerBackendRegistry([backend]));
        var runner = new UsageFailureRunner();
        var worker = new WorkerService(
            tracker,
            runner,
            new CurrentWorkspace(),
            [new ClaudeAgentAdapter()],
            clock: () => clock.UtcNow);

        await worker.RunAsync(
            config, Options(), directory, _ => Task.CompletedTask, CancellationToken.None);
        var first = await backend.GetAgentSessionAsync(
            config, created.Id, CancellationToken.None);
        clock.UtcNow = first!.Dispatch!.NotBefore;
        var events = new List<WorkerEvent>();

        var second = await worker.RunAsync(
            config,
            Options(),
            directory,
            value =>
            {
                events.Add(value);
                return Task.CompletedTask;
            },
            CancellationToken.None);

        Assert.Equal(new WorkerRunSummary(1), second);
        Assert.Contains(events, value => value.Type == "retry-due");
        Assert.Contains(events, value => value.Type == "retry-started");
        var rescheduled = Assert.Single(events, value => value.Type == "retry-scheduled");
        Assert.Equal(2, rescheduled.Dispatch?.Attempt);
        var session = await backend.GetAgentSessionAsync(
            config, created.Id, CancellationToken.None);
        Assert.Equal(2, session?.Dispatch?.Attempt);
        Assert.Equal(DispatchStates.RetryScheduled,
            (await backend.GetAsync(config, created.Id, CancellationToken.None))?.DispatchState);
        Assert.Equal(2, runner.Calls);
    }

    [Fact]
    public async Task Due_retry_finished_by_agent_records_success_and_continuous_worker_keeps_running()
    {
        var backend = new LocalMarkdownTrackerBackend(new FakeIdentity(), clock);
        var config = WorkerConfig() with
        {
            Worker = new WorkerConfig
            {
                RequirementsAssessment = new WorkerRequirementsAssessmentConfig { Mode = "inline" },
                UseWorkerQueue = false,
                UsageFailure = new WorkerUsageFailureConfig
                {
                    InitialRetryMinutes = 1,
                    MaxAttempts = 3,
                    ResetGraceMinutes = 0
                }
            }
        };
        await backend.InitializeAsync(config, false, CancellationToken.None);
        var created = await backend.CreateAsync(config, new CreateWorkItemOperation(
            new CreateWorkItemRequest("Finish recovered retry", "Body", "Todo", "P1",
                AutomaticExecutionAllowed: true, AgentPolicy: "claude"), false), CancellationToken.None);
        var tracker = new TrackerService(new TrackerBackendRegistry([backend]));
        var schedulingWorker = new WorkerService(
            tracker,
            new UsageFailureRunner(),
            new CurrentWorkspace(),
            [new ClaudeAgentAdapter()],
            clock: () => clock.UtcNow);

        await schedulingWorker.RunAsync(
            config, Options(), directory, _ => Task.CompletedTask, CancellationToken.None);
        var scheduled = await backend.GetAgentSessionAsync(
            config, created.Id, CancellationToken.None);
        clock.UtcNow = scheduled!.Dispatch!.NotBefore;

        var runner = new FinishingRunner(async (environment, sessionId) =>
        {
            var claimant = new AgentExecutionContext(
                "claude",
                sessionId,
                AgentContextSource.ExplicitOption,
                ClaimantKind: ClaimantKind.Agent,
                ClaimantId: environment["WRIGHTY_CLAIMANT_ID"],
                ClaimToken: environment["WRIGHTY_CLAIM_TOKEN"]);
            await tracker.FinishAsync(
                config,
                created.Id,
                null,
                new ClaimHandle(claimant, environment["WRIGHTY_CLAIM_TOKEN"]),
                CancellationToken.None);
        });
        using var cancellation = new CancellationTokenSource();
        var events = new List<WorkerEvent>();
        var recoveryWorker = new WorkerService(
            tracker,
            runner,
            new CurrentWorkspace(),
            [new ClaudeAgentAdapter()],
            (delay, token) =>
            {
                if (delay <= TimeSpan.FromSeconds(8))
                {
                    cancellation.Cancel();
                    return Task.CompletedTask;
                }
                return Task.Delay(Timeout.InfiniteTimeSpan, token);
            },
            () => clock.UtcNow);

        var summary = await recoveryWorker.RunAsync(
            config,
            Options() with { Once = false },
            directory,
            value =>
            {
                events.Add(value);
                return Task.CompletedTask;
            },
            cancellation.Token);

        Assert.Equal(new WorkerRunSummary(1), summary);
        Assert.Contains(events, value => value.Type == "retry-due");
        Assert.Contains(events, value => value.Type == "retry-started");
        Assert.Contains(events, value => value.Type == "finished");
        Assert.Contains(events, value => value.Type == "idle");
        Assert.Equal(config.DefaultFinishTo,
            (await backend.GetAsync(config, created.Id, CancellationToken.None))?.Status);
        var session = await backend.GetAgentSessionAsync(
            config, created.Id, CancellationToken.None);
        Assert.Equal(RunOutcome.Succeeded, session?.Outcome);
        Assert.Null(session?.Dispatch);
        Assert.Equal(ClaimOwnershipState.Unclaimed,
            (await backend.GetClaimOwnershipAsync(
                config, created.Id, CancellationToken.None)).State);
    }

    [Fact]
    public async Task Crashed_due_retry_is_rediscovered_after_its_claim_expires()
    {
        var backend = new LocalMarkdownTrackerBackend(new FakeIdentity(), clock);
        var config = WorkerConfig() with
        {
            Worker = new WorkerConfig
            {
                RequirementsAssessment = new WorkerRequirementsAssessmentConfig { Mode = "inline" },
                UseWorkerQueue = false,
                UsageFailure = new WorkerUsageFailureConfig
                {
                    InitialRetryMinutes = 1,
                    MaxAttempts = 3,
                    ResetGraceMinutes = 0
                }
            }
        };
        await backend.InitializeAsync(config, false, CancellationToken.None);
        var created = await backend.CreateAsync(config, new CreateWorkItemOperation(
            new CreateWorkItemRequest("Recover interrupted retry", "Body", "Todo", "P1",
                AutomaticExecutionAllowed: true, AgentPolicy: "claude"), false), CancellationToken.None);
        var tracker = new TrackerService(new TrackerBackendRegistry([backend]));
        var schedulingWorker = new WorkerService(
            tracker,
            new UsageFailureRunner(),
            new CurrentWorkspace(),
            [new ClaudeAgentAdapter()],
            clock: () => clock.UtcNow);

        await schedulingWorker.RunAsync(
            config, Options(), directory, _ => Task.CompletedTask, CancellationToken.None);
        var scheduled = await backend.GetAgentSessionAsync(
            config, created.Id, CancellationToken.None);
        clock.UtcNow = scheduled!.Dispatch!.NotBefore;

        var context = new AgentExecutionContext(
            "claude",
            scheduled.SessionId,
            AgentContextSource.ExplicitOption,
            ClaimantKind: ClaimantKind.Agent,
            ClaimantId: "agent:worker:interrupted");
        var claim = await backend.TryClaimAsync(
            config, created.Id, context, CancellationToken.None);
        clock.UtcNow = claim.ExpiresAt;
        var interruptedItem = await backend.GetAsync(
            config, created.Id, CancellationToken.None);
        Assert.Equal(config.DefaultPickTo, interruptedItem?.Status);
        Assert.Equal(DispatchStates.RetryScheduled, interruptedItem?.DispatchState);
        var interruptedSession = await backend.GetAgentSessionAsync(
            config, created.Id, CancellationToken.None);
        Assert.NotNull(interruptedSession);
        Assert.True(interruptedSession.IsComplete);
        Assert.True(interruptedSession.FromCurrentInstallation);
        Assert.NotNull(interruptedSession.Dispatch);
        Assert.Equal(ClaimOwnershipState.Unclaimed,
            (await backend.GetClaimOwnershipAsync(
                config, created.Id, CancellationToken.None)).State);

        var events = new List<WorkerEvent>();
        var runner = new CapturingRejectedRunner();
        var recoveryWorker = new WorkerService(
            tracker,
            runner,
            new CurrentWorkspace(),
            [new ClaudeAgentAdapter()],
            clock: () => clock.UtcNow);

        var summary = await recoveryWorker.RunAsync(
            config,
            Options(),
            directory,
            value =>
            {
                events.Add(value);
                return Task.CompletedTask;
            },
            CancellationToken.None);

        Assert.True(
            summary == new WorkerRunSummary(1, 0, 1),
            string.Join(" | ", events.Select(value => $"{value.Type}: {value.Message}")));
        Assert.Contains(events, value => value.Type == "retry-due");
        Assert.Contains(events, value => value.Type == "retry-started");
        Assert.Single(runner.SessionIds);
        Assert.Null((await backend.GetAgentSessionAsync(
            config, created.Id, CancellationToken.None))?.Dispatch);
    }

    [Fact]
    public async Task Cancelled_due_retry_restores_portable_schedule_and_releases_claim()
    {
        var backend = new LocalMarkdownTrackerBackend(new FakeIdentity(), clock);
        var config = WorkerConfig() with
        {
            Worker = new WorkerConfig
            {
                RequirementsAssessment = new WorkerRequirementsAssessmentConfig { Mode = "inline" },
                UseWorkerQueue = false,
                UsageFailure = new WorkerUsageFailureConfig
                {
                    InitialRetryMinutes = 1,
                    MaxAttempts = 3,
                    ResetGraceMinutes = 0
                }
            }
        };
        await backend.InitializeAsync(config, false, CancellationToken.None);
        var created = await backend.CreateAsync(config, new CreateWorkItemOperation(
            new CreateWorkItemRequest("Cancel retry safely", "Body", "Todo", "P1",
                AutomaticExecutionAllowed: true, AgentPolicy: "claude"), false), CancellationToken.None);
        var tracker = new TrackerService(new TrackerBackendRegistry([backend]));
        var schedulingWorker = new WorkerService(
            tracker,
            new UsageFailureRunner(),
            new CurrentWorkspace(),
            [new ClaudeAgentAdapter()],
            clock: () => clock.UtcNow);

        await schedulingWorker.RunAsync(
            config, Options(), directory, _ => Task.CompletedTask, CancellationToken.None);
        var scheduled = await backend.GetAgentSessionAsync(
            config, created.Id, CancellationToken.None);
        clock.UtcNow = scheduled!.Dispatch!.NotBefore;

        using var cancellation = new CancellationTokenSource();
        var events = new List<WorkerEvent>();
        var recoveryWorker = new WorkerService(
            tracker,
            new CancellingRunner(cancellation),
            new CurrentWorkspace(),
            [new ClaudeAgentAdapter()],
            clock: () => clock.UtcNow);

        var summary = await recoveryWorker.RunAsync(
            config,
            Options(),
            directory,
            value =>
            {
                events.Add(value);
                return Task.CompletedTask;
            },
            cancellation.Token);

        Assert.Equal(new WorkerRunSummary(1, 0, 1), summary);
        Assert.Contains(events, value => value.Type == "retry-interrupted");
        Assert.Equal(DispatchStates.RetryScheduled,
            (await backend.GetAsync(config, created.Id, CancellationToken.None))?.DispatchState);
        Assert.NotNull((await backend.GetAgentSessionAsync(
            config, created.Id, CancellationToken.None))?.Dispatch);
        Assert.Equal(ClaimOwnershipState.Unclaimed,
            (await backend.GetClaimOwnershipAsync(
                config, created.Id, CancellationToken.None)).State);
    }

    [Fact]
    public async Task Attempt_limit_moves_usage_failure_to_needs_attention()
    {
        var backend = new LocalMarkdownTrackerBackend(new FakeIdentity(), clock);
        var config = WorkerConfig() with
        {
            Worker = new WorkerConfig
            {
                RequirementsAssessment = new WorkerRequirementsAssessmentConfig { Mode = "inline" },
                UseWorkerQueue = false,
                UsageFailure = new WorkerUsageFailureConfig
                {
                    InitialRetryMinutes = 1,
                    MaxAttempts = 1,
                    ResetGraceMinutes = 0
                }
            }
        };
        await backend.InitializeAsync(config, false, CancellationToken.None);
        var created = await backend.CreateAsync(config, new CreateWorkItemOperation(
            new CreateWorkItemRequest("Bound retries", "Body", "Todo", "P1",
                AutomaticExecutionAllowed: true, AgentPolicy: "claude"), false), CancellationToken.None);
        var worker = new WorkerService(
            new TrackerService(new TrackerBackendRegistry([backend])),
            new UsageFailureRunner(),
            new CurrentWorkspace(),
            [new ClaudeAgentAdapter()],
            clock: () => clock.UtcNow);

        await worker.RunAsync(
            config, Options(), directory, _ => Task.CompletedTask, CancellationToken.None);
        var first = await backend.GetAgentSessionAsync(
            config, created.Id, CancellationToken.None);
        clock.UtcNow = first!.Dispatch!.NotBefore;
        var events = new List<WorkerEvent>();
        var recordingBackend = new HandoverRecordingBackend(backend);
        var recoveryWorker = new WorkerService(
            new TrackerService(new TrackerBackendRegistry([recordingBackend])),
            new UsageFailureRunner(),
            new CurrentWorkspace(),
            [new ClaudeAgentAdapter()],
            clock: () => clock.UtcNow);

        var summary = await recoveryWorker.RunAsync(
            config,
            Options(),
            directory,
            value =>
            {
                events.Add(value);
                return Task.CompletedTask;
            },
            CancellationToken.None);

        Assert.Equal(new WorkerRunSummary(1, 1), summary);
        Assert.Contains(events, value =>
            value.Type == "needs-attention" &&
            value.Message!.Contains("after 1 attempts", StringComparison.Ordinal));
        Assert.Equal(DispatchStates.NeedsAttention,
            (await backend.GetAsync(config, created.Id, CancellationToken.None))?.DispatchState);
        Assert.Null((await backend.GetAgentSessionAsync(
            config, created.Id, CancellationToken.None))?.Dispatch);
        Assert.Equal(HandoverPhase.NeedsAttention, recordingBackend.LastHandover?.Phase);
        Assert.Contains(
            recordingBackend.LastHandover!.Actions,
            action => action.Commands.Contains(
                $"wrighty worker --item {created.Id.Value} --yes"));
    }

    private static WorkerOptions Options() =>
        new(
            "claude",
            true,
            null,
            WorkspaceMode.Current,
            new Dictionary<string, string>(),
            null,
            TimeSpan.FromMinutes(10),
            FencedAction.Kill,
            null,
            "agent",
            false,
            false);

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }

    private TrackerConfig WorkerConfig() => new()
    {
        Backend = "local-markdown",
        DefaultPickFrom = "Todo",
        Worker = new WorkerConfig { RequirementsAssessment = new WorkerRequirementsAssessmentConfig { Mode = "inline" }, UseWorkerQueue = false },
        SourcePath = Path.Combine(directory, ".wrighty.json"),
        LocalMarkdown = new LocalMarkdownBackendConfig(),
        LeaseMinutes = 60
    };

    private async Task<(
        LocalMarkdownTrackerBackend Backend,
        TrackerConfig Config,
        WorkItemId Id,
        ClaimHandle Handle)> CreatePausedItemAsync(string identity = "worker-test")
    {
        var backend = new LocalMarkdownTrackerBackend(new FakeIdentity(identity), clock);
        var config = WorkerConfig();
        await backend.InitializeAsync(config, false, CancellationToken.None);
        var created = await backend.CreateAsync(
            config,
            new CreateWorkItemOperation(
                new CreateWorkItemRequest(
                    "Queue paused session",
                    "Body",
                    "In Progress",
                    "P1",
                    AutomaticExecutionAllowed: true,
                    AgentPolicy: "codex"),
                false),
            CancellationToken.None);
        var context = new AgentExecutionContext(
            "codex",
            "paused-session",
            AgentContextSource.ExplicitOption,
            ClaimantKind: ClaimantKind.Agent,
            ClaimantId: "agent:worker:paused");
        var claim = await backend.TryClaimAsync(
            config,
            created.Id,
            context,
            CancellationToken.None);
        var handle = new ClaimHandle(context, claim.ClaimToken);
        await backend.RenewClaimAsync(
            config,
            created.Id,
            handle,
            directory,
            "paused-session",
            CancellationToken.None);
        await backend.UpdateAsync(
            config,
            created.Id,
            new UpdateWorkItemOperation(
                new WorkItemPatch(
                    OptionalValue<string>.Unspecified,
                    OptionalValue<string>.Unspecified,
                    OptionalValue<string>.Unspecified,
                    OptionalValue<string?>.Unspecified,
                    DispatchState: OptionalValue<string?>.From(
                        DispatchStates.NeedsAttention)),
                false,
                ClaimHandle: handle),
            CancellationToken.None);
        return (backend, config, created.Id, handle);
    }

    private sealed class FakeIdentity(string identity = "worker-test") : IInstallationIdentityProvider
    {
        public Task<string> GetInstallationIdAsync(CancellationToken cancellationToken) =>
            Task.FromResult(identity);
    }

    private sealed class FakeClock(DateTimeOffset value) : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = value;
    }

    private sealed class PolicyChangingAfterClaimBackend(ITrackerBackend inner)
        : ITrackerBackend
    {
        public string Name => inner.Name;

        public Highbyte.Wrighty.Addressing.IWorkItemAddressResolver AddressResolver =>
            inner.AddressResolver;

        public Task<BackendInitializationResult> InitializeAsync(
            TrackerConfig config,
            bool checkOnly,
            CancellationToken cancellationToken) =>
            inner.InitializeAsync(config, checkOnly, cancellationToken);

        public Task<IReadOnlyList<WorkItemSummary>> ListAsync(
            TrackerConfig config,
            ListWorkItemsRequest request,
            CancellationToken cancellationToken) =>
            inner.ListAsync(config, request, cancellationToken);

        public Task<WorkItemDetail?> GetAsync(
            TrackerConfig config,
            WorkItemId id,
            CancellationToken cancellationToken) =>
            inner.GetAsync(config, id, cancellationToken);

        public Task<CreateWorkItemResult> CreateAsync(
            TrackerConfig config,
            CreateWorkItemOperation operation,
            CancellationToken cancellationToken) =>
            inner.CreateAsync(config, operation, cancellationToken);

        public Task<UpdateWorkItemResult> UpdateAsync(
            TrackerConfig config,
            WorkItemId id,
            UpdateWorkItemOperation operation,
            CancellationToken cancellationToken) =>
            inner.UpdateAsync(config, id, operation, cancellationToken);

        public Task<ClaimResult> TryClaimAsync(
            TrackerConfig config,
            WorkItemId id,
            AgentExecutionContext agentContext,
            CancellationToken cancellationToken) =>
            TryClaimAsync(config, id, agentContext, cancellationToken, null);

        public async Task<ClaimResult> TryClaimAsync(
            TrackerConfig config,
            WorkItemId id,
            AgentExecutionContext agentContext,
            CancellationToken cancellationToken,
            string? expectedClaimToken)
        {
            var claim = await inner.TryClaimAsync(
                config,
                id,
                agentContext,
                cancellationToken,
                expectedClaimToken);
            await inner.UpdateAsync(
                config,
                id,
                new UpdateWorkItemOperation(
                    new WorkItemPatch(
                        OptionalValue<string>.Unspecified,
                        OptionalValue<string>.Unspecified,
                        OptionalValue<string>.Unspecified,
                        OptionalValue<string?>.Unspecified,
                        AutomaticExecutionAllowed: OptionalValue<bool>.From(false)),
                    false,
                    ClaimHandle: new ClaimHandle(
                        agentContext with { ClaimantId = claim.ClaimantId },
                        claim.ClaimToken)),
                cancellationToken);
            return claim;
        }

        public Task<ClaimResult> TakeoverAsync(
            TrackerConfig config,
            WorkItemId id,
            AgentExecutionContext claimantContext,
            string? currentClaimToken,
            CancellationToken cancellationToken) =>
            inner.TakeoverAsync(
                config, id, claimantContext, currentClaimToken, cancellationToken);

        public Task<ClaimResult> RenewClaimAsync(
            TrackerConfig config,
            WorkItemId id,
            ClaimHandle claimHandle,
            string? workspacePath,
            string? sessionId,
            string? branch,
            CancellationToken cancellationToken) =>
            inner.RenewClaimAsync(
                config,
                id,
                claimHandle,
                workspacePath,
                sessionId,
                branch,
                cancellationToken);

        public Task<ClaimOwnershipResult> GetClaimOwnershipAsync(
            TrackerConfig config,
            WorkItemId id,
            CancellationToken cancellationToken) =>
            inner.GetClaimOwnershipAsync(config, id, cancellationToken);

        public Task ReleaseAsync(
            TrackerConfig config,
            WorkItemId id,
            CancellationToken cancellationToken) =>
            inner.ReleaseAsync(config, id, cancellationToken);

        public Task ReleaseAsync(
            TrackerConfig config,
            WorkItemId id,
            ClaimHandle claimHandle,
            bool overrideClaimant,
            DispatchStateOnRelease dispatchState,
            CancellationToken cancellationToken) =>
            inner.ReleaseAsync(
                config, id, claimHandle, overrideClaimant, dispatchState, cancellationToken);

        public Task<ArchiveWorkItemResult> ArchiveAsync(
            TrackerConfig config,
            WorkItemId id,
            CancellationToken cancellationToken) =>
            inner.ArchiveAsync(config, id, cancellationToken);

        public Task<ArchiveWorkItemResult> ArchiveAsync(
            TrackerConfig config,
            WorkItemId id,
            ClaimHandle claimHandle,
            CancellationToken cancellationToken) =>
            inner.ArchiveAsync(config, id, claimHandle, cancellationToken);

        public Task<ArchiveWorkItemResult> UnarchiveAsync(
            TrackerConfig config,
            WorkItemId id,
            CancellationToken cancellationToken) =>
            inner.UnarchiveAsync(config, id, cancellationToken);
    }

    // Re-declares ITrackerBackend so the interface maps PresentDispatchAsync to this recording
    // implementation instead of the interface's default no-op inherited through the base class.
    private sealed class DispatchPresentationRecordingBackend(ITrackerBackend inner)
        : DelegatingTrackerBackend(inner), ITrackerBackend
    {
        public List<DispatchInfo> PresentedDispatches { get; } = [];

        public Task PresentDispatchAsync(
            TrackerConfig config,
            WorkItemId id,
            DispatchInfo dispatch,
            CancellationToken cancellationToken)
        {
            PresentedDispatches.Add(dispatch);
            return Task.CompletedTask;
        }
    }

    private sealed class ProjectedContextApprovalBackend(
        ITrackerBackend inner,
        IReadOnlyDictionary<WorkItemId, bool> approvals)
        : DelegatingTrackerBackend(inner)
    {
        public override async Task<WorkItemDetail?> GetAsync(
            TrackerConfig config,
            WorkItemId id,
            CancellationToken cancellationToken)
        {
            var detail = await base.GetAsync(config, id, cancellationToken);
            var projected = approvals.TryGetValue(id, out var approved)
                ? approved
                : (bool?)null;
            return detail is null
                ? null
                : detail with
                {
                    ContextApprovalFieldApproved = projected
                };
        }
    }

    private sealed class HandoverRecordingBackend(ITrackerBackend inner)
        : ITrackerBackend
    {
        public HandoverContent? LastHandover { get; private set; }

        public string Name => inner.Name;

        public Highbyte.Wrighty.Addressing.IWorkItemAddressResolver AddressResolver =>
            inner.AddressResolver;

        public Task<BackendInitializationResult> InitializeAsync(
            TrackerConfig config,
            bool checkOnly,
            CancellationToken cancellationToken) =>
            inner.InitializeAsync(config, checkOnly, cancellationToken);

        public Task<IReadOnlyList<WorkItemSummary>> ListAsync(
            TrackerConfig config,
            ListWorkItemsRequest request,
            CancellationToken cancellationToken) =>
            inner.ListAsync(config, request, cancellationToken);

        public Task<WorkItemDetail?> GetAsync(
            TrackerConfig config,
            WorkItemId id,
            CancellationToken cancellationToken) =>
            inner.GetAsync(config, id, cancellationToken);

        public Task<CreateWorkItemResult> CreateAsync(
            TrackerConfig config,
            CreateWorkItemOperation operation,
            CancellationToken cancellationToken) =>
            inner.CreateAsync(config, operation, cancellationToken);

        public Task<UpdateWorkItemResult> UpdateAsync(
            TrackerConfig config,
            WorkItemId id,
            UpdateWorkItemOperation operation,
            CancellationToken cancellationToken) =>
            inner.UpdateAsync(config, id, operation, cancellationToken);

        public Task<ClaimResult> TryClaimAsync(
            TrackerConfig config,
            WorkItemId id,
            AgentExecutionContext agentContext,
            CancellationToken cancellationToken) =>
            inner.TryClaimAsync(config, id, agentContext, cancellationToken);

        public Task<ClaimResult> TryClaimAsync(
            TrackerConfig config,
            WorkItemId id,
            AgentExecutionContext agentContext,
            CancellationToken cancellationToken,
            string? expectedClaimToken) =>
            inner.TryClaimAsync(
                config, id, agentContext, cancellationToken, expectedClaimToken);

        public Task<ClaimResult> TakeoverAsync(
            TrackerConfig config,
            WorkItemId id,
            AgentExecutionContext claimantContext,
            string? currentClaimToken,
            CancellationToken cancellationToken) =>
            inner.TakeoverAsync(
                config, id, claimantContext, currentClaimToken, cancellationToken);

        public Task<ClaimResult> RenewClaimAsync(
            TrackerConfig config,
            WorkItemId id,
            ClaimHandle claimHandle,
            string? workspacePath,
            string? sessionId,
            string? branch,
            CancellationToken cancellationToken) =>
            inner.RenewClaimAsync(
                config, id, claimHandle, workspacePath, sessionId, branch, cancellationToken);

        public Task<ClaimOwnershipResult> GetClaimOwnershipAsync(
            TrackerConfig config,
            WorkItemId id,
            CancellationToken cancellationToken) =>
            inner.GetClaimOwnershipAsync(config, id, cancellationToken);

        public Task<AgentSessionRecord?> GetAgentSessionAsync(
            TrackerConfig config,
            WorkItemId id,
            CancellationToken cancellationToken) =>
            inner.GetAgentSessionAsync(config, id, cancellationToken);

        public Task RecordRunOutcomeAsync(
            TrackerConfig config,
            WorkItemId id,
            RunOutcome outcome,
            string? finalMessage,
            DateTimeOffset endedAt,
            AgentFailure? failure,
            CancellationToken cancellationToken) =>
            inner.RecordRunOutcomeAsync(
                config, id, outcome, finalMessage, endedAt, failure, cancellationToken);

        public Task RecordPendingDispatchAsync(
            TrackerConfig config,
            WorkItemId id,
            PendingDispatch dispatch,
            CancellationToken cancellationToken) =>
            inner.RecordPendingDispatchAsync(config, id, dispatch, cancellationToken);

        public Task ClearPendingDispatchAsync(
            TrackerConfig config,
            WorkItemId id,
            CancellationToken cancellationToken) =>
            inner.ClearPendingDispatchAsync(config, id, cancellationToken);

        public Task PresentDispatchAsync(
            TrackerConfig config,
            WorkItemId id,
            DispatchInfo dispatch,
            CancellationToken cancellationToken) =>
            inner.PresentDispatchAsync(config, id, dispatch, cancellationToken);

        public Task PostHandoverAsync(
            TrackerConfig config,
            HandoverContent content,
            CancellationToken cancellationToken)
        {
            LastHandover = content;
            return Task.CompletedTask;
        }

        public Task ReleaseAsync(
            TrackerConfig config,
            WorkItemId id,
            CancellationToken cancellationToken) =>
            inner.ReleaseAsync(config, id, cancellationToken);

        public Task ReleaseAsync(
            TrackerConfig config,
            WorkItemId id,
            ClaimHandle claimHandle,
            bool overrideClaimant,
            DispatchStateOnRelease dispatchState,
            CancellationToken cancellationToken) =>
            inner.ReleaseAsync(
                config, id, claimHandle, overrideClaimant, dispatchState, cancellationToken);

        public Task<ArchiveWorkItemResult> ArchiveAsync(
            TrackerConfig config,
            WorkItemId id,
            CancellationToken cancellationToken) =>
            inner.ArchiveAsync(config, id, cancellationToken);

        public Task<ArchiveWorkItemResult> ArchiveAsync(
            TrackerConfig config,
            WorkItemId id,
            ClaimHandle claimHandle,
            CancellationToken cancellationToken) =>
            inner.ArchiveAsync(config, id, claimHandle, cancellationToken);

        public Task<ArchiveWorkItemResult> UnarchiveAsync(
            TrackerConfig config,
            WorkItemId id,
            CancellationToken cancellationToken) =>
            inner.UnarchiveAsync(config, id, cancellationToken);
    }

    private sealed class CurrentWorkspace : IWorkspaceManager
    {
        public Task<Workspace> PrepareAsync(
            WorkspaceRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new Workspace(Path.GetFullPath(request.RepositoryPath)));
    }

    private sealed class FailIfPrepareWorkspace : IWorkspaceManager
    {
        public Task<Workspace> PrepareAsync(
            WorkspaceRequest request, CancellationToken cancellationToken) =>
            throw new Xunit.Sdk.XunitException("No workspace should have been prepared.");
    }

    private sealed class RecordingWorkspaceMode : IWorkspaceManager
    {
        public WorkspaceMode? Mode { get; private set; }
        public string? RepositoryPath { get; private set; }

        public Task<Workspace> PrepareAsync(
            WorkspaceRequest request, CancellationToken cancellationToken)
        {
            Mode = request.Mode;
            var path = Path.GetFullPath(request.RepositoryPath);
            RepositoryPath = path;
            return Task.FromResult(new Workspace(path));
        }
    }

    private sealed class HungRunner : IAgentProcessRunner
    {
        public IReadOnlyDictionary<string, string>? Environment { get; private set; }

        public async Task<AgentRunResult> RunAsync(AgentInvocation invocation,
            IAgentAdapter adapter, TimeSpan timeout,
            IReadOnlyDictionary<string, string> grantEnvironment,
            Func<string, CancellationToken, Task>? sessionStarted,
            bool killOnCancellation, CancellationToken cancellationToken)
        {
            Environment = grantEnvironment;
            try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
            catch (OperationCanceledException) { }
            return new AgentRunResult(AgentOutcome.TimedOut, null, "budget exhausted");
        }
    }

    private sealed class SuccessfulRunner : IAgentProcessRunner
    {
        public Task<AgentRunResult> RunAsync(AgentInvocation invocation,
            IAgentAdapter adapter, TimeSpan timeout,
            IReadOnlyDictionary<string, string> grantEnvironment,
            Func<string, CancellationToken, Task>? sessionStarted,
            bool killOnCancellation, CancellationToken cancellationToken)
        {
            var marker = invocation.Arguments.ToList().IndexOf("--session-id");
            var sessionId = marker >= 0 ? invocation.Arguments[marker + 1] : "session-from-output";
            return Task.FromResult(new AgentRunResult(AgentOutcome.Succeeded, sessionId,
                "The item needs clarification."));
        }
    }

    private sealed class ReadyThenImplementationRunner : IAgentProcessRunner
    {
        public List<AgentInvocation> Invocations { get; } = [];
        public List<IReadOnlyDictionary<string, string>> Environments { get; } = [];

        public async Task<AgentRunResult> RunAsync(
            AgentInvocation invocation,
            IAgentAdapter adapter,
            TimeSpan timeout,
            IReadOnlyDictionary<string, string> grantEnvironment,
            Func<string, CancellationToken, Task>? sessionStarted,
            bool killOnCancellation,
            CancellationToken cancellationToken)
        {
            Invocations.Add(invocation);
            Environments.Add(new Dictionary<string, string>(grantEnvironment));
            var arguments = invocation.Arguments.ToList();
            var start = arguments.IndexOf("--session-id");
            var resume = arguments.IndexOf("--resume");
            var sessionId = start >= 0
                ? arguments[start + 1]
                : resume >= 0
                    ? arguments[resume + 1]
                    : "session-42";
            if (sessionStarted is not null)
                await sessionStarted(sessionId, cancellationToken);
            return Invocations.Count == 1
                ? new AgentRunResult(AgentOutcome.Succeeded, sessionId, """
                    ```wrighty-readiness
                    {
                      "schemaVersion": 1,
                      "verdict": "ready",
                      "reason": "The requested bytes and verification are explicit.",
                      "blockingQuestions": [],
                      "assumptions": []
                    }
                    ```
                    """)
                : new AgentRunResult(
                    AgentOutcome.Succeeded, sessionId, "Implementation stopped in test.");
        }
    }

    private sealed class NeedsClarificationRunner : IAgentProcessRunner
    {
        public int Calls { get; private set; }
        public IReadOnlyDictionary<string, string>? Environment { get; private set; }

        public async Task<AgentRunResult> RunAsync(
            AgentInvocation invocation,
            IAgentAdapter adapter,
            TimeSpan timeout,
            IReadOnlyDictionary<string, string> grantEnvironment,
            Func<string, CancellationToken, Task>? sessionStarted,
            bool killOnCancellation,
            CancellationToken cancellationToken)
        {
            Calls++;
            Environment = new Dictionary<string, string>(grantEnvironment);
            var arguments = invocation.Arguments.ToList();
            var marker = arguments.IndexOf("--session-id");
            var sessionId = marker >= 0 ? arguments[marker + 1] : "session-42";
            if (sessionStarted is not null)
                await sessionStarted(sessionId, cancellationToken);
            return new AgentRunResult(AgentOutcome.Succeeded, sessionId, """
                ```wrighty-readiness
                {
                  "schemaVersion": 1,
                  "verdict": "needs-clarification",
                  "reason": "The item permits two incompatible visible outcomes.",
                  "blockingQuestions": ["Should the output be BLUE or GREEN?"],
                  "assumptions": []
                }
                ```
                """);
        }
    }

    private sealed class InvalidAssessmentRunner(bool timesOut) : IAgentProcessRunner
    {
        public int Calls { get; private set; }
        public IReadOnlyDictionary<string, string>? Environment { get; private set; }

        public async Task<AgentRunResult> RunAsync(
            AgentInvocation invocation,
            IAgentAdapter adapter,
            TimeSpan timeout,
            IReadOnlyDictionary<string, string> grantEnvironment,
            Func<string, CancellationToken, Task>? sessionStarted,
            bool killOnCancellation,
            CancellationToken cancellationToken)
        {
            Calls++;
            Environment = new Dictionary<string, string>(grantEnvironment);
            var arguments = invocation.Arguments.ToList();
            var marker = arguments.IndexOf("--session-id");
            var sessionId = marker >= 0 ? arguments[marker + 1] : "session-42";
            if (sessionStarted is not null)
                await sessionStarted(sessionId, cancellationToken);
            return timesOut
                ? new AgentRunResult(AgentOutcome.TimedOut, sessionId, "budget exhausted")
                : new AgentRunResult(AgentOutcome.Succeeded, sessionId, "plain prose");
        }
    }

    private sealed class AssessmentFailureWithoutSessionRunner : IAgentProcessRunner
    {
        public Task<AgentRunResult> RunAsync(
            AgentInvocation invocation,
            IAgentAdapter adapter,
            TimeSpan timeout,
            IReadOnlyDictionary<string, string> grantEnvironment,
            Func<string, CancellationToken, Task>? sessionStarted,
            bool killOnCancellation,
            CancellationToken cancellationToken) =>
            Task.FromResult(new AgentRunResult(
                AgentOutcome.Failed,
                null,
                "The provider could not create its local session state."));
    }

    private sealed class UsageFailureRunner(DateTimeOffset? retryAt = null) : IAgentProcessRunner
    {
        public int Calls { get; private set; }

        public Task<AgentRunResult> RunAsync(
            AgentInvocation invocation,
            IAgentAdapter adapter,
            TimeSpan timeout,
            IReadOnlyDictionary<string, string> grantEnvironment,
            Func<string, CancellationToken, Task>? sessionStarted,
            bool killOnCancellation,
            CancellationToken cancellationToken)
        {
            Calls++;
            var marker = invocation.Arguments.ToList().IndexOf("--session-id");
            var resume = invocation.Arguments.ToList().IndexOf("--resume");
            var sessionId = marker >= 0
                ? invocation.Arguments[marker + 1]
                : resume >= 0
                    ? invocation.Arguments[resume + 1]
                    : "session-from-output";
            return Task.FromResult(new AgentRunResult(
                AgentOutcome.Failed,
                sessionId,
                "Usage limit reached.",
                1,
                new AgentFailure(
                    AgentFailureKind.UsageExhausted,
                    "usage_limit_reached",
                    retryAt,
                    null,
                    true,
                    AgentFailureConfidence.Authoritative,
                    "Usage limit reached.")));
        }
    }

    private PendingDispatch Dispatch(WorkItemId id) =>
        new(
            id.Value,
            DispatchStates.RetryScheduled,
            "Usage limit reached.",
            "claude",
            "session-42",
            null,
            clock.UtcNow.AddHours(1),
            1,
            5,
            AgentFailureConfidence.Authoritative,
            clock.UtcNow);

    private sealed class FinishingRunner(
        Func<IReadOnlyDictionary<string, string>, string, Task> finish,
        string finalMessage = "Completed the item.") : IAgentProcessRunner
    {
        public async Task<AgentRunResult> RunAsync(
            AgentInvocation invocation,
            IAgentAdapter adapter,
            TimeSpan timeout,
            IReadOnlyDictionary<string, string> grantEnvironment,
            Func<string, CancellationToken, Task>? sessionStarted,
            bool killOnCancellation,
            CancellationToken cancellationToken)
        {
            var marker = invocation.Arguments.ToList().IndexOf("--session-id");
            var sessionId = marker >= 0 ? invocation.Arguments[marker + 1] : "session-from-output";
            await finish(grantEnvironment, sessionId);
            return new AgentRunResult(
                AgentOutcome.Succeeded,
                sessionId,
                finalMessage);
        }
    }

    private sealed class TrackingWorktree(string path, bool cleanupSucceeds = true) : IWorkspaceManager
    {
        public int CleanupCalls { get; private set; }

        public Task<Workspace> PrepareAsync(
            WorkspaceRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new Workspace(path, true, "wrighty-worker/test"));

        public Task<bool> CleanupAsync(
            Workspace workspace,
            CancellationToken cancellationToken)
        {
            CleanupCalls++;
            return Task.FromResult(cleanupSucceeds);
        }
    }

    private sealed class CapturingRejectedRunner : IAgentProcessRunner
    {
        public List<string> SessionIds { get; } = [];

        public Task<AgentRunResult> RunAsync(
            AgentInvocation invocation,
            IAgentAdapter adapter,
            TimeSpan timeout,
            IReadOnlyDictionary<string, string> grantEnvironment,
            Func<string, CancellationToken, Task>? sessionStarted,
            bool killOnCancellation,
            CancellationToken cancellationToken)
        {
            var arguments = invocation.Arguments.ToList();
            var marker = arguments.IndexOf("--session-id");
            if (marker < 0)
                marker = arguments.IndexOf("--resume");
            var sessionId = arguments[marker + 1];
            SessionIds.Add(sessionId);
            return Task.FromResult(new AgentRunResult(
                AgentOutcome.Rejected,
                sessionId,
                "simulated rejection",
                1));
        }
    }

    private sealed class FailingRunner(AgentFailure failure, string? finalMessage = null)
        : IAgentProcessRunner
    {
        public Task<AgentRunResult> RunAsync(
            AgentInvocation invocation,
            IAgentAdapter adapter,
            TimeSpan timeout,
            IReadOnlyDictionary<string, string> grantEnvironment,
            Func<string, CancellationToken, Task>? sessionStarted,
            bool killOnCancellation,
            CancellationToken cancellationToken)
        {
            var marker = invocation.Arguments.ToList().IndexOf("--session-id");
            var sessionId = invocation.Arguments[marker + 1];
            return Task.FromResult(new AgentRunResult(
                AgentOutcome.Failed, sessionId,
                finalMessage ?? failure.SanitizedMessage, 1, failure));
        }
    }

    private sealed class StartFailureRunner : IAgentProcessRunner
    {
        public Task<AgentRunResult> RunAsync(
            AgentInvocation invocation,
            IAgentAdapter adapter,
            TimeSpan timeout,
            IReadOnlyDictionary<string, string> grantEnvironment,
            Func<string, CancellationToken, Task>? sessionStarted,
            bool killOnCancellation,
            CancellationToken cancellationToken) =>
            throw new TrackerException(
                "AGENT_START_FAILED",
                "The executable disappeared before spawn.",
                7);
    }

    private sealed class MutableRuntimeCatalog(params string[] installed) : IAgentRuntimeCatalog
    {
        public HashSet<string> Installed { get; } =
            new(installed, StringComparer.OrdinalIgnoreCase);

        public AgentRuntimeSnapshot Snapshot() => new(
            new[] { "claude", "codex", "copilot" }.Select(agent =>
                new AgentRuntime(
                    agent,
                    agent,
                    Supported: true,
                    Installed.Contains(agent)
                        ? AgentInstallationState.Installed
                        : AgentInstallationState.Missing,
                    Installed.Contains(agent) ? $"/tools/{agent}" : null)));
    }

    /// <summary>
    /// Reproduces a real vendor sequence: the agent drives the item to its finish state and
    /// releases its own claim, and only then does the session end badly — a usage limit reached
    /// right after `wrighty finish`.
    /// </summary>
    private sealed class FinishThenFailRunner(
        TrackerService tracker,
        TrackerConfig config,
        WorkItemId id,
        AgentFailure failure) : IAgentProcessRunner
    {
        public async Task<AgentRunResult> RunAsync(
            AgentInvocation invocation,
            IAgentAdapter adapter,
            TimeSpan timeout,
            IReadOnlyDictionary<string, string> grantEnvironment,
            Func<string, CancellationToken, Task>? sessionStarted,
            bool killOnCancellation,
            CancellationToken cancellationToken)
        {
            var marker = invocation.Arguments.ToList().IndexOf("--session-id");
            var sessionId = invocation.Arguments[marker + 1];
            var handle = new ClaimHandle(
                new AgentExecutionContext(
                    adapter.Agent,
                    sessionId,
                    AgentContextSource.ExplicitOption,
                    ClaimantKind: ClaimantKind.Agent,
                    ClaimantId: grantEnvironment["WRIGHTY_CLAIMANT_ID"]),
                grantEnvironment["WRIGHTY_CLAIM_TOKEN"]);
            await tracker.FinishAsync(config, id, null, handle, cancellationToken);
            return new AgentRunResult(
                AgentOutcome.Failed, sessionId, failure.SanitizedMessage, 1, failure);
        }
    }

    /// <summary>
    /// Simulates the torn half of a sandboxed agent's `wrighty finish`: the status move landed but
    /// the claim release was denied, and the vendor process then exited successfully.
    /// </summary>
    private sealed class StatusOnlyFinishRunner(
        TrackerService tracker,
        TrackerConfig config,
        WorkItemId id) : IAgentProcessRunner
    {
        public async Task<AgentRunResult> RunAsync(
            AgentInvocation invocation,
            IAgentAdapter adapter,
            TimeSpan timeout,
            IReadOnlyDictionary<string, string> grantEnvironment,
            Func<string, CancellationToken, Task>? sessionStarted,
            bool killOnCancellation,
            CancellationToken cancellationToken)
        {
            var marker = invocation.Arguments.ToList().IndexOf("--session-id");
            var sessionId = invocation.Arguments[marker + 1];
            var handle = new ClaimHandle(
                new AgentExecutionContext(
                    adapter.Agent,
                    sessionId,
                    AgentContextSource.ExplicitOption,
                    ClaimantKind: ClaimantKind.Agent,
                    ClaimantId: grantEnvironment["WRIGHTY_CLAIMANT_ID"]),
                grantEnvironment["WRIGHTY_CLAIM_TOKEN"]);
            await tracker.UpdateAsync(
                config, id, WorkItemPatch.StatusOnly(config.DefaultFinishTo),
                expectedRevision: null, handle, cancellationToken);
            return new AgentRunResult(
                AgentOutcome.Succeeded, sessionId, "Work is complete.", 0, null);
        }
    }

    private sealed class CancellingRunner(CancellationTokenSource cancellation)
        : IAgentProcessRunner
    {
        public Task<AgentRunResult> RunAsync(
            AgentInvocation invocation,
            IAgentAdapter adapter,
            TimeSpan timeout,
            IReadOnlyDictionary<string, string> grantEnvironment,
            Func<string, CancellationToken, Task>? sessionStarted,
            bool killOnCancellation,
            CancellationToken cancellationToken)
        {
            var resume = invocation.Arguments.ToList().IndexOf("--resume");
            var sessionId = invocation.Arguments[resume + 1];
            cancellation.Cancel();
            return Task.FromResult(new AgentRunResult(
                AgentOutcome.Rejected,
                sessionId,
                "simulated cancellation",
                -1));
        }
    }

    /// <summary>An agent that pauses for a decision and ends with the report block it was asked for.</summary>
    private sealed class ReportingRunner : IAgentProcessRunner
    {
        public Task<AgentRunResult> RunAsync(
            AgentInvocation invocation,
            IAgentAdapter adapter,
            TimeSpan timeout,
            IReadOnlyDictionary<string, string> grantEnvironment,
            Func<string, CancellationToken, Task>? sessionStarted,
            bool killOnCancellation,
            CancellationToken cancellationToken) =>
            Task.FromResult(new AgentRunResult(
                AgentOutcome.Succeeded,
                "session-reporting",
                "I need one decision before finishing.\n\n" +
                "```wrighty-report\n" +
                """{"summary":"Did the work.","requestedInput":["Which cap applies?"]}""" +
                "\n```"));
    }

    /// <summary>
    /// An agent verbose enough that its prose plus its report block cross the durable message cap,
    /// with the prose alone staying just under it.
    ///
    /// The sizing is the whole point and is easy to get wrong: pad the prose past the cap instead
    /// and the cut lands in the prose, the block never reaches the boundary, and the test passes
    /// whether or not anything is stripped. 52 + 1,900 characters of prose puts the 2,000th
    /// character a few dozen into the JSON body — an opening fence with no terminator, which is
    /// what was seen live.
    /// </summary>
    private sealed class VerboseReportingRunner(int proseFiller) : IAgentProcessRunner
    {
        public const string Opening = "The reissued context does not change the situation. ";

        public Task<AgentRunResult> RunAsync(
            AgentInvocation invocation,
            IAgentAdapter adapter,
            TimeSpan timeout,
            IReadOnlyDictionary<string, string> grantEnvironment,
            Func<string, CancellationToken, Task>? sessionStarted,
            bool killOnCancellation,
            CancellationToken cancellationToken) =>
            Task.FromResult(new AgentRunResult(
                AgentOutcome.Succeeded,
                "session-verbose",
                Opening + new string('x', proseFiller) + "\n\n" +
                "```wrighty-report\n" +
                """{"summary":"Did the work.","requestedInput":["Which cap applies?"]}""" +
                "\n```"));
    }

    private sealed class CapturingResumeRunner : IAgentProcessRunner
    {
        public AgentInvocation? Invocation { get; private set; }
        public IReadOnlyDictionary<string, string>? Environment { get; private set; }

        public Task<AgentRunResult> RunAsync(
            AgentInvocation invocation,
            IAgentAdapter adapter,
            TimeSpan timeout,
            IReadOnlyDictionary<string, string> grantEnvironment,
            Func<string, CancellationToken, Task>? sessionStarted,
            bool killOnCancellation,
            CancellationToken cancellationToken)
        {
            Invocation = invocation;
            Environment = grantEnvironment;
            var resume = invocation.Arguments.ToList().IndexOf("--resume");
            var sessionId = resume >= 0
                ? invocation.Arguments[resume + 1]
                : "session-original";
            return Task.FromResult(new AgentRunResult(
                AgentOutcome.Succeeded,
                sessionId,
                "Clarification still needed."));
        }
    }

    private sealed class ChangedSessionRunner(string? changedSessionId) : IAgentProcessRunner
    {
        public async Task<AgentRunResult> RunAsync(
            AgentInvocation invocation,
            IAgentAdapter adapter,
            TimeSpan timeout,
            IReadOnlyDictionary<string, string> grantEnvironment,
            Func<string, CancellationToken, Task>? sessionStarted,
            bool killOnCancellation,
            CancellationToken cancellationToken)
        {
            if (sessionStarted is not null && changedSessionId is not null)
                await sessionStarted(changedSessionId, cancellationToken);
            return new AgentRunResult(
                AgentOutcome.Succeeded,
                changedSessionId,
                "Unexpected replacement session.");
        }
    }

    private sealed class FailIfRunRunner : IAgentProcessRunner
    {
        public Task<AgentRunResult> RunAsync(
            AgentInvocation invocation,
            IAgentAdapter adapter,
            TimeSpan timeout,
            IReadOnlyDictionary<string, string> grantEnvironment,
            Func<string, CancellationToken, Task>? sessionStarted,
            bool killOnCancellation,
            CancellationToken cancellationToken) =>
            throw new Xunit.Sdk.XunitException("No vendor process should have been started.");
    }

    private sealed class RejectingWorkspaceLock : IWorkspaceExecutionLock
    {
        public List<string> Attempts { get; } = [];

        public ValueTask<IAsyncDisposable> AcquireAsync(
            string workspacePath,
            CancellationToken cancellationToken)
        {
            Attempts.Add(Path.GetFullPath(workspacePath));
            throw new TrackerException(
                "WORKSPACE_BUSY",
                "Simulated busy workspace.",
                7,
                new Dictionary<string, object?>
                {
                    ["workspacePath"] = Path.GetFullPath(workspacePath)
                });
        }
    }

    private sealed class RejectingSkillAvailability : IWorkerSkillAvailability
    {
        public List<(string Agent, string RepositoryPath)> Attempts { get; } = [];

        public void EnsureWorktreeReady(
            string agentType,
            string repositoryPath,
            string? existingWorkspacePath = null)
        {
            Attempts.Add((agentType, Path.GetFullPath(repositoryPath)));
            throw new TrackerException(
                "WORKER_SKILL_UNAVAILABLE",
                "Simulated missing worker skill.",
                9);
        }
    }
}
