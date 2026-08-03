using Highbyte.Wrighty.AgentContext;
using Highbyte.Wrighty.Backends;
using Highbyte.Wrighty.Claims;
using Highbyte.Wrighty.Configuration;
using Highbyte.Wrighty.Identity;
using Highbyte.Wrighty.LocalMarkdown;
using Highbyte.Wrighty.Models;
using Highbyte.Wrighty.Time;
using Highbyte.Wrighty.Workers;

namespace Highbyte.Wrighty.UnitTests.Workers;

/// <summary>
/// The worker queue (on by default, <c>worker.useWorkerQueue</c>): moving an item into the
/// pick-from status — "Agent queue" by default — authorizes automatic execution and moving it out
/// revokes it, while the durable flag stays the source of truth. Never applied to the worker's
/// own status moves.
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
            config, id, WorkItemPatch.StatusOnly("Agent queue"),
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
    public async Task Opting_out_leaves_execution_policy_untouched()
    {
        var (service, backend, config, id) = await SetupAsync(
            initialStatus: "Todo", useWorkerQueue: false);
        var handle = await ClaimAsHumanAsync(backend, config, id);

        await service.UpdateAsync(
            config, id, WorkItemPatch.StatusOnly("Agent queue"),
            expectedRevision: null, handle, CancellationToken.None);

        var detail = await backend.GetAsync(config, id, CancellationToken.None);
        Assert.False(detail!.AutomaticExecutionAllowed);
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
                OptionalValue<string>.From("Agent queue"),
                OptionalValue<string?>.Unspecified,
                AutomaticExecutionAllowed: OptionalValue<bool>.From(false)),
            expectedRevision: null,
            handle,
            CancellationToken.None);

        var detail = await backend.GetAsync(config, id, CancellationToken.None);
        Assert.Equal("Agent queue", detail!.Status);
        Assert.False(detail.AutomaticExecutionAllowed);
    }

    [Fact]
    public async Task Worker_status_moves_never_trigger_the_queue_rule()
    {
        // A refusal-restore moves an item back into pick-from with applyWorkerQueue: false; the
        // restore of a revoked item must not re-authorize it.
        var (service, backend, config, id) = await SetupAsync(initialStatus: "Todo");
        var handle = await ClaimAsHumanAsync(backend, config, id);

        await service.UpdateAsync(
            config,
            id,
            WorkItemPatch.StatusOnly("Agent queue"),
            expectedRevision: null,
            handle,
            CancellationToken.None,
            applyWorkerQueue: false);

        var detail = await backend.GetAsync(config, id, CancellationToken.None);
        Assert.Equal("Agent queue", detail!.Status);
        Assert.False(detail.AutomaticExecutionAllowed);
    }

    [Fact]
    public async Task Poll_authorizes_items_found_in_the_queue_and_announces_it()
    {
        // The board half of the worker queue: an item placed into pick-from outside any Wrighty
        // surface (a GitHub board drag) is authorized by the worker poll.
        var (service, backend, config, id) = await SetupAsync(initialStatus: "Agent queue");
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
    public async Task Poll_leaves_items_untouched_when_opted_out()
    {
        var (service, backend, config, id) = await SetupAsync(
            initialStatus: "Agent queue", useWorkerQueue: false);
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
        var detail = await backend.GetAsync(config, id, CancellationToken.None);
        Assert.False(detail!.AutomaticExecutionAllowed);
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
            bool useWorkerQueue = true)
    {
        var backend = new LocalMarkdownTrackerBackend(new FakeIdentity(), clock);
        // Default statuses and the default pick-from ("Agent queue") are exactly what these tests
        // exercise; only the opt-out cases override the worker section.
        var config = new TrackerConfig
        {
            Backend = "local-markdown",
            SourcePath = Path.Combine(directory, ".wrighty.json"),
            LocalMarkdown = new LocalMarkdownBackendConfig(),
            LeaseMinutes = 60,
            Worker = useWorkerQueue ? null : new WorkerConfig { UseWorkerQueue = false }
        };
        await backend.InitializeAsync(config, false, CancellationToken.None);
        var created = await backend.CreateAsync(
            config,
            new CreateWorkItemOperation(
                new CreateWorkItemRequest(
                    "Queued item", "Body", initialStatus, "P1",
                    AutomaticExecutionAllowed: false, AgentPolicy: "claude"),
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
}
