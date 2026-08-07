using Highbyte.Wrighty.AgentContext;
using Highbyte.Wrighty.Backends;
using Highbyte.Wrighty.Claims;
using Highbyte.Wrighty.Configuration;
using Highbyte.Wrighty.Errors;
using Highbyte.Wrighty.Identity;
using Highbyte.Wrighty.LocalMarkdown;
using Highbyte.Wrighty.Models;
using Highbyte.Wrighty.Time;
using Highbyte.Wrighty.Workers;
using Highbyte.Wrighty;

namespace Highbyte.Wrighty.UnitTests.Workers;

/// <summary>
/// Plan 026 part d: the cross-agent handoff lifecycle over the Local Markdown backend — the
/// scheduling decision after a usage failure, the bounded recovery budget, and the target launch
/// that continues the retained workspace under a new vendor session.
/// </summary>
public sealed class CrossAgentHandoffTests : IDisposable
{
    private readonly string directory = Path.Combine(
        Path.GetTempPath(), $"wrighty-handoff-{Guid.NewGuid():N}");
    private readonly FakeClock clock = new(DateTimeOffset.Parse("2026-08-05T10:00:00Z"));

    public CrossAgentHandoffTests()
    {
        Directory.CreateDirectory(directory);
    }

    [Fact]
    public async Task Usage_failure_with_handoff_action_queues_the_first_available_fallback()
    {
        var (backend, config, id) = await CreateQueueableItemAsync(new WorkerUsageFailureConfig
        {
            Action = "handoff",
            Fallbacks = new Dictionary<string, IReadOnlyList<string>>(
                StringComparer.OrdinalIgnoreCase)
            { ["claude"] = ["codex", "copilot"] }
        });
        var events = new List<WorkerEvent>();
        var worker = Worker(backend, new UsageFailureRunner());

        var summary = await worker.RunAsync(
            config, Options(), directory, Collect(events), CancellationToken.None);

        Assert.Equal(new WorkerRunSummary(1), summary);
        var queued = Assert.Single(events, value => value.Type == "handoff-queued");
        Assert.Equal(DispatchStates.HandoffQueued, queued.Dispatch?.State);
        Assert.Equal("codex", queued.Dispatch?.Agent);
        Assert.Equal("claude", queued.Dispatch?.SessionAgent);
        Assert.Equal(
            DispatchStates.HandoffQueued,
            (await backend.GetAsync(config, id, CancellationToken.None))?.DispatchState);
        Assert.Equal(
            ClaimOwnershipState.Unclaimed,
            (await backend.GetClaimOwnershipAsync(config, id, CancellationToken.None)).State);
        var session = await backend.GetAgentSessionAsync(config, id, CancellationToken.None);
        Assert.Equal(DispatchStates.HandoffQueued, session?.Dispatch?.State);
        Assert.Equal("codex", session?.Dispatch?.Agent);
        // The policy field now names the target, so the board shows the new direction.
        Assert.Equal(
            "codex",
            (await backend.GetAsync(config, id, CancellationToken.None))!.AgentPolicy);
    }

    [Fact]
    public async Task Exhausted_retries_hand_off_when_the_operator_opted_in()
    {
        var (backend, config, id) = await CreateQueueableItemAsync(new WorkerUsageFailureConfig
        {
            Action = "retry",
            MaxAttempts = 1,
            InitialRetryMinutes = 30,
            AllowCrossAgentHandoff = true,
            Fallbacks = new Dictionary<string, IReadOnlyList<string>>(
                StringComparer.OrdinalIgnoreCase)
            { ["claude"] = ["codex"] }
        });
        var firstEvents = new List<WorkerEvent>();
        await Worker(backend, new UsageFailureRunner()).RunAsync(
            config, Options(), directory, Collect(firstEvents), CancellationToken.None);
        Assert.Single(firstEvents, value => value.Type == "retry-scheduled");

        clock.UtcNow = clock.UtcNow.AddHours(1);
        var secondEvents = new List<WorkerEvent>();
        await Worker(backend, new UsageFailureRunner()).RunAsync(
            config, Options(), directory, Collect(secondEvents), CancellationToken.None);

        var queued = Assert.Single(secondEvents, value => value.Type == "handoff-queued");
        Assert.Equal("codex", queued.Dispatch?.Agent);
        Assert.Equal(2, queued.Dispatch?.Attempt);
        _ = id;
    }

    [Fact]
    public async Task Exhausted_retries_without_the_opt_in_still_need_attention()
    {
        var (backend, config, _) = await CreateQueueableItemAsync(new WorkerUsageFailureConfig
        {
            Action = "retry",
            MaxAttempts = 1,
            InitialRetryMinutes = 30,
            Fallbacks = new Dictionary<string, IReadOnlyList<string>>(
                StringComparer.OrdinalIgnoreCase)
            { ["claude"] = ["codex"] }
        });
        await Worker(backend, new UsageFailureRunner()).RunAsync(
            config, Options(), directory, _ => Task.CompletedTask, CancellationToken.None);

        clock.UtcNow = clock.UtcNow.AddHours(1);
        var events = new List<WorkerEvent>();
        await Worker(backend, new UsageFailureRunner()).RunAsync(
            config, Options(), directory, Collect(events), CancellationToken.None);

        var attention = Assert.Single(events, value => value.Type == "needs-attention");
        Assert.Contains("Automatic retry stopped after 1 attempts", attention.Message);
        Assert.DoesNotContain(events, value => value.Type == "handoff-queued");
    }

    [Fact]
    public async Task Handoff_action_without_a_viable_target_needs_attention()
    {
        // Only claude is registered, so every configured fallback is unsupported here.
        var (backend, config, _) = await CreateQueueableItemAsync(new WorkerUsageFailureConfig
        {
            Action = "handoff",
            Fallbacks = new Dictionary<string, IReadOnlyList<string>>(
                StringComparer.OrdinalIgnoreCase)
            { ["claude"] = ["codex"] }
        });
        var events = new List<WorkerEvent>();
        var worker = new WorkerService(
            new TrackerService(new TrackerBackendRegistry([backend])),
            new UsageFailureRunner(),
            new CurrentWorkspace(),
            [new ClaudeAgentAdapter()],
            clock: () => clock.UtcNow);

        await worker.RunAsync(
            config, Options(), directory, Collect(events), CancellationToken.None);

        var attention = Assert.Single(events, value => value.Type == "needs-attention");
        Assert.Contains("no available target agent", attention.Message);
    }

    [Fact]
    public async Task Due_handoff_launches_the_target_with_the_packet_over_stdin()
    {
        var (backend, config, id) = await SeedQueuedHandoffAsync();
        var runner = new RecordingRunner();
        var events = new List<WorkerEvent>();
        var worker = Worker(backend, runner);

        var summary = await worker.RunAsync(
            config, Options(agent: null), directory, Collect(events), CancellationToken.None);

        Assert.Equal(1, summary.Processed);
        Assert.Single(events, value => value.Type == "handoff-due");
        var started = Assert.Single(events, value => value.Type == "handoff-started");
        Assert.Equal("codex", started.Agent);
        Assert.NotNull(runner.Invocation);
        Assert.Equal("codex", runner.Adapter?.Agent);
        var prompt = runner.Invocation!.StandardInput;
        Assert.NotNull(prompt);
        Assert.Contains("# Cross-agent handoff", prompt);
        Assert.Contains("The work item and the workspace are authoritative", prompt);
        Assert.Contains("claude", prompt);
        // The replaced source address is retained as bounded lineage in the runtime store.
        var runtime = await File.ReadAllTextAsync(
            Path.Combine(directory, ".wrighty", ".wrighty-runtime-v1.json"));
        Assert.Contains("priorSessions", runtime);
        Assert.Contains("handoff-session", runtime);
        _ = id;
    }

    [Fact]
    public async Task Queued_handoff_is_not_picked_before_it_is_due()
    {
        var (backend, config, _) = await SeedQueuedHandoffAsync(
            notBefore: clock.UtcNow.AddHours(1));
        var events = new List<WorkerEvent>();

        var summary = await Worker(backend, new FailIfRunRunner()).RunAsync(
            config, Options(agent: null), directory, Collect(events), CancellationToken.None);

        Assert.Equal(0, summary.Processed);
        Assert.DoesNotContain(events, value => value.Type == "handoff-started");
    }

    [Fact]
    public async Task Operator_handoff_starts_the_named_target_from_a_recorded_session()
    {
        var (backend, config, id) = await SeedRecordedSessionAsync();
        var runner = new RecordingRunner();
        var events = new List<WorkerEvent>();

        var summary = await Worker(backend, runner).RunItemAsync(
            config, Options(agent: "codex"), directory, id, WorkerItemIntent.Handoff,
            currentClaimToken: null, Collect(events), CancellationToken.None);

        Assert.Equal(1, summary.Processed);
        var started = Assert.Single(events, value => value.Type == "handoff-started");
        Assert.Equal("codex", started.Agent);
        Assert.Equal("codex", runner.Adapter?.Agent);
        Assert.Contains("# Cross-agent handoff", runner.Invocation!.StandardInput);
        Assert.Equal(
            "codex",
            (await backend.GetAsync(config, id, CancellationToken.None))!.AgentPolicy);
    }

    [Fact]
    public async Task Operator_handoff_selects_the_first_configured_fallback_when_unnamed()
    {
        var (backend, config, id) = await SeedRecordedSessionAsync();
        var runner = new RecordingRunner();

        await Worker(backend, runner).RunItemAsync(
            config, Options(agent: null), directory, id, WorkerItemIntent.Handoff,
            currentClaimToken: null, _ => Task.CompletedTask, CancellationToken.None);

        Assert.Equal("codex", runner.Adapter?.Agent);
    }

    [Fact]
    public async Task Operator_handoff_supersedes_the_ended_sessions_retained_claim()
    {
        // A needs-attention ending keeps its claim for the rest of the lease; the explicit
        // handoff must not make the operator wait it out.
        var (backend, config, id) = await SeedRecordedSessionAsync(releaseClaim: false);
        Assert.Equal(
            ClaimOwnershipState.OwnedByCurrent,
            (await backend.GetClaimOwnershipAsync(config, id, CancellationToken.None)).State);
        var runner = new RecordingRunner();
        var events = new List<WorkerEvent>();

        var summary = await Worker(backend, runner).RunItemAsync(
            config, Options(agent: "codex"), directory, id, WorkerItemIntent.Handoff,
            currentClaimToken: null, Collect(events), CancellationToken.None);

        Assert.Equal(1, summary.Processed);
        Assert.Equal("codex", runner.Adapter?.Agent);
        Assert.Single(events, value => value.Type == "handoff-started");
    }

    [Fact]
    public async Task Operator_handoff_rejects_the_recorded_agent_as_target()
    {
        var (backend, config, id) = await SeedRecordedSessionAsync();

        var exception = await Assert.ThrowsAsync<TrackerException>(() =>
            Worker(backend, new FailIfRunRunner()).RunItemAsync(
                config, Options(agent: "claude"), directory, id, WorkerItemIntent.Handoff,
                currentClaimToken: null, _ => Task.CompletedTask, CancellationToken.None));

        Assert.Equal("ARGUMENT_INVALID", exception.Code);
        Assert.Contains("--resume", exception.Message);
    }

    [Fact]
    public async Task Operator_handoff_without_a_recorded_session_is_refused()
    {
        var (backend, config, id) = await CreateQueueableItemAsync(new WorkerUsageFailureConfig());

        var exception = await Assert.ThrowsAsync<TrackerException>(() =>
            Worker(backend, new FailIfRunRunner()).RunItemAsync(
                config, Options(agent: "codex"), directory, id, WorkerItemIntent.Handoff,
                currentClaimToken: null, _ => Task.CompletedTask, CancellationToken.None));

        Assert.Equal("RESUME_ADDRESS_UNAVAILABLE", exception.Code);
        Assert.Contains("hand off", exception.Message);
    }

    [Fact]
    public async Task Editing_the_agent_policy_field_directs_a_handover_on_the_next_scan()
    {
        var (backend, config, id) = await SeedRecordedSessionAsync(agentPolicy: "codex");
        var runner = new RecordingRunner();
        var events = new List<WorkerEvent>();

        var summary = await Worker(backend, runner).RunAsync(
            config, Options(agent: null), directory, Collect(events), CancellationToken.None);

        Assert.Equal(1, summary.Processed);
        Assert.Single(events, value => value.Type == "handoff-directed");
        Assert.Single(events, value => value.Type == "handoff-started");
        Assert.Equal("codex", runner.Adapter?.Agent);
        Assert.Equal(
            "codex",
            (await backend.GetAsync(config, id, CancellationToken.None))!.AgentPolicy);
    }

    [Fact]
    public async Task A_completed_handover_does_not_direct_again()
    {
        var (backend, config, id) = await SeedRecordedSessionAsync(agentPolicy: "codex");
        await Worker(backend, new RecordingRunner()).RunAsync(
            config, Options(agent: null), directory, _ => Task.CompletedTask,
            CancellationToken.None);
        var session = await backend.GetAgentSessionAsync(config, id, CancellationToken.None);
        Assert.Equal("codex", session?.Agent);

        var summary = await Worker(backend, new FailIfRunRunner()).RunAsync(
            config, Options(agent: null), directory, _ => Task.CompletedTask,
            CancellationToken.None);

        Assert.Equal(0, summary.Processed);
    }

    [Fact]
    public async Task A_directed_handover_supersedes_a_needs_attention_items_retained_claim()
    {
        var (backend, config, id) = await SeedRecordedSessionAsync(
            agentPolicy: "codex", releaseClaim: false, dispatchState: DispatchStates.NeedsAttention);
        Assert.Equal(
            ClaimOwnershipState.OwnedByCurrent,
            (await backend.GetClaimOwnershipAsync(config, id, CancellationToken.None)).State);
        var runner = new RecordingRunner();
        var events = new List<WorkerEvent>();

        var summary = await Worker(backend, runner).RunAsync(
            config, Options(agent: null), directory, Collect(events), CancellationToken.None);

        Assert.Equal(1, summary.Processed);
        Assert.Equal("codex", runner.Adapter?.Agent);
        Assert.Single(events, value => value.Type == "handoff-started");
    }

    [Fact]
    public async Task A_needs_attention_item_without_a_direction_stays_put()
    {
        var (backend, config, _) = await SeedRecordedSessionAsync(
            releaseClaim: false, dispatchState: DispatchStates.NeedsAttention);

        var summary = await Worker(backend, new FailIfRunRunner()).RunAsync(
            config, Options(agent: null), directory, _ => Task.CompletedTask,
            CancellationToken.None);

        Assert.Equal(0, summary.Processed);
    }

    [Fact]
    public void Handover_comment_names_the_queued_handoff()
    {
        var dispatch = new DispatchInfo(
            DispatchStates.HandoffQueued, "Usage limit reached.", "claude", "codex",
            clock.UtcNow, 2, 7, clock.UtcNow, true);
        var content = new HandoverContent(
            new WorkItemId("local:7"), HandoverPhase.HandoffQueued, RunOutcome.Failed,
            null, "host", "/ws", "main", [], HandoverCommentMode.Full, dispatch);

        var rendered = HandoverRenderer.Render(content);

        Assert.Contains("cross-agent handoff queued", rendered);
        Assert.Contains("**Handoff:** to `codex`", rendered);
        Assert.Contains("- Handoff: to `codex`", rendered);

        var withoutDispatch = HandoverRenderer.Render(content with { Dispatch = null });
        Assert.Contains("queued to continue under a different agent", withoutDispatch);
    }

    [Fact]
    public void Prior_session_lineage_keeps_the_replaced_cross_agent_address_bounded()
    {
        var claude = new SessionAddress("claude", "s-1", "/ws");
        var older = new SessionAddress("copilot", "s-0", "/ws");

        Assert.Null(GitHubClaimService.PriorSessionLineage(
            null, null, "codex", sameSession: false));
        Assert.Null(GitHubClaimService.PriorSessionLineage(
            claude, null, "claude", sameSession: false));
        var lineage = GitHubClaimService.PriorSessionLineage(
            claude, [older], "codex", sameSession: false);
        Assert.Equal([claude, older], lineage);
        Assert.Equal(
            3,
            GitHubClaimService.PriorSessionLineage(
                claude,
                [older, older, older],
                "codex",
                sameSession: false)!.Count);
        Assert.Equal(
            [older],
            GitHubClaimService.PriorSessionLineage(
                claude, [older], "codex", sameSession: true));
    }

    private WorkerService Worker(LocalMarkdownTrackerBackend backend, IAgentProcessRunner runner) =>
        new(
            new TrackerService(new TrackerBackendRegistry([backend])),
            runner,
            new CurrentWorkspace(),
            [new ClaudeAgentAdapter(), new CodexAgentAdapter()],
            clock: () => clock.UtcNow);

    private async Task<(LocalMarkdownTrackerBackend Backend, TrackerConfig Config, WorkItemId Id)>
        CreateQueueableItemAsync(WorkerUsageFailureConfig usageFailure)
    {
        var backend = new LocalMarkdownTrackerBackend(new FakeIdentity(), clock);
        var config = WorkerConfig(usageFailure);
        await backend.InitializeAsync(config, false, CancellationToken.None);
        var created = await backend.CreateAsync(config, new CreateWorkItemOperation(
            new CreateWorkItemRequest("Hand off on exhaustion", "Body", "Todo", "P1",
                AutomaticExecutionAllowed: true, AgentPolicy: "claude"), false),
            CancellationToken.None);
        return (backend, config, created.Id);
    }

    /// <summary>Seeds an item with a complete recorded claude session and no pending dispatch —
    /// the state an operator-invoked handoff starts from. With <paramref name="releaseClaim"/>
    /// false the claim stays active, as after a needs-attention ending; a non-null
    /// <paramref name="agentPolicy"/> repoints the policy field after the session is recorded,
    /// like an operator directing a handover.</summary>
    private async Task<(LocalMarkdownTrackerBackend Backend, TrackerConfig Config, WorkItemId Id)>
        SeedRecordedSessionAsync(
            bool releaseClaim = true,
            string? agentPolicy = null,
            string? dispatchState = null)
    {
        var backend = new LocalMarkdownTrackerBackend(new FakeIdentity(), clock);
        var config = WorkerConfig(new WorkerUsageFailureConfig());
        await backend.InitializeAsync(config, false, CancellationToken.None);
        var created = await backend.CreateAsync(config, new CreateWorkItemOperation(
            new CreateWorkItemRequest("Switch agents", "Body", "In Progress", "P1",
                AutomaticExecutionAllowed: true, AgentPolicy: "claude"), false),
            CancellationToken.None);
        var context = new AgentExecutionContext("claude", "handoff-session",
            AgentContextSource.ExplicitOption,
            ClaimantKind: ClaimantKind.Agent, ClaimantId: "agent:worker:seed");
        var claim = await backend.TryClaimAsync(
            config, created.Id, context, CancellationToken.None);
        var handle = new ClaimHandle(context, claim.ClaimToken);
        await backend.RenewClaimAsync(
            config, created.Id, handle, directory, "handoff-session", CancellationToken.None);
        if (agentPolicy is not null || dispatchState is not null)
            await backend.UpdateAsync(config, created.Id, new UpdateWorkItemOperation(
                new WorkItemPatch(
                    OptionalValue<string>.Unspecified,
                    OptionalValue<string>.Unspecified,
                    OptionalValue<string>.Unspecified,
                    OptionalValue<string?>.Unspecified,
                    AgentPolicy: agentPolicy is null
                        ? default
                        : OptionalValue<string?>.From(agentPolicy),
                    DispatchState: dispatchState is null
                        ? default
                        : OptionalValue<string?>.From(dispatchState)),
                false,
                ClaimHandle: handle), CancellationToken.None);
        if (releaseClaim)
            await backend.ReleaseAsync(config, created.Id, handle, false, DispatchStateOnRelease.Clear, CancellationToken.None);
        return (backend, config, created.Id);
    }

    /// <summary>Seeds the state a scheduling pass leaves behind: an In Progress item whose claude
    /// session is recorded, whose handoff to codex is queued locally and published, and whose
    /// claim is released.</summary>
    private async Task<(LocalMarkdownTrackerBackend Backend, TrackerConfig Config, WorkItemId Id)>
        SeedQueuedHandoffAsync(DateTimeOffset? notBefore = null)
    {
        var backend = new LocalMarkdownTrackerBackend(new FakeIdentity(), clock);
        var config = WorkerConfig(new WorkerUsageFailureConfig());
        await backend.InitializeAsync(config, false, CancellationToken.None);
        var created = await backend.CreateAsync(config, new CreateWorkItemOperation(
            new CreateWorkItemRequest("Continue elsewhere", "Body", "In Progress", "P1",
                AutomaticExecutionAllowed: true, AgentPolicy: "claude"), false),
            CancellationToken.None);
        var context = new AgentExecutionContext("claude", "handoff-session",
            AgentContextSource.ExplicitOption,
            ClaimantKind: ClaimantKind.Agent, ClaimantId: "agent:worker:seed");
        var claim = await backend.TryClaimAsync(
            config, created.Id, context, CancellationToken.None);
        var handle = new ClaimHandle(context, claim.ClaimToken);
        await backend.RenewClaimAsync(
            config, created.Id, handle, directory, "handoff-session", CancellationToken.None);
        await backend.RecordPendingDispatchAsync(config, created.Id, new PendingDispatch(
            created.Id.Value,
            DispatchStates.HandoffQueued,
            "Usage limit reached.",
            "claude",
            "handoff-session",
            "codex",
            notBefore ?? clock.UtcNow,
            1,
            5,
            AgentFailureConfidence.Authoritative,
            clock.UtcNow), CancellationToken.None);
        await backend.UpdateAsync(config, created.Id, new UpdateWorkItemOperation(
            new WorkItemPatch(
                OptionalValue<string>.Unspecified,
                OptionalValue<string>.Unspecified,
                OptionalValue<string>.Unspecified,
                OptionalValue<string?>.Unspecified,
                DispatchState: OptionalValue<string?>.From(DispatchStates.HandoffQueued)),
            false,
            ClaimHandle: handle), CancellationToken.None);
        await backend.ReleaseAsync(config, created.Id, handle, false, DispatchStateOnRelease.Preserve, CancellationToken.None);
        return (backend, config, created.Id);
    }

    private TrackerConfig WorkerConfig(WorkerUsageFailureConfig usageFailure) => new()
    {
        Backend = "local-markdown",
        DefaultPickFrom = "Todo",
        Worker = new WorkerConfig { UseWorkerQueue = false, UsageFailure = usageFailure },
        SourcePath = Path.Combine(directory, ".wrighty.json"),
        LocalMarkdown = new LocalMarkdownBackendConfig(),
        LeaseMinutes = 60
    };

    private static WorkerOptions Options(string? agent = "claude") =>
        new(
            agent,
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

    private static Func<WorkerEvent, Task> Collect(List<WorkerEvent> events) =>
        value =>
        {
            events.Add(value);
            return Task.CompletedTask;
        };

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
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

    private sealed class CurrentWorkspace : IWorkspaceManager
    {
        public Task<Workspace> PrepareAsync(
            WorkspaceRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new Workspace(Path.GetFullPath(request.RepositoryPath)));
    }

    private sealed class UsageFailureRunner : IAgentProcessRunner
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
            // Echo back whichever session the invocation addressed, so fresh launches and
            // resumes both look like the vendor honoring its session id.
            var arguments = invocation.Arguments.ToList();
            var marker = arguments.IndexOf("--session-id");
            var resume = arguments.IndexOf("--resume");
            var sessionId = marker >= 0
                ? arguments[marker + 1]
                : resume >= 0
                    ? arguments[resume + 1]
                    : "session-from-output";
            return Task.FromResult(new AgentRunResult(
                AgentOutcome.Failed,
                sessionId,
                "Usage limit reached.",
                1,
                new AgentFailure(
                    AgentFailureKind.UsageExhausted,
                    "usage_limit_reached",
                    null,
                    null,
                    true,
                    AgentFailureConfidence.Authoritative,
                    "Usage limit reached.")));
        }
    }

    private sealed class RecordingRunner : IAgentProcessRunner
    {
        public AgentInvocation? Invocation { get; private set; }

        public IAgentAdapter? Adapter { get; private set; }

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
            Adapter = adapter;
            return Task.FromResult(new AgentRunResult(
                AgentOutcome.Succeeded, "thread-999", "Continued the work.", 0));
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
            throw new Xunit.Sdk.XunitException("No agent process should have started.");
    }
}
