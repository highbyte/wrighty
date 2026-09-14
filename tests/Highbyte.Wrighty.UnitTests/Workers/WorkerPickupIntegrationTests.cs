using Highbyte.Wrighty.AgentContext;
using Highbyte.Wrighty.Backends;
using Highbyte.Wrighty.Caching;
using Highbyte.Wrighty.Claims;
using Highbyte.Wrighty.Configuration;
using Highbyte.Wrighty.LocalMarkdown;
using Highbyte.Wrighty.Models;
using Highbyte.Wrighty.Processes;
using Highbyte.Wrighty.Workers;

namespace Highbyte.Wrighty.UnitTests.Workers;

public sealed partial class LocalDispatchStateTests
{
    [Theory]
    [InlineData("claude")]
    [InlineData("codex")]
    [InlineData("copilot")]
    [InlineData("opencode")]
    public async Task Pickup_assessment_resolves_item_agent_without_launching_or_mutating(string agent)
    {
        var config = WorkerConfig();
        var backend = new LocalMarkdownTrackerBackend(new FakeIdentity(), clock);
        await backend.InitializeAsync(config, false, default);
        var created = await backend.CreateAsync(config, new CreateWorkItemOperation(
            new("Assess", "Requirements", "Todo", "P1", AutomaticExecutionAllowed: true, AgentPolicy: agent), false), default);
        var tracker = new TrackerService(new TrackerBackendRegistry([backend]));
        var registry = BuiltInAgentRegistry.Create(new PathExecutableResolver());
        var worker = new WorkerService(tracker, new FailIfRunRunner(), new DiscoveryWorkspaces(),
            registry.ExecutionAdapters, clock: () => clock.UtcNow);
        var before = TrackerContents();
        var state = await tracker.GetOperationalAsync(config, created.Id, default);
        var status = PickupRun(config);
        var result = await worker.AssessPickupAsync(config, status, state, "revision", clock.UtcNow, default);
        Assert.Equal("could-pick-up", result.Outcome);
        Assert.Equal(agent, result.Agent);
        Assert.Equal(before, TrackerContents());
        Assert.Equal(ClaimOwnershipState.Unclaimed, (await backend.GetClaimOwnershipAsync(config, created.Id, default)).State);
    }

    [Theory]
    [InlineData("paused", "cannot-pick-up")]
    [InlineData("queued", "could-pick-up")]
    [InlineData("claimed", "cannot-pick-up")]
    [InlineData("provider", "cannot-pick-up")]
    [InlineData("missing-workspace", "unknown")]
    public async Task Pickup_assessment_reuses_retained_session_rules_and_cached_capacity(string scenario, string outcome)
    {
        var (backend, config, id, _) = await CreatePausedItemAsync();
        if (scenario != "paused") await backend.QueuePausedAsync(config, id, default);
        if (scenario == "claimed")
            await backend.TryClaimAsync(config, id, new AgentExecutionContext("codex", null, AgentContextSource.ExplicitOption,
                ClaimantKind: ClaimantKind.Agent, ClaimantId: "agent:another"), default);
        var provider = new JsonProviderCapacityStore(new CachePaths(Path.Combine(directory, "capacity")));
        if (scenario == "provider")
            await provider.RecordUnavailableAsync("codex", "Quota window", clock.UtcNow.AddHours(1),
                AgentFailureConfidence.Authoritative, clock.UtcNow, default);
        var tracker = new TrackerService(new TrackerBackendRegistry([backend]));
        var worker = new WorkerService(tracker, new FailIfRunRunner(), new DiscoveryWorkspaces(),
            [new CodexAgentAdapter()], clock: () => clock.UtcNow, providerCapacityStore: provider);
        var status = PickupRun(config);
        if (scenario == "missing-workspace") status = status with { Instance = status.Instance with
            { Scheduling = status.Instance.Scheduling! with { RepositoryPath = Path.Combine(directory, "missing") } } };
        var before = TrackerContents();
        var result = await worker.AssessPickupAsync(config, status, await tracker.GetOperationalAsync(config, id, default),
            "revision", clock.UtcNow, default);
        Assert.Equal(outcome, result.Outcome);
        if (scenario == "provider") Assert.Equal("PROVIDER_DEFERRED", result.Code);
        Assert.Equal(before, TrackerContents());
    }

    [Fact]
    public async Task Worker_host_records_effective_startup_and_loop_allowance()
    {
        var config = WorkerConfig();
        var backend = new LocalMarkdownTrackerBackend(new FakeIdentity(), clock);
        await backend.InitializeAsync(config, false, default);
        await backend.CreateAsync(config, new CreateWorkItemOperation(
            new("Bounded run", "Body", "Todo", "P1", AutomaticExecutionAllowed: true, AgentPolicy: "claude"), false), default);
        var worker = new WorkerService(new TrackerService(new TrackerBackendRegistry([backend])),
            new CapturingRejectedRunner(), new TrackingWorktree(directory), [new ClaudeAgentAdapter()], clock: () => clock.UtcNow);
        var registry = new ProgressRegistry();
        var host = new WorkerRunHost(worker, registry);
        using var control = new WorkerRunControl();
        var options = Options() with { Once = false, MaxItems = 1, Agent = null, IdleTimeout = TimeSpan.FromSeconds(1) };
        await host.RunAsync(config, options, new(directory, config.SourcePath!, "revision", "unparsed", WorkerHostKind.WebHosted),
            new(null), control, new(_ => Task.CompletedTask, null), default);
        Assert.NotNull(registry.Metadata?.Scheduling);
        Assert.Equal("bounded", registry.Metadata.Scheduling.Mode);
        Assert.Equal(1, registry.Metadata.Scheduling.ItemLimit);
        Assert.Null(registry.Metadata.Scheduling.AgentOverride);
        Assert.Equal([0, 1], registry.Progress.Select(value => value.Processed));
    }

    private WorkerInstanceStatus PickupRun(TrackerConfig config) => new(new("run", 123, "start", clock.UtcNow,
        clock.UtcNow, "scope", "revision", "1", "display only", null, WorkerInstanceState.Idle,
        Scheduling: new("continuous", null, WorkerItemIntent.Auto, config.DefaultPickFrom, config.DefaultPickTo,
            null, "claude", WorkspaceMode.Current, directory, new Dictionary<string, string>(), null, null,
            TimeSpan.FromHours(1), null, false), Progress: new(0, clock.UtcNow)), WorkerInstanceLiveness.Running, null);
    private Dictionary<string, string> TrackerContents() => Directory.GetFiles(directory, "*.md", SearchOption.AllDirectories)
        .Concat(Directory.GetFiles(directory, "state.json", SearchOption.AllDirectories))
        .ToDictionary(path => path, File.ReadAllText);

    private sealed class DiscoveryWorkspaces : IWorkspaceManager
    {
        public Task<Workspace> PrepareAsync(WorkspaceRequest request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Discovery must not prepare a workspace.");
        public Task<bool> CleanupAsync(Workspace workspace, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Discovery must not clean up a workspace.");
    }

    private sealed class ProgressRegistry : IWorkerInstanceRegistry, IWorkerInstanceRegistration
    {
        public WorkerRegistrationMetadata? Metadata { get; private set; }
        public List<WorkerRunProgress> Progress { get; } = [];
        public string RunId => "run";
        public Task<IWorkerInstanceRegistration> RegisterAsync(string configurationPath, string configurationRevision, string invocationSummary, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Expected structured registration.");
        public Task<IWorkerInstanceRegistration> RegisterAsync(string configurationPath, string configurationRevision, string invocationSummary,
            WorkerRegistrationMetadata metadata, CancellationToken cancellationToken)
        {
            Metadata = metadata;
            return Task.FromResult<IWorkerInstanceRegistration>(this);
        }
        public Task<IReadOnlyList<WorkerInstanceStatus>> ListAsync(string configurationPath, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task UpdateAsync(string? currentItemId, WorkerInstanceState state, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task UpdateProgressAsync(WorkerRunProgress progress, CancellationToken cancellationToken)
        {
            Progress.Add(progress);
            return Task.CompletedTask;
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
