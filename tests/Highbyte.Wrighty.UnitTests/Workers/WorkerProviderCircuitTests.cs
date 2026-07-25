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
        await providerStore.RecordUnavailableAsync(
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
        Assert.Equal(ProviderCapacityState.UnavailableUntil,
            unavailable.ProviderCapacity?.State);
        var noItem = Assert.Single(events, value => value.Type == "no-item");
        Assert.Equal(1, noItem.Candidates?.ProviderUnavailable);
    }

    [Fact]
    public async Task Preflight_reports_structured_provider_unavailable_event()
    {
        var (backend, config, _) = await CreateItemAsync("Blocked during preflight");
        var providerStore = Store();
        await providerStore.RecordUnavailableAsync(
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
            providerCapacityStore: providerStore);

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
            ProviderCapacityState.UnavailableUntil,
            unavailable.ProviderCapacity?.State);
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
        Assert.Equal(ProviderCapacityState.ProbeInProgress,
            unavailable.ProviderCapacity?.State);
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
            unavailable.ProviderCapacity?.UnavailableUntil);
        Assert.Equal(1, persisted?.ConsecutiveFailures);
        Assert.Equal(
            DispatchStates.RetryScheduled,
            (await backend.GetAsync(config, created, CancellationToken.None))?.DispatchState);
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
        Assert.Equal(ProviderCapacityState.Available, availability?.State);
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
        Assert.Equal(ProviderCapacityState.UnavailableUntil, availability?.State);
        Assert.True(availability?.UnavailableUntil > clock.UtcNow.AddHours(1));
        Assert.Equal(2, availability?.ConsecutiveFailures);
    }

    [Fact]
    public async Task Explicit_provider_probe_bypasses_timer_and_closes_circuit_without_claiming_item()
    {
        var (backend, config, created) = await CreateItemAsync("Probe provider now");
        var providerStore = Store();
        await providerStore.RecordUnavailableAsync(
            "claude",
            "Usage limit reached.",
            clock.UtcNow.AddHours(2),
            AgentFailureConfidence.Authoritative,
            clock.UtcNow,
            CancellationToken.None);
        var runner = new SuccessfulRunner();
        var events = new List<WorkerEvent>();
        var worker = Worker(backend, runner, config, providerStore);

        var availability = await worker.ProbeProviderAsync(
            config,
            "claude",
            directory,
            value =>
            {
                events.Add(value);
                return Task.CompletedTask;
            },
            CancellationToken.None);

        Assert.Equal(1, runner.Calls);
        Assert.Equal(ProviderCapacityState.Available, availability.State);
        Assert.Contains(events, value => value.Type == "provider-probe-started");
        Assert.Contains(events, value => value.Type == "provider-available");
        Assert.Equal(
            ClaimOwnershipState.Unclaimed,
            (await backend.GetClaimOwnershipAsync(
                config,
                created,
                CancellationToken.None)).State);
        Assert.Equal(
            "Todo",
            (await backend.GetAsync(config, created, CancellationToken.None))?.Status);
    }

    [Fact]
    public async Task Explicit_provider_probe_succeeds_without_an_existing_circuit()
    {
        var (backend, config, created) = await CreateItemAsync("Probe available provider");
        var providerStore = Store();
        var runner = new SuccessfulRunner();
        var events = new List<WorkerEvent>();

        var availability = await Worker(backend, runner, config, providerStore)
            .ProbeProviderAsync(
                config,
                "claude",
                directory,
                value =>
                {
                    events.Add(value);
                    return Task.CompletedTask;
                },
                CancellationToken.None);

        Assert.Equal(1, runner.Calls);
        Assert.Equal(ProviderCapacityState.Available, availability.State);
        Assert.Equal(0, availability.ConsecutiveFailures);
        Assert.Contains(events, value => value.Type == "provider-probe-started");
        Assert.Contains(events, value => value.Type == "provider-available");
        Assert.Equal(
            ClaimOwnershipState.Unclaimed,
            (await backend.GetClaimOwnershipAsync(
                config,
                created,
                CancellationToken.None)).State);
    }

    [Fact]
    public async Task Explicit_provider_probe_opens_circuit_when_available_provider_is_exhausted()
    {
        var (backend, config, _) = await CreateItemAsync("Discover exhausted provider");
        var providerStore = Store();
        var runner = new UsageFailureRunner(clock.UtcNow.AddHours(3));

        var availability = await Worker(backend, runner, config, providerStore)
            .ProbeProviderAsync(
                config,
                "claude",
                directory,
                _ => Task.CompletedTask,
                CancellationToken.None);

        Assert.Equal(ProviderCapacityState.UnavailableUntil, availability.State);
        Assert.Equal(1, availability.ConsecutiveFailures);
        Assert.True(availability.UnavailableUntil > clock.UtcNow.AddHours(3));
    }

    [Fact]
    public async Task Interrupted_proactive_probe_restores_available_state()
    {
        var (backend, config, _) = await CreateItemAsync("Interrupted provider probe");
        var providerStore = Store();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Worker(backend, new ThrowingRunner(), config, providerStore)
                .ProbeProviderAsync(
                    config,
                    "claude",
                    directory,
                    _ => Task.CompletedTask,
                    CancellationToken.None));

        var availability = await providerStore.GetAsync(
            "claude",
            CancellationToken.None);
        Assert.Equal(ProviderCapacityState.Available, availability?.State);
        Assert.Null(availability?.UnavailableUntil);
    }

    [Fact]
    public async Task Explicit_provider_probe_reopens_circuit_when_usage_is_still_exhausted()
    {
        var (backend, config, _) = await CreateItemAsync("Probe exhausted provider");
        var providerStore = Store();
        await providerStore.RecordUnavailableAsync(
            "claude",
            "Usage limit reached.",
            clock.UtcNow.AddHours(2),
            AgentFailureConfidence.Authoritative,
            clock.UtcNow,
            CancellationToken.None);
        var runner = new UsageFailureRunner(clock.UtcNow.AddHours(3));
        var events = new List<WorkerEvent>();
        var worker = Worker(backend, runner, config, providerStore);

        var availability = await worker.ProbeProviderAsync(
            config,
            "claude",
            directory,
            value =>
            {
                events.Add(value);
                return Task.CompletedTask;
            },
            CancellationToken.None);

        Assert.Equal(ProviderCapacityState.UnavailableUntil, availability.State);
        Assert.True(availability.UnavailableUntil > clock.UtcNow.AddHours(3));
        Assert.Equal(2, availability.ConsecutiveFailures);
        Assert.Contains(events, value =>
            value.Type == "provider-unavailable" &&
            value.Failure?.Kind == AgentFailureKind.UsageExhausted);
    }

    [Fact]
    public async Task Concurrent_explicit_provider_probe_observes_existing_lease_without_spawn()
    {
        var (backend, config, _) = await CreateItemAsync("Concurrent provider probe");
        var providerStore = Store();
        await providerStore.RecordUnavailableAsync(
            "claude",
            "Usage limit reached.",
            clock.UtcNow.AddHours(2),
            AgentFailureConfidence.Authoritative,
            clock.UtcNow,
            CancellationToken.None);
        var lease = await providerStore.TryAcquireProbeAsync(
            "claude",
            clock.UtcNow,
            TimeSpan.FromMinutes(2),
            CancellationToken.None,
            allowBeforeUnavailableUntil: true);
        var runner = new FailIfRunRunner();
        var events = new List<WorkerEvent>();

        var availability = await Worker(
                backend,
                runner,
                config,
                providerStore)
            .ProbeProviderAsync(
                config,
                "claude",
                directory,
                value =>
                {
                    events.Add(value);
                    return Task.CompletedTask;
                },
                CancellationToken.None);

        Assert.NotNull(lease);
        Assert.Equal(0, runner.Calls);
        Assert.Equal(ProviderCapacityState.ProbeInProgress, availability.State);
        Assert.Contains(events, value =>
            value.Type == "provider-unavailable" &&
            value.ProviderCapacity?.State == ProviderCapacityState.ProbeInProgress);
    }

    private JsonProviderCapacityStore Store() =>
        new(new CachePaths(Path.Combine(directory, "cache")));

    private WorkerService Worker(
        LocalMarkdownTrackerBackend backend,
        IAgentProcessRunner runner,
        TrackerConfig config,
        IProviderCapacityStore providerStore,
        IEnumerable<IAgentCapacityProbe>? capacityProbes = null) =>
        new(
            new TrackerService(new TrackerBackendRegistry([backend])),
            runner,
            new CurrentWorkspace(),
            [new ClaudeAgentAdapter()],
            clock: () => clock.UtcNow,
            providerCapacityStore: providerStore,
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
                    AutomaticExecutionAllowed: true,
                    AgentPolicy: "claude"),
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

    private sealed class FakeIdentity : IInstallationIdentityProvider
    {
        public Task<string> GetInstallationIdAsync(CancellationToken cancellationToken) =>
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

    private sealed class ThrowingRunner : IAgentProcessRunner
    {
        public Task<AgentRunResult> RunAsync(
            AgentInvocation invocation,
            IAgentAdapter adapter,
            TimeSpan timeout,
            IReadOnlyDictionary<string, string> grantEnvironment,
            Func<string, CancellationToken, Task>? sessionStarted,
            bool killOnCancellation,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Synthetic provider process failure.");
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
        public string Agent => "claude";
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
