using Highbyte.Wrighty.AgentContext;
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

public sealed class LocalWorkerStateTests : IDisposable
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
            SourcePath = Path.Combine(directory, ".wrighty.json"),
            LocalMarkdown = new LocalMarkdownBackendConfig(),
            LeaseMinutes = 60
        };
        await backend.InitializeAsync(config, false, CancellationToken.None);
        var created = await backend.CreateAsync(config, new CreateWorkItemOperation(
            new CreateWorkItemRequest("Automate me", "Body", "Todo", "P1",
                AutomationEligible: true, PreferredAgent: "claude"), false), CancellationToken.None);
        var detail = await backend.GetAsync(config, created.Id, CancellationToken.None);
        Assert.True(detail!.AutomationEligible);
        Assert.Equal("claude", detail.PreferredAgent);

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
            SourcePath = Path.Combine(directory, ".wrighty.json"),
            LocalMarkdown = new LocalMarkdownBackendConfig(),
            LeaseMinutes = 60
        };
        await backend.InitializeAsync(config, false, CancellationToken.None);
        await backend.CreateAsync(config, new CreateWorkItemOperation(
            new CreateWorkItemRequest("Hung item", "Body", "Todo", "P1",
                AutomationEligible: true, PreferredAgent: "claude"), false), CancellationToken.None);
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
            SourcePath = Path.Combine(directory, ".wrighty.json"),
            LocalMarkdown = new LocalMarkdownBackendConfig(),
            LeaseMinutes = 60
        };
        await backend.InitializeAsync(config, false, CancellationToken.None);
        await backend.CreateAsync(config, new CreateWorkItemOperation(
            new CreateWorkItemRequest("Retry item", "Body", "Todo", "P1",
                AutomationEligible: true, PreferredAgent: "claude"), false), CancellationToken.None);
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
    public async Task Busy_current_workspace_is_rejected_before_claim_or_vendor_spawn()
    {
        var backend = new LocalMarkdownTrackerBackend(new FakeIdentity(), clock);
        var config = new TrackerConfig
        {
            Backend = "local-markdown",
            SourcePath = Path.Combine(directory, ".wrighty.json"),
            LocalMarkdown = new LocalMarkdownBackendConfig(),
            LeaseMinutes = 60
        };
        await backend.InitializeAsync(config, false, CancellationToken.None);
        var created = await backend.CreateAsync(config, new CreateWorkItemOperation(
            new CreateWorkItemRequest("Do not claim me", "Body", "Todo", "P1",
                AutomationEligible: true, PreferredAgent: "claude"), false), CancellationToken.None);
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
            SourcePath = Path.Combine(directory, ".wrighty.json"),
            LocalMarkdown = new LocalMarkdownBackendConfig(),
            LeaseMinutes = 60
        };
        await backend.InitializeAsync(config, false, CancellationToken.None);
        await backend.CreateAsync(config, new CreateWorkItemOperation(
            new CreateWorkItemRequest("Isolated work", "Body", "Todo", "P1",
                AutomationEligible: true, PreferredAgent: "claude"), false), CancellationToken.None);
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
            SourcePath = Path.Combine(directory, ".wrighty.json"),
            LocalMarkdown = new LocalMarkdownBackendConfig(),
            LeaseMinutes = 60
        };
        await backend.InitializeAsync(config, false, CancellationToken.None);
        var created = await backend.CreateAsync(config, new CreateWorkItemOperation(
            new CreateWorkItemRequest("Needs a skill", "Body", "Todo", "P1",
                AutomationEligible: true, PreferredAgent: "claude"), false), CancellationToken.None);
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
            SourcePath = Path.Combine(directory, ".wrighty.json"),
            LocalMarkdown = new LocalMarkdownBackendConfig(),
            LeaseMinutes = 60
        };
        await backend.InitializeAsync(config, false, CancellationToken.None);
        await backend.CreateAsync(config, new CreateWorkItemOperation(
            new CreateWorkItemRequest("Shared work", "Body", "Todo", "P1",
                AutomationEligible: true, PreferredAgent: "claude"), false), CancellationToken.None);
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
            SourcePath = Path.Combine(directory, ".wrighty.json"),
            LocalMarkdown = new LocalMarkdownBackendConfig(),
            LeaseMinutes = 60
        };
        await backend.InitializeAsync(config, false, CancellationToken.None);
        await backend.CreateAsync(config, new CreateWorkItemOperation(
            new CreateWorkItemRequest("Needs clarification", "...", "Todo", "P1",
                AutomationEligible: true, PreferredAgent: "claude"), false), CancellationToken.None);
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
                Assert.Contains("Save and queue for worker", action.Description);
                Assert.Contains("Finish when complete", action.Description);
                Assert.Contains("Archive", action.Description);
            },
            action =>
            {
                Assert.Contains("continuous worker", action.Scenario);
                Assert.Equal(
                    ["wrighty edit local:1 --takeover --yes --body-file requirements.md --requeue"],
                    action.Commands);
                Assert.Contains("prioritizes it before fresh Todo work", action.Description);
            },
            action =>
            {
                Assert.Contains("Continue with Claude", action.Scenario);
                Assert.Equal(["wrighty worker --item local:1 --yes"], action.Commands);
                Assert.Contains("active or after it expires", action.Description);
            },
            action =>
            {
                Assert.Contains("CLI instead", action.Scenario);
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
            });
        var ownership = await backend.GetClaimOwnershipAsync(config, new WorkItemId("local:1"),
            CancellationToken.None);
        Assert.Equal(ClaimOwnershipState.OwnedByCurrent, ownership.State);
        Assert.Equal(directory, ownership.WorkspacePath);
        Assert.Equal(attention.SessionId, ownership.SessionId);
        Assert.Equal("In Progress", (await backend.GetAsync(config, new WorkItemId("local:1"),
            CancellationToken.None))!.Status);
        Assert.Equal(
            WorkerDispatchStates.NeedsAttention,
            (await backend.GetAsync(config, new WorkItemId("local:1"),
                CancellationToken.None))!.WorkerState);
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
                AutomationEligible: true,
                PreferredAgent: "codex"),
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
                    WorkerState: OptionalValue<string?>.From(
                        WorkerDispatchStates.NeedsAttention)),
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
            WorkerDispatchStates.Queued,
            (await backend.GetAsync(config, created.Id, CancellationToken.None))!.WorkerState);
        var session = await backend.GetAgentSessionAsync(
            config, created.Id, CancellationToken.None);
        Assert.Equal("codex", session!.AgentType);
        Assert.Equal("paused-session", session.SessionId);
        Assert.Equal(directory, session.WorkspacePath);
    }

    [Fact]
    public async Task Queue_paused_rejects_item_after_worker_state_changes()
    {
        var backend = new LocalMarkdownTrackerBackend(new FakeIdentity(), clock);
        var config = new TrackerConfig
        {
            Backend = "local-markdown",
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
                AutomationEligible: true,
                PreferredAgent: "codex"),
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
                    AutomationEligible: OptionalValue<bool>.From(false)),
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
            WorkerDispatchStates.NeedsAttention,
            (await ownerBackend.GetAsync(config, id, CancellationToken.None))!.WorkerState);
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
                    AutomationEligible: true,
                    PreferredAgent: "codex"),
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
                    WorkerState: OptionalValue<string?>.From(
                        WorkerDispatchStates.NeedsAttention)),
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
                AutomationEligible: true,
                PreferredAgent: "claude"),
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
            WorkerDispatchStates.Queued,
            (await backend.GetAsync(config, created.Id, CancellationToken.None))!.WorkerState);
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
            WorkerDispatchStates.NeedsAttention,
            (await backend.GetAsync(config, created.Id, CancellationToken.None))!.WorkerState);
    }

    [Fact]
    public async Task Fresh_run_reclaims_exact_expired_active_item_with_a_new_session()
    {
        var backend = new LocalMarkdownTrackerBackend(new FakeIdentity(), clock);
        var config = new TrackerConfig
        {
            Backend = "local-markdown",
            SourcePath = Path.Combine(directory, ".wrighty.json"),
            LocalMarkdown = new LocalMarkdownBackendConfig(),
            LeaseMinutes = 60
        };
        await backend.InitializeAsync(config, false, CancellationToken.None);
        var created = await backend.CreateAsync(config, new CreateWorkItemOperation(
            new CreateWorkItemRequest("Retry me", "Body", "Todo", "P1",
                AutomationEligible: true, PreferredAgent: "claude"), false), CancellationToken.None);
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
            SourcePath = Path.Combine(directory, ".wrighty.json"),
            LocalMarkdown = new LocalMarkdownBackendConfig(),
            LeaseMinutes = 60
        };
        await backend.InitializeAsync(config, false, CancellationToken.None);
        var created = await backend.CreateAsync(config, new CreateWorkItemOperation(
            new CreateWorkItemRequest("Continue me", "Clarified body", "In Progress", "P1",
                AutomationEligible: true, PreferredAgent: "claude"), false), CancellationToken.None);
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
            SourcePath = Path.Combine(directory, ".wrighty.json"),
            LocalMarkdown = new LocalMarkdownBackendConfig(),
            LeaseMinutes = 60
        };
        await backend.InitializeAsync(config, false, CancellationToken.None);
        var created = await backend.CreateAsync(config, new CreateWorkItemOperation(
            new CreateWorkItemRequest("Finish me", "Body", "Todo", "P1",
                AutomationEligible: true, PreferredAgent: "claude"), false), CancellationToken.None);
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
        Assert.DoesNotContain("WRIGHTY_CLAIMANT_ID", finished.ReviewCommand);
        Assert.DoesNotContain("WRIGHTY_CLAIM_TOKEN", finished.ReviewCommand);
        Assert.Equal(ClaimOwnershipState.Unclaimed,
            (await backend.GetClaimOwnershipAsync(config, created.Id, CancellationToken.None)).State);
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
            SourcePath = Path.Combine(directory, ".wrighty.json"),
            LocalMarkdown = new LocalMarkdownBackendConfig(),
            LeaseMinutes = 60,
            Worker = new WorkerConfig
            {
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
                AutomationEligible: true, PreferredAgent: "claude"), false), CancellationToken.None);
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
            SourcePath = Path.Combine(directory, ".wrighty.json"),
            LocalMarkdown = new LocalMarkdownBackendConfig(),
            LeaseMinutes = 60,
            Worker = new WorkerConfig
            {
                Completion = new WorkerCompletionConfig { Commit = "agent" }
            }
        };
        await backend.InitializeAsync(config, false, CancellationToken.None);
        var created = await backend.CreateAsync(config, new CreateWorkItemOperation(
            new CreateWorkItemRequest("Finish worktree", "Body", "Todo", "P1",
                AutomationEligible: true, PreferredAgent: "claude"), false), CancellationToken.None);
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
            SourcePath = Path.Combine(directory, ".wrighty.json"),
            LocalMarkdown = new LocalMarkdownBackendConfig(),
            LeaseMinutes = 60
        };
        await backend.InitializeAsync(config, false, CancellationToken.None);
        var created = await backend.CreateAsync(config, new CreateWorkItemOperation(
            new CreateWorkItemRequest("Clarified item", "Actionable body", "In Progress", "P1",
                AutomationEligible: true, PreferredAgent: "claude"), false), CancellationToken.None);
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
        Assert.Equal("claude", ownership.AgentType);
        Assert.Equal("session-original", ownership.SessionId);
    }

    [Fact]
    public async Task Busy_resume_workspace_is_rejected_before_human_claim_rotates_to_agent()
    {
        var backend = new LocalMarkdownTrackerBackend(new FakeIdentity(), clock);
        var config = new TrackerConfig
        {
            Backend = "local-markdown",
            SourcePath = Path.Combine(directory, ".wrighty.json"),
            LocalMarkdown = new LocalMarkdownBackendConfig(),
            LeaseMinutes = 60
        };
        await backend.InitializeAsync(config, false, CancellationToken.None);
        var created = await backend.CreateAsync(config, new CreateWorkItemOperation(
            new CreateWorkItemRequest("Clarified item", "Actionable body", "In Progress", "P1",
                AutomationEligible: true, PreferredAgent: "claude"), false), CancellationToken.None);
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
            SourcePath = Path.Combine(directory, ".wrighty.json"),
            LocalMarkdown = new LocalMarkdownBackendConfig(),
            LeaseMinutes = 60
        };
        await backend.InitializeAsync(config, false, CancellationToken.None);
        var created = await backend.CreateAsync(config, new CreateWorkItemOperation(
            new CreateWorkItemRequest("Claimed item", "Body", "Todo", "P1",
                AutomationEligible: true, PreferredAgent: "claude"), false), CancellationToken.None);
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
                PreferredAgent: "claude"),
            false), CancellationToken.None);
        await backend.CreateAsync(config, new CreateWorkItemOperation(
            new CreateWorkItemRequest("Missing agent", "Body", "Todo", "P3",
                AutomationEligible: true),
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
        Assert.Contains("3 active item(s)", noItem.Message);
        Assert.Contains("2 missing wrighty-auto=true", noItem.Message);
        Assert.Contains("2 missing a wrighty-agent item preference", noItem.Message);
        Assert.Contains("--agent > wrighty-agent > worker.defaultAgent", noItem.Message);
    }

    [Fact]
    public async Task Worker_preflight_reports_claimable_count_and_first_available_candidate_without_claiming()
    {
        var backend = new LocalMarkdownTrackerBackend(new FakeIdentity(), clock);
        var config = new TrackerConfig
        {
            Backend = "local-markdown",
            SourcePath = Path.Combine(directory, ".wrighty.json"),
            LocalMarkdown = new LocalMarkdownBackendConfig(),
            LeaseMinutes = 60
        };
        await backend.InitializeAsync(config, false, CancellationToken.None);
        var claimed = await backend.CreateAsync(config, new CreateWorkItemOperation(
            new CreateWorkItemRequest("Claimed first", "Body", "Todo", "P1",
                AutomationEligible: true, PreferredAgent: "claude"),
            false), CancellationToken.None);
        var available = await backend.CreateAsync(config, new CreateWorkItemOperation(
            new CreateWorkItemRequest("Available second", "Body", "Todo", "P2",
                AutomationEligible: true, PreferredAgent: "claude"),
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
                            AutomationEligible: true),
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
            "1 automation-enabled item needs an agent; set wrighty-agent, --agent, " +
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
            SourcePath = Path.Combine(directory, ".wrighty.json"),
            LocalMarkdown = new LocalMarkdownBackendConfig(),
            Worker = new WorkerConfig { DefaultAgent = "codex" },
            LeaseMinutes = 60
        };
        await backend.InitializeAsync(config, false, CancellationToken.None);
        await backend.CreateAsync(config, new CreateWorkItemOperation(
            new CreateWorkItemRequest("Pinned item", "Body", "Todo", "P1",
                AutomationEligible: true, PreferredAgent: "claude"),
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
    public async Task Dry_run_reports_claimed_item_and_continues_to_next_claimable_item()
    {
        var backend = new LocalMarkdownTrackerBackend(new FakeIdentity(), clock);
        var config = new TrackerConfig
        {
            Backend = "local-markdown",
            SourcePath = Path.Combine(directory, ".wrighty.json"),
            LocalMarkdown = new LocalMarkdownBackendConfig(),
            LeaseMinutes = 60
        };
        await backend.InitializeAsync(config, false, CancellationToken.None);
        var claimed = await backend.CreateAsync(config, new CreateWorkItemOperation(
            new CreateWorkItemRequest("Claimed item", "Body", "Todo", "P1",
                AutomationEligible: true, PreferredAgent: "claude"), false), CancellationToken.None);
        await backend.CreateAsync(config, new CreateWorkItemOperation(
            new CreateWorkItemRequest("Available item", "Body", "Todo", "P2",
                AutomationEligible: true, PreferredAgent: "claude"), false), CancellationToken.None);
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
            SourcePath = Path.Combine(directory, ".wrighty.json"),
            LocalMarkdown = new LocalMarkdownBackendConfig(),
            LeaseMinutes = 60
        };
        await backend.InitializeAsync(config, false, CancellationToken.None);
        var created = await backend.CreateAsync(config, new CreateWorkItemOperation(
            new CreateWorkItemRequest("Claimed item", "Body", "Todo", "P1",
                AutomationEligible: true, PreferredAgent: "claude"), false), CancellationToken.None);
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
                AutomationEligible: true, PreferredAgent: "claude"), false), CancellationToken.None);
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
        await backend.ReleaseAsync(config, created.Id, handle, false, CancellationToken.None);
        var afterRelease = await backend.GetAgentSessionAsync(
            config, created.Id, CancellationToken.None);
        Assert.Equal(RunOutcome.Failed, afterRelease!.Outcome);
        Assert.Equal("The run stopped.", afterRelease.FinalMessage);
        Assert.Equal(failure, afterRelease.Failure);
        Assert.Equal("session-42", afterRelease.SessionId);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Release_preserves_or_clears_scheduled_dispatch_with_worker_state(
        bool preserveWorkerState)
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
                AutomationEligible: true,
                PreferredAgent: "claude"),
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
        await backend.RecordDeferredDispatchAsync(
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
                    WorkerState: OptionalValue<string?>.From(
                        WorkerDispatchStates.RetryScheduled)),
                false,
                ClaimHandle: handle),
            CancellationToken.None);

        if (preserveWorkerState)
        {
            await backend.ReleasePreservingWorkerStateAsync(
                config, created.Id, handle, CancellationToken.None);
        }
        else
        {
            await backend.ReleaseAsync(
                config, created.Id, handle, false, CancellationToken.None);
        }

        var item = await backend.GetAsync(config, created.Id, CancellationToken.None);
        var session = await backend.GetAgentSessionAsync(
            config, created.Id, CancellationToken.None);
        Assert.Equal(
            preserveWorkerState ? WorkerDispatchStates.RetryScheduled : null,
            item?.WorkerState);
        Assert.Equal(
            preserveWorkerState ? WorkerDispatchStates.RetryScheduled : null,
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
                AutomationEligible: true,
                PreferredAgent: "claude"),
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
        await backend.RecordDeferredDispatchAsync(
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
                    WorkerState: OptionalValue<string?>.From(
                        WorkerDispatchStates.RetryScheduled)),
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
        Assert.Equal(WorkerDispatchStates.Queued, item?.WorkerState);
        Assert.Null(session?.Dispatch);
        Assert.Equal(ClaimOwnershipState.Unclaimed, ownership.State);
    }

    [Fact]
    public async Task Corrupt_local_dispatch_fails_closed_without_losing_session_address()
    {
        var backend = new LocalMarkdownTrackerBackend(new FakeIdentity(), clock);
        var config = WorkerConfig();
        await backend.InitializeAsync(config, false, CancellationToken.None);
        var created = await backend.CreateAsync(config, new CreateWorkItemOperation(
            new CreateWorkItemRequest("Retain address", "Body", "Todo", "P1",
                AutomationEligible: true, PreferredAgent: "claude"), false), CancellationToken.None);
        var context = new AgentExecutionContext("claude", null, AgentContextSource.ExplicitOption,
            ClaimantKind: ClaimantKind.Agent, ClaimantId: "agent:worker:test");
        var claim = await backend.TryClaimAsync(config, created.Id, context, CancellationToken.None);
        var handle = new ClaimHandle(context, claim.ClaimToken);
        await backend.RenewClaimAsync(
            config, created.Id, handle, directory, "session-42", CancellationToken.None);
        await backend.RecordDeferredDispatchAsync(
            config,
            created.Id,
            new DeferredDispatch(
                created.Id.Value,
                WorkerDispatchStates.RetryScheduled,
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
            directory, ".wrighty", ".runtime-state.json");
        var json = await File.ReadAllTextAsync(runtimePath);
        await File.WriteAllTextAsync(
            runtimePath,
            json.Replace(
                "\"failureConfidence\": \"authoritative\"",
                "\"failureConfidence\": \"not-a-confidence\"",
                StringComparison.Ordinal));

        var session = await backend.GetAgentSessionAsync(
            config, created.Id, CancellationToken.None);

        Assert.Equal("session-42", session?.SessionId);
        Assert.Equal(directory, session?.WorkspacePath);
        Assert.Null(session?.Dispatch);
    }

    [Fact]
    public async Task Usage_failure_schedules_retry_releases_claim_and_waits_until_due()
    {
        var backend = new LocalMarkdownTrackerBackend(new FakeIdentity(), clock);
        var config = WorkerConfig() with
        {
            Worker = new WorkerConfig
            {
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
                AutomationEligible: true, PreferredAgent: "claude"), false), CancellationToken.None);
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
        Assert.Equal(WorkerDispatchStates.RetryScheduled, scheduled.Dispatch?.State);
        Assert.Equal(ClaimOwnershipState.Unclaimed,
            (await backend.GetClaimOwnershipAsync(config, created.Id, CancellationToken.None)).State);
        Assert.Equal(
            WorkerDispatchStates.RetryScheduled,
            (await backend.GetAsync(config, created.Id, CancellationToken.None))?.WorkerState);
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
                AutomationEligible: true, PreferredAgent: "claude"), false), CancellationToken.None);
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
        Assert.Equal(WorkerDispatchStates.RetryScheduled,
            (await backend.GetAsync(config, created.Id, CancellationToken.None))?.WorkerState);
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
                AutomationEligible: true, PreferredAgent: "claude"), false), CancellationToken.None);
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
                AutomationEligible: true, PreferredAgent: "claude"), false), CancellationToken.None);
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
        Assert.Equal(WorkerDispatchStates.RetryScheduled, interruptedItem?.WorkerState);
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
                AutomationEligible: true, PreferredAgent: "claude"), false), CancellationToken.None);
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
        Assert.Equal(WorkerDispatchStates.RetryScheduled,
            (await backend.GetAsync(config, created.Id, CancellationToken.None))?.WorkerState);
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
                AutomationEligible: true, PreferredAgent: "claude"), false), CancellationToken.None);
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

        Assert.Equal(new WorkerRunSummary(1, 1), summary);
        Assert.Contains(events, value =>
            value.Type == "needs-attention" &&
            value.Message!.Contains("after 1 attempts", StringComparison.Ordinal));
        Assert.Equal(WorkerDispatchStates.NeedsAttention,
            (await backend.GetAsync(config, created.Id, CancellationToken.None))?.WorkerState);
        Assert.Null((await backend.GetAgentSessionAsync(
            config, created.Id, CancellationToken.None))?.Dispatch);
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
                    AutomationEligible: true,
                    PreferredAgent: "codex"),
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
                    WorkerState: OptionalValue<string?>.From(
                        WorkerDispatchStates.NeedsAttention)),
                false,
                ClaimHandle: handle),
            CancellationToken.None);
        return (backend, config, created.Id, handle);
    }

    private sealed class FakeIdentity(string identity = "worker-test") : IWorkerIdentityProvider
    {
        public Task<string> GetIdentityAsync(CancellationToken cancellationToken) =>
            Task.FromResult(identity);
    }

    private sealed class FakeClock(DateTimeOffset value) : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = value;
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

    private DeferredDispatch Dispatch(WorkItemId id) =>
        new(
            id.Value,
            WorkerDispatchStates.RetryScheduled,
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
        Func<IReadOnlyDictionary<string, string>, string, Task> finish) : IAgentProcessRunner
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
                "Completed the item.");
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
            var marker = invocation.Arguments.ToList().IndexOf("--session-id");
            var sessionId = invocation.Arguments[marker + 1];
            SessionIds.Add(sessionId);
            return Task.FromResult(new AgentRunResult(
                AgentOutcome.Rejected,
                sessionId,
                "simulated rejection",
                1));
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
        public List<(string AgentType, string RepositoryPath)> Attempts { get; } = [];

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
