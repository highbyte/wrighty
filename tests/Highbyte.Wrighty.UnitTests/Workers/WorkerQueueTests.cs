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

namespace Highbyte.Wrighty.UnitTests.Workers;

/// <summary>
/// The worker queue (on by default, <c>worker.useWorkerQueue</c>): moving an item into the
/// pick-from status — "Worker queue" by default — authorizes automatic execution and projected
/// GitHub context, while moving it out revokes execution only. Never applied to the worker's own
/// status moves.
/// </summary>
public sealed class WorkerQueueTests : IDisposable
{
    private readonly string directory =
        Path.Combine(Path.GetTempPath(), $"wrighty-worker-queue-{Guid.NewGuid():N}");

    private readonly FakeClock clock = new(DateTimeOffset.Parse("2026-08-03T10:00:00Z"));

    [Fact]
    public async Task Entering_the_queue_authorizes_and_leaving_revokes_by_default()
    {
        var (service, backend, config, id) = await SetupAsync(initialStatus: "Todo");
        var handle = await ClaimAsHumanAsync(backend, config, id);

        await service.UpdateAsync(
            config, id, WorkItemPatch.StatusOnly("Worker queue"),
            expectedRevision: null, handle, CancellationToken.None);
        var entered = await backend.GetAsync(config, id, CancellationToken.None);
        Assert.True(entered!.AutomaticExecutionAllowed);

        await service.UpdateAsync(
            config, id, WorkItemPatch.StatusOnly("Todo"),
            expectedRevision: null, handle, CancellationToken.None);
        var left = await backend.GetAsync(config, id, CancellationToken.None);
        Assert.False(left!.AutomaticExecutionAllowed);
    }

    [Fact]
    public async Task Entering_the_queue_cycles_projected_context_approval_every_time()
    {
        var (service, inner, config, id) = await SetupAsync(initialStatus: "Todo");
        var approvals = new Dictionary<WorkItemId, bool> { [id] = true };
        var backend = new ApprovalTrackingBackend(inner, approvals);
        service = new TrackerService(new TrackerBackendRegistry([backend]));
        var handle = await ClaimAsHumanAsync(inner, config, id);

        await service.UpdateAsync(
            config, id, WorkItemPatch.StatusOnly("Worker queue"),
            expectedRevision: null, handle, CancellationToken.None);
        await service.UpdateAsync(
            config, id, WorkItemPatch.StatusOnly("Todo"),
            expectedRevision: null, handle, CancellationToken.None);
        await service.UpdateAsync(
            config, id, WorkItemPatch.StatusOnly("Worker queue"),
            expectedRevision: null, handle, CancellationToken.None);

        Assert.Equal(2, backend.ApprovalCycles);
        Assert.True(approvals[id]);
    }

    [Fact]
    public async Task Approval_failure_reports_the_already_applied_queue_move()
    {
        var (_, inner, config, id) = await SetupAsync(initialStatus: "Todo");
        var approvals = new Dictionary<WorkItemId, bool> { [id] = false };
        var backend = new ApprovalTrackingBackend(inner, approvals)
        {
            ApprovalException = new TrackerException(
                "CONTEXT_APPROVAL_UNAVAILABLE", "Approval field missing.")
        };
        var service = new TrackerService(new TrackerBackendRegistry([backend]));
        var handle = await ClaimAsHumanAsync(inner, config, id);

        var exception = await Assert.ThrowsAsync<TrackerException>(() =>
            service.UpdateAsync(
                config, id, WorkItemPatch.StatusOnly("Worker queue"),
                expectedRevision: null, handle, CancellationToken.None));

        Assert.Equal("PARTIAL_UPDATE", exception.Code);
        Assert.Equal(
            ["contextApproval"],
            Assert.IsType<string[]>(exception.Details["pendingFields"]));
        var detail = await inner.GetAsync(config, id, CancellationToken.None);
        Assert.Equal("Worker queue", detail!.Status);
        Assert.True(detail.AutomaticExecutionAllowed);
    }

    [Fact]
    public async Task Opting_out_leaves_execution_policy_untouched()
    {
        var (_, inner, config, id) = await SetupAsync(
            initialStatus: "Todo", useWorkerQueue: false);
        var approvals = new Dictionary<WorkItemId, bool> { [id] = false };
        var backend = new ApprovalTrackingBackend(inner, approvals);
        var service = new TrackerService(new TrackerBackendRegistry([backend]));
        var handle = await ClaimAsHumanAsync(inner, config, id);

        await service.UpdateAsync(
            config, id, WorkItemPatch.StatusOnly("Worker queue"),
            expectedRevision: null, handle, CancellationToken.None);

        var detail = await inner.GetAsync(config, id, CancellationToken.None);
        Assert.False(detail!.AutomaticExecutionAllowed);
        Assert.Equal(0, backend.ApprovalCycles);
        Assert.False(approvals[id]);
    }

    [Fact]
    public async Task Explicitly_patched_execution_policy_wins_over_the_queue_rule()
    {
        var (service, backend, config, id) = await SetupAsync(initialStatus: "Todo");
        var handle = await ClaimAsHumanAsync(backend, config, id);

        await service.UpdateAsync(
            config,
            id,
            new WorkItemPatch(
                OptionalValue<string>.Unspecified,
                OptionalValue<string>.Unspecified,
                OptionalValue<string>.From("Worker queue"),
                OptionalValue<string?>.Unspecified,
                AutomaticExecutionAllowed: OptionalValue<bool>.From(false)),
            expectedRevision: null,
            handle,
            CancellationToken.None);

        var detail = await backend.GetAsync(config, id, CancellationToken.None);
        Assert.Equal("Worker queue", detail!.Status);
        Assert.False(detail.AutomaticExecutionAllowed);
    }

    [Fact]
    public async Task Worker_status_moves_never_trigger_the_queue_rule()
    {
        // A refusal-restore moves an item back into pick-from with applyWorkerQueue: false; the
        // restore of a revoked item must not re-authorize it.
        var (_, inner, config, id) = await SetupAsync(initialStatus: "Todo");
        var approvals = new Dictionary<WorkItemId, bool> { [id] = false };
        var backend = new ApprovalTrackingBackend(inner, approvals);
        var service = new TrackerService(new TrackerBackendRegistry([backend]));
        var handle = await ClaimAsHumanAsync(inner, config, id);

        await service.UpdateAsync(
            config,
            id,
            WorkItemPatch.StatusOnly("Worker queue"),
            expectedRevision: null,
            handle,
            CancellationToken.None,
            applyWorkerQueue: false);

        var detail = await inner.GetAsync(config, id, CancellationToken.None);
        Assert.Equal("Worker queue", detail!.Status);
        Assert.False(detail.AutomaticExecutionAllowed);
        Assert.Equal(0, backend.ApprovalCycles);
        Assert.False(approvals[id]);
    }

    [Fact]
    public async Task Poll_authorizes_items_found_in_the_queue_and_announces_it()
    {
        // The board half of the worker queue: an item placed into pick-from outside any Wrighty
        // surface (a GitHub board drag) is authorized by the worker poll.
        var (service, backend, config, id) = await SetupAsync(initialStatus: "Worker queue");
        var events = new List<WorkerEvent>();
        var worker = Worker(service);
        var options = Options();

        await worker.RunAsync(
            config,
            options,
            directory,
            value =>
            {
                events.Add(value);
                return Task.CompletedTask;
            },
            CancellationToken.None);

        Assert.Contains(events, value => value.Type == "worker-queue-active");
        Assert.Contains(
            events,
            value => value.Type == "worker-queue-authorized" && value.ItemId == id.Value);
        var detail = await backend.GetAsync(config, id, CancellationToken.None);
        Assert.True(detail!.AutomaticExecutionAllowed);
    }

    [Fact]
    public async Task Poll_repairs_the_half_authorized_queue_state_with_context_approval()
    {
        var (_, inner, config, id) = await SetupAsync(
            initialStatus: "Worker queue",
            automaticExecutionAllowed: true);
        var approvals = new Dictionary<WorkItemId, bool> { [id] = false };
        var backend = new ApprovalTrackingBackend(inner, approvals);
        var service = new TrackerService(new TrackerBackendRegistry([backend]));
        var events = new List<WorkerEvent>();

        await Worker(service).RunAsync(
            config,
            Options(),
            directory,
            value =>
            {
                events.Add(value);
                return Task.CompletedTask;
            },
            CancellationToken.None);

        Assert.Equal(1, backend.ApprovalCycles);
        Assert.True(approvals[id]);
        Assert.Contains(
            events,
            value => value.Type == "worker-queue-authorized" && value.ItemId == id.Value);
        Assert.Equal(
            ClaimOwnershipState.Unclaimed,
            (await inner.GetClaimOwnershipAsync(config, id, CancellationToken.None)).State);
    }

    [Fact]
    public async Task Poll_refreshes_existing_approval_when_execution_marks_a_new_entry()
    {
        var (_, inner, config, id) = await SetupAsync(initialStatus: "Worker queue");
        var approvals = new Dictionary<WorkItemId, bool> { [id] = true };
        var backend = new ApprovalTrackingBackend(inner, approvals);
        var service = new TrackerService(new TrackerBackendRegistry([backend]));

        await Worker(service).RunAsync(
            config,
            Options(),
            directory,
            _ => Task.CompletedTask,
            CancellationToken.None);

        Assert.Equal(1, backend.ApprovalCycles);
        Assert.True(
            (await inner.GetAsync(config, id, CancellationToken.None))!
            .AutomaticExecutionAllowed);
    }

    [Fact]
    public async Task Poll_releases_its_automation_claim_when_approval_fails()
    {
        var (_, inner, config, id) = await SetupAsync(
            initialStatus: "Worker queue",
            automaticExecutionAllowed: true);
        var approvals = new Dictionary<WorkItemId, bool> { [id] = false };
        var backend = new ApprovalTrackingBackend(inner, approvals)
        {
            ApprovalException = new TrackerException(
                "CONTEXT_APPROVAL_UNAVAILABLE", "Approval field missing.")
        };
        var service = new TrackerService(new TrackerBackendRegistry([backend]));

        await Worker(service).RunAsync(
            config,
            Options(),
            directory,
            _ => Task.CompletedTask,
            CancellationToken.None);

        Assert.Equal(
            ClaimOwnershipState.Unclaimed,
            (await inner.GetClaimOwnershipAsync(config, id, CancellationToken.None)).State);
        Assert.False(approvals[id]);
    }

    [Fact]
    public async Task Poll_leaves_items_untouched_when_opted_out()
    {
        var (_, inner, config, id) = await SetupAsync(
            initialStatus: "Worker queue", useWorkerQueue: false);
        var approvals = new Dictionary<WorkItemId, bool> { [id] = false };
        var backend = new ApprovalTrackingBackend(inner, approvals);
        var service = new TrackerService(new TrackerBackendRegistry([backend]));
        var events = new List<WorkerEvent>();
        var worker = Worker(service);
        var options = Options();

        await worker.RunAsync(
            config,
            options,
            directory,
            value =>
            {
                events.Add(value);
                return Task.CompletedTask;
            },
            CancellationToken.None);

        Assert.DoesNotContain(events, value => value.Type == "worker-queue-active");
        Assert.DoesNotContain(events, value => value.Type == "worker-queue-authorized");
        var detail = await inner.GetAsync(config, id, CancellationToken.None);
        Assert.False(detail!.AutomaticExecutionAllowed);
        Assert.Equal(0, backend.ApprovalCycles);
        Assert.False(approvals[id]);
    }

    private WorkerService Worker(TrackerService service) => new(
        service,
        new FailingRunner(new AgentFailure(
            AgentFailureKind.Authentication, "auth_failed", null, null, false,
            AgentFailureConfidence.Authoritative, "Not signed in.")),
        new CurrentWorkspace(),
        [new ClaudeAgentAdapter()],
        clock: () => clock.UtcNow);

    private static WorkerOptions Options() => new(
        "claude", true, null, WorkspaceMode.Current,
        new Dictionary<string, string>(), null, TimeSpan.FromMinutes(10),
        FencedAction.Kill, null, "agent", false, false);

    private static async Task<ClaimHandle> ClaimAsHumanAsync(
        LocalMarkdownTrackerBackend backend,
        TrackerConfig config,
        WorkItemId id)
    {
        var context = new AgentExecutionContext(
            null, null, AgentContextSource.ExplicitOption,
            ClaimantKind: ClaimantKind.Human, ClaimantId: "human:queue-test");
        var claim = await backend.TryClaimAsync(config, id, context, CancellationToken.None);
        return new ClaimHandle(
            context with { ClaimantId = claim.ClaimantId }, claim.ClaimToken);
    }

    private async Task<(TrackerService Service, LocalMarkdownTrackerBackend Backend,
        TrackerConfig Config, WorkItemId Id)> SetupAsync(
            string initialStatus,
            bool useWorkerQueue = true,
            bool automaticExecutionAllowed = false)
    {
        var backend = new LocalMarkdownTrackerBackend(new FakeIdentity(), clock);
        // Default statuses and the default pick-from ("Worker queue") are exactly what these tests
        // exercise; only the opt-out cases override the worker section.
        var config = new TrackerConfig
        {
            Backend = "local-markdown",
            SourcePath = Path.Combine(directory, ".wrighty.json"),
            LocalMarkdown = new LocalMarkdownBackendConfig(),
            LeaseMinutes = 60,
            Worker = new WorkerConfig
            {
                RequirementsAssessment = new WorkerRequirementsAssessmentConfig { Mode = "inline" },
                UseWorkerQueue = useWorkerQueue
            }
        };
        await backend.InitializeAsync(config, false, CancellationToken.None);
        var created = await backend.CreateAsync(
            config,
            new CreateWorkItemOperation(
                new CreateWorkItemRequest(
                    "Queued item", "Body", initialStatus, "P1",
                    AutomaticExecutionAllowed: automaticExecutionAllowed,
                    AgentPolicy: "claude"),
                false),
            CancellationToken.None);
        var service = new TrackerService(new TrackerBackendRegistry([backend]));
        return (service, backend, config, created.Id);
    }

    public void Dispose()
    {
        if (Directory.Exists(directory))
            Directory.Delete(directory, true);
    }

    private sealed class FakeIdentity(string identity = "queue-test")
        : IInstallationIdentityProvider
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

    private sealed class ApprovalTrackingBackend(
        ITrackerBackend inner,
        IDictionary<WorkItemId, bool> approvals)
        : DelegatingTrackerBackend(inner)
    {
        public int ApprovalCycles { get; private set; }

        public Exception? ApprovalException { get; init; }

        public override async Task<IReadOnlyList<WorkItemSummary>> ListAsync(
            TrackerConfig config,
            ListWorkItemsRequest request,
            CancellationToken cancellationToken) =>
            (await base.ListAsync(config, request, cancellationToken))
            .Select(ProjectApproval)
            .ToArray();

        public override async Task<WorkItemDetail?> GetAsync(
            TrackerConfig config,
            WorkItemId id,
            CancellationToken cancellationToken)
        {
            var detail = await base.GetAsync(config, id, cancellationToken);
            return detail is null
                ? null
                : detail with
                {
                    ContextApprovalFieldApproved = approvals.TryGetValue(id, out var approved)
                        ? approved
                        : null
                };
        }

        public override Task CycleContextApprovalAsync(
            TrackerConfig config,
            WorkItemId id,
            CancellationToken cancellationToken)
        {
            ApprovalCycles++;
            if (ApprovalException is not null)
                throw ApprovalException;
            approvals[id] = true;
            return Task.CompletedTask;
        }

        private WorkItemSummary ProjectApproval(WorkItemSummary item) => item with
        {
            ContextApprovalFieldApproved = approvals.TryGetValue(item.Id, out var approved)
                ? approved
                : null
        };
    }
}
