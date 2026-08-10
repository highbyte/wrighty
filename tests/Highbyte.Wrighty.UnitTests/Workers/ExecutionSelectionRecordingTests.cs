using Highbyte.Wrighty;
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
/// A recorded session must be able to show what it was launched with. Without this the "a resumed
/// session keeps its original selection" guarantee is only structural — resume cannot apply a new
/// profile because the parameter does not exist on those methods — and nothing says what the
/// original actually was.
/// </summary>
public sealed class ExecutionSelectionRecordingTests : IDisposable
{
    private readonly string directory =
        Path.Combine(Path.GetTempPath(), $"wrighty-selection-{Guid.NewGuid():N}");
    private readonly FakeClock clock = new(DateTimeOffset.Parse("2026-08-09T09:00:00Z"));

    private async Task<(LocalMarkdownTrackerBackend Backend, TrackerConfig Config, WorkItemId Id)>
        SeedAsync(WorkerConfig worker)
    {
        Directory.CreateDirectory(directory);
        var backend = new LocalMarkdownTrackerBackend(new FakeIdentity(), clock);
        var config = new TrackerConfig
        {
            Backend = "local-markdown",
            DefaultPickFrom = "Todo",
            Worker = worker,
            SourcePath = Path.Combine(directory, ".wrighty.json"),
            LocalMarkdown = new LocalMarkdownBackendConfig(),
            LeaseMinutes = 60
        };
        await backend.InitializeAsync(config, false, CancellationToken.None);
        var created = await backend.CreateAsync(config, new CreateWorkItemOperation(
            new CreateWorkItemRequest("Profiled item", "Body", "Todo", "P1",
                AutomaticExecutionAllowed: true, AgentPolicy: "claude"), false),
            CancellationToken.None);
        return (backend, config, created.Id);
    }

    private static WorkerOptions Options(string? profile = null) =>
        new("claude", true, null, WorkspaceMode.Current, new Dictionary<string, string>(),
            null, TimeSpan.FromMinutes(10), FencedAction.Kill, null, "agent", false, false,
            Profile: profile);

    [Fact]
    public async Task A_fresh_launch_records_what_it_asked_the_vendor_for()
    {
        var (backend, config, id) = await SeedAsync(new WorkerConfig
        {
            UseWorkerQueue = false,
            ExecutionProfiles = ["economy", "balanced", "deep"]
        });
        var worker = new WorkerService(
            new TrackerService(new TrackerBackendRegistry([backend])),
            new SessionReportingRunner("session-1"),
            new CurrentWorkspace(),
            [new ClaudeAgentAdapter()],
            clock: () => clock.UtcNow,
            agentVersions: new StubVersionProbe("claude 9.9.9"));

        await worker.RunAsync(
            config, Options("deep"), directory, _ => Task.CompletedTask, CancellationToken.None);

        var session = await backend.GetAgentSessionAsync(config, id, CancellationToken.None);
        var selection = session?.Selection;
        Assert.NotNull(selection);
        Assert.Equal("deep", selection!.Profile);
        Assert.Equal("claude", selection.Agent);
        Assert.Equal(ExecutionEffort.High, selection.Effort);
        // The built-in tiers carry no model, so the vendor's own default applied.
        Assert.Null(selection.Model);
        Assert.Equal(ExecutionProfileSource.CommandLine, selection.Source);
        Assert.Equal(ExecutionMappingSource.BuiltIn, selection.MappingSource);
        // The version stamp is the whole point of recording: it separates "this mapping was always
        // wrong" from "the vendor changed underneath it".
        Assert.Equal("claude 9.9.9", selection.CliVersion);
        Assert.Equal(clock.UtcNow, selection.ResolvedAt);
    }

    [Fact]
    public async Task The_storage_layer_round_trips_a_selection()
    {
        // Isolates persistence from the worker: if this passes and the launch test does not, the
        // gap is in the wiring, not the store.
        var (backend, config, id) = await SeedAsync(new WorkerConfig { UseWorkerQueue = false });
        var context = new AgentExecutionContext("claude", "session-x",
            AgentContextSource.ExplicitOption,
            ClaimantKind: ClaimantKind.Agent, ClaimantId: "agent:test");
        var claim = await backend.TryClaimAsync(config, id, context, CancellationToken.None);
        await backend.RenewClaimAsync(
            config, id, new ClaimHandle(context, claim.ClaimToken), directory, "session-x",
            null, CancellationToken.None);

        await backend.RecordExecutionSelectionAsync(
            config, id,
            new ExecutionSelection("deep", "claude", null, ExecutionEffort.High,
                ExecutionProfileSource.CommandLine, "claude 9.9.9", clock.UtcNow),
            CancellationToken.None);

        var selection = (await backend.GetAgentSessionAsync(
            config, id, CancellationToken.None))?.Selection;
        Assert.NotNull(selection);
        Assert.Equal(ExecutionEffort.High, selection!.Effort);
        Assert.Equal("claude 9.9.9", selection.CliVersion);
    }

    [Fact]
    public async Task A_run_without_any_profile_records_no_selection()
    {
        // Nothing was chosen, so there is nothing to attest to. Writing an empty record would
        // suggest a decision had been made.
        var (backend, config, id) = await SeedAsync(new WorkerConfig { UseWorkerQueue = false });
        var worker = new WorkerService(
            new TrackerService(new TrackerBackendRegistry([backend])),
            new SessionReportingRunner("session-2"),
            new CurrentWorkspace(),
            [new ClaudeAgentAdapter()],
            clock: () => clock.UtcNow,
            agentVersions: new StubVersionProbe("claude 9.9.9"));

        await worker.RunAsync(
            config, Options(), directory, _ => Task.CompletedTask, CancellationToken.None);

        var session = await backend.GetAgentSessionAsync(config, id, CancellationToken.None);
        Assert.Null(session?.Selection);
    }

    [Fact]
    public async Task A_selection_survives_without_a_version_when_none_can_be_read()
    {
        // The version is a best-effort note; a machine that cannot report one still gets the
        // model and effort recorded.
        var (backend, config, id) = await SeedAsync(new WorkerConfig
        {
            UseWorkerQueue = false,
            ExecutionProfiles = ["economy", "balanced", "deep"]
        });
        var worker = new WorkerService(
            new TrackerService(new TrackerBackendRegistry([backend])),
            new SessionReportingRunner("session-3"),
            new CurrentWorkspace(),
            [new ClaudeAgentAdapter()],
            clock: () => clock.UtcNow,
            agentVersions: new StubVersionProbe(null));

        await worker.RunAsync(
            config, Options("economy"), directory, _ => Task.CompletedTask, CancellationToken.None);

        var selection = (await backend.GetAgentSessionAsync(
            config, id, CancellationToken.None))?.Selection;
        Assert.NotNull(selection);
        Assert.Equal(ExecutionEffort.Low, selection!.Effort);
        Assert.Null(selection.CliVersion);
    }

    private sealed class FakeClock(DateTimeOffset value) : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = value;
    }

    private sealed class FakeIdentity : IInstallationIdentityProvider
    {
        public Task<string> GetInstallationIdAsync(CancellationToken cancellationToken) =>
            Task.FromResult("selection-test");
    }

    private sealed class CurrentWorkspace : IWorkspaceManager
    {
        public Task<Workspace> PrepareAsync(
            WorkspaceRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new Workspace(Path.GetFullPath(request.RepositoryPath)));
    }

    private sealed class StubVersionProbe(string? version) : IAgentVersionProbe
    {
        public Task<string?> TryGetVersionAsync(string agent, CancellationToken cancellationToken) =>
            Task.FromResult(version);
    }

    /// <summary>Reports a session id the way a real vendor does, which is what creates the record
    /// the selection attaches to.</summary>
    private sealed class SessionReportingRunner(string sessionId) : IAgentProcessRunner
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
            if (sessionStarted is not null)
                await sessionStarted(sessionId, cancellationToken);
            return new AgentRunResult(AgentOutcome.Succeeded, sessionId, "Needs a decision.");
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(directory))
            Directory.Delete(directory, true);
    }
}
