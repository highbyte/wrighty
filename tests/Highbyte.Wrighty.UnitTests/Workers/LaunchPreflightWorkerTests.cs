using Highbyte.Wrighty.AgentContext;
using Highbyte.Wrighty.ApprovedContext;
using Highbyte.Wrighty.Backends;
using Highbyte.Wrighty.Claims;
using Highbyte.Wrighty.Configuration;
using Highbyte.Wrighty.Identity;
using Highbyte.Wrighty.LocalMarkdown;
using Highbyte.Wrighty.Models;
using Highbyte.Wrighty.Processes;
using Highbyte.Wrighty.Time;
using Highbyte.Wrighty.Workers;

namespace Highbyte.Wrighty.UnitTests.Workers;

/// <summary>
/// The launch-preflight seam as <see cref="WorkerService"/> actually uses it: a refusal at the
/// last gate must stop the vendor process, leave nothing claimed, and leave no workspace behind
/// that only this aborted launch created.
/// </summary>
public sealed class LaunchPreflightWorkerTests : IDisposable
{
    private readonly string directory =
        Path.Combine(Path.GetTempPath(), $"wrighty-preflight-{Guid.NewGuid():N}");

    private readonly FakeClock clock = new(DateTimeOffset.Parse("2026-07-25T10:00:00Z"));

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }

    [Fact]
    public async Task Pre_spawn_refusal_stops_the_vendor_and_releases_the_claim()
    {
        var backend = new LocalMarkdownTrackerBackend(new FakeIdentity(), clock);
        var config = Config();
        await backend.InitializeAsync(config, false, CancellationToken.None);
        var created = await backend.CreateAsync(config, new CreateWorkItemOperation(
            new CreateWorkItemRequest("Approved-context gate", "Body", "Todo", "P1",
                AutomaticExecutionAllowed: true, AgentPolicy: "claude"), false),
            CancellationToken.None);
        var workspaces = new RecordingWorkspaces();
        var events = new List<WorkerEvent>();
        var worker = new WorkerService(
            new TrackerService(new TrackerBackendRegistry([backend])),
            new FailIfRunRunner(),
            workspaces,
            [new ClaudeAgentAdapter()],
            clock: () => clock.UtcNow,
            launchPreflightChecks:
            [
                new RefusingCheck(
                    "approved-context", LaunchStage.PreSpawn, "CONTEXT_REVISION_CHANGED")
            ]);

        var summary = await worker.RunItemAsync(
            config, Options(), directory, created.Id, WorkerItemIntent.Fresh, null,
            value =>
            {
                events.Add(value);
                return Task.CompletedTask;
            },
            CancellationToken.None);

        Assert.Equal(1, summary.Processed);
        Assert.Equal(0, summary.Failed);
        Assert.Equal(0, summary.NeedsAttention);
        Assert.DoesNotContain(events, value => value.Type == "started");

        var skipped = Assert.Single(events, value => value.Type == "skipped-policy");
        Assert.Contains("pre-spawn", skipped.Message);
        Assert.Contains("approved-context", skipped.Message);
        Assert.Contains("CONTEXT_REVISION_CHANGED", skipped.Message);

        // The item goes back to the claimable pool in its original status, so resolving the
        // refusal is all an operator has to do.
        var item = await backend.GetAsync(config, created.Id, CancellationToken.None);
        Assert.Equal("Todo", item!.Status);
        Assert.True(item.AutomaticExecutionAllowed);
        Assert.Equal(
            ClaimOwnershipState.Unclaimed,
            (await backend.GetClaimOwnershipAsync(
                config, created.Id, CancellationToken.None)).State);

        // Nothing this launch created may survive it.
        Assert.Equal(1, workspaces.Prepared);
        Assert.Equal(1, workspaces.CleanedUp);
    }

    [Fact]
    public async Task A_refused_queued_resume_leaves_the_item_needing_attention_and_queueable()
    {
        // A fresh launch that is refused goes back to the claimable pool, and that is its whole
        // unwind — the test above covers it. A QUEUED RESUME is different: an operator put the item
        // in the queue from needs-attention, and the refusal used to release in a way that cleared
        // the dispatch state. The item then read as an idle paused session — outside
        // needs-attention, where the queue action lives — so the operator's next step was gone
        // along with any sign that something needed them. Seen live when a queued session's
        // recorded context could not be established: the refusal was correct, the stranding was
        // not.
        var backend = new LocalMarkdownTrackerBackend(new FakeIdentity(), clock);
        var config = Config();
        await backend.InitializeAsync(config, false, CancellationToken.None);
        var created = await backend.CreateAsync(config, new CreateWorkItemOperation(
            new CreateWorkItemRequest("Queued then refused", "Body", "In Progress", "P1",
                AutomaticExecutionAllowed: true, AgentPolicy: "claude"), false),
            CancellationToken.None);

        // A recorded session paused into needs-attention, then queued by the operator.
        var context = new AgentExecutionContext(
            "claude", "recorded-session", AgentContextSource.ExplicitOption,
            ClaimantKind: ClaimantKind.Agent, ClaimantId: "agent:worker:recorded");
        var claim = await backend.TryClaimAsync(config, created.Id, context, CancellationToken.None);
        var handle = new ClaimHandle(context, claim.ClaimToken);
        await backend.RenewClaimAsync(
            config, created.Id, handle, directory, "recorded-session", CancellationToken.None);
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
        await backend.QueuePausedAsync(config, created.Id, CancellationToken.None);

        var workspaces = new RecordingWorkspaces();
        var events = new List<WorkerEvent>();
        var worker = new WorkerService(
            new TrackerService(new TrackerBackendRegistry([backend])),
            new FailIfRunRunner(),
            workspaces,
            [new ClaudeAgentAdapter()],
            clock: () => clock.UtcNow,
            launchPreflightChecks:
            [
                new RefusingCheck(
                    "approved-context", LaunchStage.PreSpawn, "CONTEXT_MANIFEST_UNAVAILABLE")
            ]);

        var summary = await worker.RunItemAsync(
            config, Options(), directory, created.Id, WorkerItemIntent.Auto, null,
            value =>
            {
                events.Add(value);
                return Task.CompletedTask;
            },
            CancellationToken.None);

        // The refusal itself is correct and the vendor never starts.
        Assert.DoesNotContain(events, value => value.Type == "started");
        Assert.Contains(events, value =>
            value.Type == "skipped-policy" &&
            value.Message!.Contains("CONTEXT_MANIFEST_UNAVAILABLE", StringComparison.Ordinal));

        // The stranding is the defect: the item must come out of this needing attention — visible,
        // with the queue action offered — not as an idle paused session with no action on it, and
        // not still queued, which would refuse again on every poll.
        var item = await backend.GetAsync(config, created.Id, CancellationToken.None);
        Assert.Equal(DispatchStates.NeedsAttention, item!.DispatchState);
        Assert.Equal(
            ClaimOwnershipState.Unclaimed,
            (await backend.GetClaimOwnershipAsync(
                config, created.Id, CancellationToken.None)).State);
        // The recorded session survives for the next, resolved attempt.
        var session = await backend.GetAgentSessionAsync(config, created.Id, CancellationToken.None);
        Assert.Equal("recorded-session", session!.SessionId);
    }

    [Fact]
    public async Task An_admitting_pre_spawn_check_leaves_the_normal_launch_untouched()
    {
        var backend = new LocalMarkdownTrackerBackend(new FakeIdentity(), clock);
        var config = Config();
        await backend.InitializeAsync(config, false, CancellationToken.None);
        var created = await backend.CreateAsync(config, new CreateWorkItemOperation(
            new CreateWorkItemRequest("Approved item", "Body", "Todo", "P1",
                AutomaticExecutionAllowed: true, AgentPolicy: "claude"), false),
            CancellationToken.None);
        var workspaces = new RecordingWorkspaces();
        var events = new List<WorkerEvent>();
        var check = new AdmittingCheck();
        var worker = new WorkerService(
            new TrackerService(new TrackerBackendRegistry([backend])),
            new SucceedingRunner(),
            workspaces,
            [new ClaudeAgentAdapter()],
            clock: () => clock.UtcNow,
            launchPreflightChecks: [check]);

        await worker.RunItemAsync(
            config, Options(), directory, created.Id, WorkerItemIntent.Fresh, null,
            value =>
            {
                events.Add(value);
                return Task.CompletedTask;
            },
            CancellationToken.None);

        Assert.Contains(events, value => value.Type == "started");
        Assert.Equal(0, workspaces.CleanedUp);
        // Post-claim and pre-spawn are distinct evaluations, not one call reused.
        Assert.Equal(
            [LaunchStage.PostClaim, LaunchStage.PreSpawn],
            check.Stages);
    }

    [Fact]
    public async Task An_admitted_launch_records_the_context_it_resolved_with_the_session()
    {
        // The check resolves the context and the worker persists it. Between those two is the
        // wiring this covers: without it the check still admits every launch and every later
        // resume still refuses, with nothing anywhere saying why.
        //
        // Read after the run rather than at launch, deliberately. The launch records the context
        // before the vendor has reported the session id it will use, so a carry-forward keyed on
        // session-id equality drops it the moment that id lands — which looks perfect at spawn and
        // leaves nothing behind by the time anything would read it.
        var backend = new LocalMarkdownTrackerBackend(new FakeIdentity(), clock);
        var config = Config();
        await backend.InitializeAsync(config, false, CancellationToken.None);
        var created = await backend.CreateAsync(config, new CreateWorkItemOperation(
            new CreateWorkItemRequest("Approved item", "Body", "Todo", "P1",
                AutomaticExecutionAllowed: true, AgentPolicy: "claude"), false),
            CancellationToken.None);
        var events = new List<WorkerEvent>();
        var worker = new WorkerService(
            new TrackerService(new TrackerBackendRegistry([backend])),
            new SucceedingRunner(),
            new RecordingWorkspaces(),
            [new ClaudeAgentAdapter()],
            clock: () => clock.UtcNow,
            launchPreflightChecks: [new ContextResolvingCheck(clock.UtcNow)]);

        await worker.RunItemAsync(
            config, Options(), directory, created.Id, WorkerItemIntent.Fresh, null,
            value =>
            {
                events.Add(value);
                return Task.CompletedTask;
            },
            CancellationToken.None);

        Assert.Contains(events, value => value.Type == "started");
        var session = await backend.GetAgentSessionAsync(
            config, created.Id, CancellationToken.None);
        Assert.Equal("sha256:resolved", session!.Context?.SuppliedDigest);
        Assert.Equal(ContextApprovalSource.ProjectField, session.Context!.ApprovalSource);
    }

    [Fact]
    public async Task A_launch_survives_a_session_context_that_cannot_be_recorded()
    {
        // Failing the launch here would let a machine-local write problem block work that is
        // properly approved. The degradation is already safe — an unrecorded context refuses to
        // resume — so the run proceeds and says so.
        var backend = new LocalMarkdownTrackerBackend(new FakeIdentity(), clock);
        var config = Config();
        await backend.InitializeAsync(config, false, CancellationToken.None);
        var created = await backend.CreateAsync(config, new CreateWorkItemOperation(
            new CreateWorkItemRequest("Approved item", "Body", "Todo", "P1",
                AutomaticExecutionAllowed: true, AgentPolicy: "claude"), false),
            CancellationToken.None);
        var events = new List<WorkerEvent>();
        var worker = new WorkerService(
            new TrackerService(new TrackerBackendRegistry(
                [new UnrecordableBackend(backend)])),
            new SucceedingRunner(),
            new RecordingWorkspaces(),
            [new ClaudeAgentAdapter()],
            clock: () => clock.UtcNow,
            launchPreflightChecks: [new ContextResolvingCheck(clock.UtcNow)]);

        await worker.RunItemAsync(
            config, Options(), directory, created.Id, WorkerItemIntent.Fresh, null,
            value =>
            {
                events.Add(value);
                return Task.CompletedTask;
            },
            CancellationToken.None);

        Assert.Contains(events, value => value.Type == "started");
        var warning = Assert.Single(events, value => value.Type == "context-record-failed");
        Assert.Contains("CONTEXT_STORE_UNAVAILABLE", warning.Message);
        Assert.Equal(
            WorkerEventSemantic.Warning, WorkerEventClassifier.Classify(warning.Type));
    }

    [Fact]
    public async Task An_approved_context_reaches_the_agent_on_stdin_and_never_in_an_event()
    {
        // The end of the chain: gate admits, context is rendered, and the vendor receives it — with
        // the content on standard input. Worker events print the argument list, so a context that
        // travelled on the command line would be published to the terminal and to any log capturing
        // it, on every single run.
        var backend = new LocalMarkdownTrackerBackend(new FakeIdentity(), clock);
        var config = Config();
        await backend.InitializeAsync(config, false, CancellationToken.None);
        var created = await backend.CreateAsync(config, new CreateWorkItemOperation(
            new CreateWorkItemRequest("Approved item", "Body", "Todo", "P1",
                AutomaticExecutionAllowed: true, AgentPolicy: "claude"), false),
            CancellationToken.None);
        var events = new List<WorkerEvent>();
        var runner = new CapturingRunner();
        var worker = new WorkerService(
            new TrackerService(new TrackerBackendRegistry([backend])),
            runner,
            new RecordingWorkspaces(),
            [new ClaudeAgentAdapter()],
            clock: () => clock.UtcNow,
            launchPreflightChecks: [new ContextResolvingCheck(clock.UtcNow)]);

        await worker.RunItemAsync(
            config, Options(), directory, created.Id, WorkerItemIntent.Fresh, null,
            value =>
            {
                events.Add(value);
                return Task.CompletedTask;
            },
            CancellationToken.None);

        Assert.NotNull(runner.Invocation);
        Assert.NotNull(runner.Invocation!.StandardInput);
        Assert.Contains("Resolved body", runner.Invocation.StandardInput!, StringComparison.Ordinal);
        Assert.Contains("Trust boundary", runner.Invocation.StandardInput!, StringComparison.Ordinal);

        Assert.DoesNotContain(
            runner.Invocation.Arguments,
            a => a.Contains("Resolved body", StringComparison.Ordinal));
        foreach (var value in events)
        {
            var rendered = string.Join(" ", value.Arguments ?? []) + " " + (value.Message ?? "");
            Assert.DoesNotContain("Resolved body", rendered, StringComparison.Ordinal);
            Assert.DoesNotContain("Trust boundary", rendered, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task A_backend_with_no_approved_context_still_gets_the_bootstrap_prompt()
    {
        // Not every backend has an approval surface. Those launches must keep working, and their
        // agent still learns what to do by reading the item.
        var backend = new LocalMarkdownTrackerBackend(new FakeIdentity(), clock);
        var config = Config();
        await backend.InitializeAsync(config, false, CancellationToken.None);
        var created = await backend.CreateAsync(config, new CreateWorkItemOperation(
            new CreateWorkItemRequest("Plain item", "Body", "Todo", "P1",
                AutomaticExecutionAllowed: true, AgentPolicy: "claude"), false),
            CancellationToken.None);
        var runner = new CapturingRunner();
        var worker = new WorkerService(
            new TrackerService(new TrackerBackendRegistry([backend])),
            runner,
            new RecordingWorkspaces(),
            [new ClaudeAgentAdapter()],
            clock: () => clock.UtcNow,
            launchPreflightChecks: [new AdmittingCheck()]);

        await worker.RunItemAsync(
            config, Options(), directory, created.Id, WorkerItemIntent.Fresh, null,
            _ => Task.CompletedTask, CancellationToken.None);

        Assert.NotNull(runner.Invocation);
        Assert.Null(runner.Invocation!.StandardInput);
        Assert.Contains(runner.Invocation.Arguments, a => a.Contains("local:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_pre_claim_refusal_passes_over_the_item_and_the_next_candidate_runs()
    {
        // The starvation-and-churn defect this stage exists for: a higher-priority item whose
        // approved context is refused used to be claimed, status-moved, refused post-claim, and
        // released on every poll — visible churn on the item, and no candidate ranked behind it
        // could ever run. Refused before the claim, it must instead be passed over untouched
        // while the next candidate launches.
        var backend = new LocalMarkdownTrackerBackend(new FakeIdentity(), clock);
        var config = Config();
        await backend.InitializeAsync(config, false, CancellationToken.None);
        var blocked = await backend.CreateAsync(config, new CreateWorkItemOperation(
            new CreateWorkItemRequest("Blocked item", "Body", "Todo", "P0",
                AutomaticExecutionAllowed: true, AgentPolicy: "claude"), false),
            CancellationToken.None);
        var runnable = await backend.CreateAsync(config, new CreateWorkItemOperation(
            new CreateWorkItemRequest("Runnable item", "Body", "Todo", "P1",
                AutomaticExecutionAllowed: true, AgentPolicy: "claude"), false),
            CancellationToken.None);
        var workspaces = new RecordingWorkspaces();
        var events = new List<WorkerEvent>();
        var worker = new WorkerService(
            new TrackerService(new TrackerBackendRegistry([backend])),
            new SucceedingRunner(),
            workspaces,
            [new ClaudeAgentAdapter()],
            clock: () => clock.UtcNow,
            launchPreflightChecks:
            [
                new PreClaimRefusingCheck(blocked.Id)
            ]);
        Func<WorkerEvent, Task> emit = value =>
        {
            events.Add(value);
            return Task.CompletedTask;
        };

        var summary = await worker.RunAsync(
            config, Options(), directory, emit, CancellationToken.None);

        // The lower-priority candidate ran; the blocked one was never claimed and never moved.
        Assert.Equal(1, summary.Processed);
        var started = Assert.Single(events, value => value.Type == "started");
        Assert.Equal(runnable.Id.Value, started.ItemId);
        var item = await backend.GetAsync(config, blocked.Id, CancellationToken.None);
        Assert.Equal("Todo", item!.Status);
        Assert.Equal(
            ClaimOwnershipState.Unclaimed,
            (await backend.GetClaimOwnershipAsync(
                config, blocked.Id, CancellationToken.None)).State);
        var skipped = Assert.Single(events, value => value.Type == "skipped-policy");
        Assert.Equal(blocked.Id.Value, skipped.ItemId);
        Assert.Contains("pre-claim", skipped.Message);
        Assert.Contains("CONTEXT_COMMENT_PENDING", skipped.Message);
        Assert.Contains("was not claimed", skipped.Message);
        Assert.Equal(1, workspaces.Prepared);

        // A second pass over the same unchanged item is silent — the skip was already announced —
        // and the idle diagnostics carry the count instead.
        var second = await worker.RunAsync(
            config, Options(), directory, emit, CancellationToken.None);

        Assert.Equal(0, second.Processed);
        Assert.Single(events, value => value.Type == "skipped-policy");
        var idle = Assert.Single(events, value => value.Type == "no-item");
        Assert.Equal(1, idle.Candidates!.ContextBlocked);
    }

    [Fact]
    public void Worker_service_reports_which_checks_gate_each_stage()
    {
        var worker = new WorkerService(
            null!, new FailIfRunRunner(), new RecordingWorkspaces(), [new ClaudeAgentAdapter()],
            launchPreflightChecks: [new AdmittingCheck()]);

        Assert.Equal(
            ["worker-policy", "agent-permissions", "recording"],
            worker.LaunchPreflightChecks(LaunchStage.PostClaim, LaunchKind.Fresh));
        Assert.Equal(
            ["recording"],
            worker.LaunchPreflightChecks(LaunchStage.PreSpawn, LaunchKind.Fresh));
    }

    private TrackerConfig Config() => new()
    {
        Backend = "local-markdown",
        SourcePath = Path.Combine(directory, ".wrighty.json"),
        LocalMarkdown = new LocalMarkdownBackendConfig(),
        LeaseMinutes = 60
    };

    private static WorkerOptions Options() => new(
        "claude", true, null, WorkspaceMode.Current, new Dictionary<string, string>(),
        null, TimeSpan.FromMinutes(10), FencedAction.Kill, null, "agent", false, false);

    /// <summary>
    /// Stands in for the approved-context check's advisory stage: refuses one item before the
    /// claim and admits everything else everywhere.
    /// </summary>
    private sealed class PreClaimRefusingCheck(WorkItemId blocked) : ILaunchPreflightCheck
    {
        public string Name => "approved-context";

        public bool AppliesTo(LaunchStage stage, LaunchKind kind) =>
            stage == LaunchStage.PreClaim;

        public ValueTask<LaunchPreflightDecision> EvaluateAsync(
            LaunchPreflightRequest request, CancellationToken cancellationToken) =>
            ValueTask.FromResult(request.Detail.Id == blocked
                ? LaunchPreflightDecision.Refuse(
                    "CONTEXT_COMMENT_PENDING",
                    "One comment has no approval or exclusion decision covering its current " +
                    "revision.")
                : LaunchPreflightDecision.Admit());
    }

    private sealed class RefusingCheck(string name, LaunchStage stage, string code)
        : ILaunchPreflightCheck
    {
        public string Name => name;

        public bool AppliesTo(LaunchStage value, LaunchKind kind) => value == stage;

        public ValueTask<LaunchPreflightDecision> EvaluateAsync(
            LaunchPreflightRequest request, CancellationToken cancellationToken) =>
            ValueTask.FromResult(LaunchPreflightDecision.Refuse(
                code, "The approved context revision changed after the claim."));
    }

    /// <summary>
    /// Stands in for the approved-context check: admits, and offers the context it resolved for the
    /// worker to record. Local rather than the real check so this test covers the worker's wiring
    /// and not the resolver's rules.
    /// </summary>
    /// <summary>Captures the invocation so a test can inspect where the prompt travelled.</summary>
    private sealed class CapturingRunner : IAgentProcessRunner
    {
        public AgentInvocation? Invocation { get; private set; }

        public Task<AgentRunResult> RunAsync(
            AgentInvocation invocation, IAgentAdapter adapter, TimeSpan timeout,
            IReadOnlyDictionary<string, string> grantEnvironment,
            Func<string, CancellationToken, Task>? sessionStarted,
            bool killOnCancellation, CancellationToken cancellationToken)
        {
            Invocation = invocation;
            return Task.FromResult(new AgentRunResult(
                AgentOutcome.Succeeded, "session-preflight", "Needs a decision."));
        }
    }

    private sealed class ContextResolvingCheck(DateTimeOffset capturedAt)
        : ILaunchPreflightCheck, ILaunchSessionContextSource
    {
        public string Name => "approved-context";

        public bool AppliesTo(LaunchStage stage, LaunchKind kind) =>
            stage is LaunchStage.PostClaim or LaunchStage.PreSpawn;

        public ValueTask<LaunchPreflightDecision> EvaluateAsync(
            LaunchPreflightRequest request, CancellationToken cancellationToken) =>
            ValueTask.FromResult(LaunchPreflightDecision.Admit());

        public ResolvedLaunchContext? TakeResolvedContext(WorkItemId id)
        {
            var decisions = Array.Empty<DiscussionDecision>();
            var snapshot = new ExecutionContextSnapshot(
                id, "Resolved title", "Resolved body",
                new ContextApproval(ContextApprovalSource.ProjectField, capturedAt, capturedAt),
                new BaseContentRevision("sha256:title", "sha256:body"),
                new ContextRevision(1, "sha256:resolved", capturedAt),
                ExecutionContextSnapshot.NoDiscussion, decisions);
            return new ResolvedLaunchContext(id, snapshot, capturedAt);
        }
    }

    /// <summary>A backend whose session-context write always fails.</summary>
    private sealed class UnrecordableBackend(ITrackerBackend inner)
        : DelegatingTrackerBackend(inner)
    {
        public override Task RecordSessionContextAsync(
            TrackerConfig config, WorkItemId id, SessionContextMetadata context,
            CancellationToken cancellationToken) =>
            throw new Errors.TrackerException(
                "CONTEXT_STORE_UNAVAILABLE", "The session store could not be written.", 5);
    }

    private sealed class AdmittingCheck : ILaunchPreflightCheck
    {
        public List<LaunchStage> Stages { get; } = [];

        public string Name => "recording";

        public bool AppliesTo(LaunchStage stage, LaunchKind kind) =>
            stage is LaunchStage.PostClaim or LaunchStage.PreSpawn;

        public ValueTask<LaunchPreflightDecision> EvaluateAsync(
            LaunchPreflightRequest request, CancellationToken cancellationToken)
        {
            Stages.Add(request.Stage);
            return ValueTask.FromResult(LaunchPreflightDecision.Admit());
        }
    }

    private sealed class RecordingWorkspaces : IWorkspaceManager
    {
        public int Prepared { get; private set; }

        public int CleanedUp { get; private set; }

        public Task<Workspace> PrepareAsync(
            WorkspaceRequest request, CancellationToken cancellationToken)
        {
            Prepared++;
            return Task.FromResult(new Workspace(Path.GetFullPath(request.RepositoryPath)));
        }

        public Task<bool> CleanupAsync(Workspace workspace, CancellationToken cancellationToken)
        {
            CleanedUp++;
            return Task.FromResult(true);
        }
    }

    private sealed class FailIfRunRunner : IAgentProcessRunner
    {
        public Task<AgentRunResult> RunAsync(
            AgentInvocation invocation, IAgentAdapter adapter, TimeSpan timeout,
            IReadOnlyDictionary<string, string> grantEnvironment,
            Func<string, CancellationToken, Task>? sessionStarted,
            bool killOnCancellation, CancellationToken cancellationToken) =>
            throw new Xunit.Sdk.XunitException("No vendor process should have been started.");
    }

    private sealed class SucceedingRunner : IAgentProcessRunner
    {
        public Task<AgentRunResult> RunAsync(
            AgentInvocation invocation, IAgentAdapter adapter, TimeSpan timeout,
            IReadOnlyDictionary<string, string> grantEnvironment,
            Func<string, CancellationToken, Task>? sessionStarted,
            bool killOnCancellation, CancellationToken cancellationToken) =>
            Task.FromResult(new AgentRunResult(
                AgentOutcome.Succeeded, "session-preflight", "Needs a decision."));
    }

    private sealed class FakeIdentity : IInstallationIdentityProvider
    {
        public Task<string> GetInstallationIdAsync(CancellationToken cancellationToken) =>
            Task.FromResult("worker-preflight-test");
    }

    private sealed class FakeClock(DateTimeOffset value) : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = value;
    }
}
