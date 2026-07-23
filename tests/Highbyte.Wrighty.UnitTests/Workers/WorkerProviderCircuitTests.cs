using Highbyte.Wrighty.AgentContext;
using Highbyte.Wrighty.Backends;
using Highbyte.Wrighty.Caching;
using Highbyte.Wrighty.Claims;
using Highbyte.Wrighty.Configuration;
using Highbyte.Wrighty.Identity;
using Highbyte.Wrighty.LocalMarkdown;
using Highbyte.Wrighty.Models;
using Highbyte.Wrighty.Processes;
using Highbyte.Wrighty.Time;
using Highbyte.Wrighty.Workers;

namespace Highbyte.Wrighty.UnitTests.Workers;

public sealed class WorkerProviderCircuitTests : IDisposable
{
    private readonly string directory = Path.Combine(
        Path.GetTempPath(), $"wrighty-provider-worker-{Guid.NewGuid():N}");
    private readonly FakeClock clock =
        new(new DateTimeOffset(2026, 7, 23, 18, 0, 0, TimeSpan.Zero));

    [Fact]
    public async Task Open_provider_blocks_fresh_item_before_claim_or_spawn()
    {
        var (backend, config, created) = await CreateItemAsync("Blocked fresh work");
        var providerStore = Store();
        await providerStore.OpenAsync(
            "claude",
            "Usage limit reached.",
            clock.UtcNow.AddHours(2),
            AgentFailureConfidence.Authoritative,
            clock.UtcNow,
            CancellationToken.None);
        var runner = new FailIfRunRunner();
        var events = new List<WorkerEvent>();
        var worker = Worker(backend, runner, config, providerStore);

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

        Assert.Equal(new WorkerRunSummary(0), summary);
        Assert.Equal(0, runner.Calls);
        Assert.Equal("Todo",
            (await backend.GetAsync(config, created, CancellationToken.None))?.Status);
        Assert.Equal(
            ClaimOwnershipState.Unclaimed,
            (await backend.GetClaimOwnershipAsync(
                config, created, CancellationToken.None)).State);
        var unavailable = Assert.Single(
            events, value => value.Type == "provider-unavailable");
        Assert.Equal(ProviderAvailabilityState.UnavailableUntil,
            unavailable.ProviderAvailability?.State);
        var noItem = Assert.Single(events, value => value.Type == "no-item");
        Assert.Equal(1, noItem.Candidates?.ProviderUnavailable);
    }

    [Fact]
    public async Task Preflight_reports_structured_provider_unavailable_event()
    {
        var (backend, config, _) = await CreateItemAsync("Blocked during preflight");
        var providerStore = Store();
        await providerStore.OpenAsync(
            "copilot",
            "Usage limit reached.",
            clock.UtcNow.AddHours(2),
            AgentFailureConfidence.Authoritative,
            clock.UtcNow,
            CancellationToken.None);
        var events = new List<WorkerEvent>();
        var worker = new WorkerService(
            new TrackerService(new TrackerBackendRegistry([backend])),
            new FailIfRunRunner(),
            new CurrentWorkspace(),
            [new ClaudeAgentAdapter(), new CopilotAgentAdapter()],
            clock: () => clock.UtcNow,
            providerAvailabilityStore: providerStore);

        var ready = await worker.PreflightAsync(
            config,
            Options() with { Agent = "copilot" },
            directory,
            value =>
            {
                events.Add(value);
                return Task.CompletedTask;
            },
            CancellationToken.None);

        Assert.False(ready);
        var unavailable = Assert.Single(
            events, value => value.Type == "provider-unavailable");
        Assert.Equal("copilot", unavailable.Agent);
        Assert.Equal(
            ProviderAvailabilityState.UnavailableUntil,
            unavailable.ProviderAvailability?.State);
        var noItem = Assert.Single(events, value => value.Type == "no-item");
        Assert.Equal(1, noItem.Candidates?.ProviderUnavailable);
    }

    [Fact]
    public async Task Preflight_reports_active_probe_lease_for_due_retry()
    {
        var (backend, config, created) = await CreateItemAsync("Due retry behind probe lease");
        var providerStore = Store();
        await Worker(
                backend,
                new UsageFailureRunner(),
                config,
                providerStore)
            .RunAsync(
                config,
                Options(),
                directory,
                _ => Task.CompletedTask,
                CancellationToken.None);
        var session = await backend.GetAgentSessionAsync(
            config, created, CancellationToken.None);
        clock.UtcNow = session!.Dispatch!.NotBefore;
        var lease = await providerStore.TryAcquireProbeAsync(
            "claude",
            clock.UtcNow,
            TimeSpan.FromMinutes(2),
            CancellationToken.None);
        var events = new List<WorkerEvent>();

        var ready = await Worker(
                backend,
                new FailIfRunRunner(),
                config,
                providerStore)
            .PreflightAsync(
                config,
                Options(),
                directory,
                value =>
                {
                    events.Add(value);
                    return Task.CompletedTask;
                },
                CancellationToken.None);

        Assert.NotNull(lease);
        Assert.False(ready);
        var unavailable = Assert.Single(
            events, value => value.Type == "provider-unavailable");
        Assert.Equal(ProviderAvailabilityState.ProbeDue,
            unavailable.ProviderAvailability?.State);
        var noItem = Assert.Single(events, value => value.Type == "no-item");
        Assert.Equal(1, noItem.Candidates?.ProviderUnavailable);
    }

    [Fact]
    public async Task Usage_failure_opens_provider_until_the_item_retry_time()
    {
        var (backend, config, created) = await CreateItemAsync("Open provider circuit");
        var providerStore = Store();
        var worker = Worker(
            backend,
            new UsageFailureRunner(clock.UtcNow.AddHours(2)),
            config,
            providerStore);
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

        Assert.Equal(new WorkerRunSummary(1), summary);
        var scheduled = Assert.Single(events, value => value.Type == "retry-scheduled");
        var unavailable = Assert.Single(
            events, value => value.Type == "provider-unavailable");
        var persisted = await providerStore.GetAsync("claude", CancellationToken.None);
        Assert.Equal(scheduled.Dispatch?.NotBefore, persisted?.UnavailableUntil);
        Assert.Equal(scheduled.Dispatch?.NotBefore,
            unavailable.ProviderAvailability?.UnavailableUntil);
        Assert.Equal(1, persisted?.ConsecutiveFailures);
        Assert.Equal(
            WorkerDispatchStates.RetryScheduled,
            (await backend.GetAsync(config, created, CancellationToken.None))?.WorkerState);
    }

    [Fact]
    public async Task Due_retained_retry_is_the_single_probe_and_success_closes_provider()
    {
        var (backend, config, created) = await CreateItemAsync("Recover provider circuit");
        var providerStore = Store();
        var schedulingWorker = Worker(
            backend,
            new UsageFailureRunner(),
            config,
            providerStore);
        await schedulingWorker.RunAsync(
            config,
            Options(),
            directory,
            _ => Task.CompletedTask,
            CancellationToken.None);
        var session = await backend.GetAgentSessionAsync(
            config, created, CancellationToken.None);
        clock.UtcNow = session!.Dispatch!.NotBefore;
        var runner = new SuccessfulRunner();
        var events = new List<WorkerEvent>();
        var recoveryWorker = Worker(backend, runner, config, providerStore);

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
        Assert.Equal(1, runner.Calls);
        Assert.Contains(events, value => value.Type == "retry-due");
        Assert.Contains(events, value => value.Type == "retry-started");
        var availability = await providerStore.GetAsync(
            "claude", CancellationToken.None);
        Assert.Equal(ProviderAvailabilityState.Available, availability?.State);
        Assert.Equal(0, availability?.ConsecutiveFailures);
    }

    [Fact]
    public async Task Due_read_only_probe_can_extend_circuit_without_spawning_agent()
    {
        var (backend, config, created) = await CreateItemAsync("Extend provider circuit");
        var providerStore = Store();
        await Worker(
                backend,
                new UsageFailureRunner(),
                config,
                providerStore)
            .RunAsync(
                config,
                Options(),
                directory,
                _ => Task.CompletedTask,
                CancellationToken.None);
        var session = await backend.GetAgentSessionAsync(
            config, created, CancellationToken.None);
        clock.UtcNow = session!.Dispatch!.NotBefore;
        var runner = new FailIfRunRunner();
        var probeFailure = new AgentFailure(
            AgentFailureKind.UsageExhausted,
            "usage_limit_reached",
            clock.UtcNow.AddHours(1),
            null,
            true,
            AgentFailureConfidence.Authoritative,
            "Usage is still unavailable.");
        var probe = new UnavailableProbe(clock.UtcNow, probeFailure);
        var events = new List<WorkerEvent>();
        var worker = Worker(backend, runner, config, providerStore, [probe]);

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

        Assert.Equal(new WorkerRunSummary(0), summary);
        Assert.Equal(1, probe.Calls);
        Assert.Equal(0, runner.Calls);
        Assert.DoesNotContain(events, value => value.Type == "retry-started");
        var availability = await providerStore.GetAsync(
            "claude", CancellationToken.None);
        Assert.Equal(ProviderAvailabilityState.UnavailableUntil, availability?.State);
        Assert.True(availability?.UnavailableUntil > clock.UtcNow.AddHours(1));
        Assert.Equal(2, availability?.ConsecutiveFailures);
    }

    private JsonProviderAvailabilityStore Store() =>
        new(new CachePaths(Path.Combine(directory, "cache")));

    private WorkerService Worker(
        LocalMarkdownTrackerBackend backend,
        IAgentProcessRunner runner,
        TrackerConfig config,
        IProviderAvailabilityStore providerStore,
        IEnumerable<IAgentCapacityProbe>? capacityProbes = null) =>
        new(
            new TrackerService(new TrackerBackendRegistry([backend])),
            runner,
            new CurrentWorkspace(),
            [new ClaudeAgentAdapter()],
            clock: () => clock.UtcNow,
            providerAvailabilityStore: providerStore,
            capacityProbes: capacityProbes);

    private async Task<(LocalMarkdownTrackerBackend Backend, TrackerConfig Config,
        WorkItemId Created)> CreateItemAsync(string title)
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
                UsageFailure = new WorkerUsageFailureConfig
                {
                    InitialRetryMinutes = 1,
                    MaxAttempts = 3,
                    ResetGraceMinutes = 0
                }
            }
        };
        await backend.InitializeAsync(config, false, CancellationToken.None);
        var created = await backend.CreateAsync(
            config,
            new CreateWorkItemOperation(
                new CreateWorkItemRequest(
                    title,
                    "Body",
                    "Todo",
                    "P1",
                    AutomationEligible: true,
                    PreferredAgent: "claude"),
                false),
            CancellationToken.None);
        return (backend, config, created.Id);
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
        if (Directory.Exists(directory))
            Directory.Delete(directory, true);
    }

    private sealed class FakeIdentity : IWorkerIdentityProvider
    {
        public Task<string> GetIdentityAsync(CancellationToken cancellationToken) =>
            Task.FromResult("provider-worker-test");
    }

    private sealed class FakeClock(DateTimeOffset value) : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = value;
    }

    private sealed class CurrentWorkspace : IWorkspaceManager
    {
        public Task<Workspace> PrepareAsync(
            WorkspaceRequest request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new Workspace(Path.GetFullPath(request.RepositoryPath)));
    }

    private sealed class FailIfRunRunner : IAgentProcessRunner
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
            throw new Xunit.Sdk.XunitException("The provider process must not be started.");
        }
    }

    private sealed class SuccessfulRunner : IAgentProcessRunner
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
            var arguments = invocation.Arguments.ToList();
            var resume = arguments.IndexOf("--resume");
            var sessionId = resume >= 0 ? arguments[resume + 1] : "session-from-output";
            return Task.FromResult(new AgentRunResult(
                AgentOutcome.Succeeded,
                sessionId,
                "Provider capacity is available."));
        }
    }

    private sealed class UsageFailureRunner(DateTimeOffset? retryAt = null)
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
            var arguments = invocation.Arguments.ToList();
            var sessionMarker = arguments.IndexOf("--session-id");
            var resumeMarker = arguments.IndexOf("--resume");
            var sessionId = sessionMarker >= 0
                ? arguments[sessionMarker + 1]
                : resumeMarker >= 0
                    ? arguments[resumeMarker + 1]
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

    private sealed class UnavailableProbe(
        DateTimeOffset observedAt,
        AgentFailure failure) : IAgentCapacityProbe
    {
        public string AgentType => "claude";
        public int Calls { get; private set; }

        public Task<AgentCapacityProbeResult?> ProbeAsync(
            AgentCapacityProbeRequest request,
            CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult<AgentCapacityProbeResult?>(
                new AgentCapacityProbeResult(false, failure, observedAt));
        }
    }
}
