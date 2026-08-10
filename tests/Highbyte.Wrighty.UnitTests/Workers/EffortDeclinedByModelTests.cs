using Highbyte.Wrighty.AgentContext;
using Highbyte.Wrighty.Backends;
using Highbyte.Wrighty.Claims;
using Highbyte.Wrighty.Configuration;
using Highbyte.Wrighty.Identity;
using Highbyte.Wrighty.LocalMarkdown;
using Highbyte.Wrighty.Models;
using Highbyte.Wrighty.Settings;
using Highbyte.Wrighty.Time;
using Highbyte.Wrighty.Workers;

namespace Highbyte.Wrighty.UnitTests.Workers;

/// <summary>
/// Effort support is a property of the model, not the vendor, and no local check can predict it: a
/// Copilot account resolving to a model without reasoning refuses the argument outright, while the
/// same CLI runs <c>gpt-5.4</c> with it. Since the built-in tiers all carry an effort, leaving that
/// unhandled means the default configuration cannot run at all on such an account.
///
/// The launch fails before any work happens, so the recovery is one cheap relaunch without the
/// argument — reported rather than silent, because on that model every profile behaves identically.
/// </summary>
public sealed class EffortDeclinedByModelTests : IDisposable
{
    private readonly string directory =
        Path.Combine(Path.GetTempPath(), $"wrighty-effort-{Guid.NewGuid():N}");
    private readonly FakeClock clock = new(DateTimeOffset.Parse("2026-08-10T09:00:00Z"));

    private UserSettingsStore Settings => field ??= new UserSettingsStore(
        new UserConfigPaths(Path.Combine(directory, "user-config")));

    // The exact wording GitHub Copilot CLI 1.0.78 writes to stderr, as the process runner presents
    // it once the diagnostic is appended to a rejected run. Pinned verbatim: this string is the
    // only signal Wrighty has, so a test that paraphrased it would prove nothing.
    private const string CopilotRefusal =
        "Copilot returned no result event. stderr: Error: Model \"claude-haiku-4.5\" does not " +
        "support reasoning effort configuration (requested: \"high\").";

    [Theory]
    [InlineData(CopilotRefusal, true)]
    [InlineData("Error: unknown option '--effort'", false)]
    [InlineData("The agent ran out of usage credit.", false)]
    [InlineData(null, false)]
    public void Only_a_model_refusing_effort_is_treated_as_one(string? message, bool expected) =>
        Assert.Equal(expected, EffortRejection.DeclinedByModel(message));

    [Fact]
    public async Task A_model_that_refuses_effort_gets_one_relaunch_without_it()
    {
        var (backend, config, id) = await SeedAsync();
        var runner = new RefusingOnceRunner(CopilotRefusal);
        var events = new List<WorkerEvent>();

        await RunAsync(backend, runner, config, "deep", events);

        // Two launches: the first carried the effort, the second did not. Nothing else changed —
        // a pinned model would be a separate choice and must survive.
        Assert.Equal(2, runner.Invocations.Count);
        Assert.Contains("--effort", runner.Invocations[0]);
        Assert.Contains("high", runner.Invocations[0]);
        Assert.DoesNotContain("--effort", runner.Invocations[1]);
        Assert.Contains("--model", runner.Invocations[1]);
        Assert.Contains("gpt-5.4", runner.Invocations[1]);

        // Reported, not silent: an operator who asked for 'deep' and got an ordinary run deserves
        // to be told the tier made no difference.
        Assert.Contains(events, e => e.Type == "effort-unsupported");

        // Recorded as what actually ran. Storing 'high' here would attest to a request the vendor
        // rejected, which is worse than storing nothing.
        var selection = (await backend.GetAgentSessionAsync(
            config, id, CancellationToken.None))?.Selection;
        Assert.NotNull(selection);
        Assert.Equal("deep", selection!.Profile);
        Assert.Equal("gpt-5.4", selection.Model);
        Assert.Null(selection.Effort);
    }

    [Fact]
    public async Task A_run_that_carries_no_effort_is_never_relaunched()
    {
        // Guards the fallback against becoming a general-purpose retry: with no effort to drop,
        // the second launch would be identical to the first and would simply fail twice.
        var (backend, config, _) = await SeedAsync();
        var runner = new RefusingOnceRunner(CopilotRefusal);

        await RunAsync(backend, runner, config, profile: null, []);

        Assert.Single(runner.Invocations);
    }

    [Fact]
    public async Task A_failure_that_is_not_about_effort_is_left_alone()
    {
        var (backend, config, _) = await SeedAsync();
        var runner = new RefusingOnceRunner("The agent ran out of usage credit.");

        await RunAsync(backend, runner, config, "deep", []);

        Assert.Single(runner.Invocations);
    }

    private async Task RunAsync(
        LocalMarkdownTrackerBackend backend,
        IAgentProcessRunner runner,
        TrackerConfig config,
        string? profile,
        List<WorkerEvent> events)
    {
        var worker = new WorkerService(
            new TrackerService(new TrackerBackendRegistry([backend])),
            runner,
            new CurrentWorkspace(),
            [new CopilotAgentAdapter()],
            clock: () => clock.UtcNow,
            agentVersions: new StubVersionProbe("copilot 1.0.78"),
            userSettings: Settings);

        await worker.RunAsync(
            config,
            new WorkerOptions("copilot", true, null, WorkspaceMode.Current,
                new Dictionary<string, string>(), null, TimeSpan.FromMinutes(10),
                FencedAction.Kill, null, "agent", false, false, Profile: profile),
            directory,
            worker_event => { events.Add(worker_event); return Task.CompletedTask; },
            CancellationToken.None);
    }

    private async Task<(LocalMarkdownTrackerBackend Backend, TrackerConfig Config, WorkItemId Id)>
        SeedAsync()
    {
        Directory.CreateDirectory(directory);
        var backend = new LocalMarkdownTrackerBackend(new FakeIdentity(), clock);
        var config = new TrackerConfig
        {
            Backend = "local-markdown",
            DefaultPickFrom = "Todo",
            Worker = new WorkerConfig
            {
                UseWorkerQueue = false,
                ExecutionProfiles = ["economy", "balanced", "deep"]
            },
            SourcePath = Path.Combine(directory, ".wrighty.json"),
            LocalMarkdown = new LocalMarkdownBackendConfig(),
            LeaseMinutes = 60
        };
        // A pinned model alongside the effort, so the test can prove the relaunch drops only the
        // argument the vendor objected to. User-scoped, like every mapping: the repository agrees
        // on the name 'deep', never on what it resolves to.
        await Settings.SaveAsync(new UserSettings
        {
            WorkerProfiles =
                new Dictionary<string, IReadOnlyDictionary<string, ExecutionProfileMapping>>
                {
                    ["deep"] = new Dictionary<string, ExecutionProfileMapping>
                    {
                        ["copilot"] = new()
                        {
                            Model = "gpt-5.4",
                            Effort = ExecutionEffort.High
                        }
                    }
                }
        }, CancellationToken.None);
        await backend.InitializeAsync(config, false, CancellationToken.None);
        var created = await backend.CreateAsync(config, new CreateWorkItemOperation(
            new CreateWorkItemRequest("Profiled item", "Body", "Todo", "P1",
                AutomaticExecutionAllowed: true, AgentPolicy: "copilot"), false),
            CancellationToken.None);
        return (backend, config, created.Id);
    }

    /// <summary>
    /// Refuses the first launch the way a vendor does — a rejected result, no session id reported —
    /// and lets the second through. Recording every invocation is the point: the assertion is about
    /// which arguments each launch carried, not merely that a retry happened.
    /// </summary>
    private sealed class RefusingOnceRunner(string refusal) : IAgentProcessRunner
    {
        public List<IReadOnlyList<string>> Invocations { get; } = [];

        public async Task<AgentRunResult> RunAsync(
            AgentInvocation invocation,
            IAgentAdapter adapter,
            TimeSpan timeout,
            IReadOnlyDictionary<string, string> grantEnvironment,
            Func<string, CancellationToken, Task>? sessionStarted,
            bool killOnCancellation,
            CancellationToken cancellationToken)
        {
            Invocations.Add(invocation.Arguments);
            if (Invocations.Count == 1)
            {
                // No session id: the launch died before the vendor opened one, which is precisely
                // why the selection recorded at launch has nothing to attach itself to.
                return new AgentRunResult(AgentOutcome.Rejected, null, refusal, 1);
            }

            if (sessionStarted is not null)
                await sessionStarted("session-after-relaunch", cancellationToken);
            return new AgentRunResult(
                AgentOutcome.Succeeded, "session-after-relaunch", "Needs a decision.");
        }
    }

    private sealed class FakeClock(DateTimeOffset value) : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = value;
    }

    private sealed class FakeIdentity : IInstallationIdentityProvider
    {
        public Task<string> GetInstallationIdAsync(CancellationToken cancellationToken) =>
            Task.FromResult("effort-test");
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

    public void Dispose()
    {
        if (Directory.Exists(directory))
            Directory.Delete(directory, true);
    }
}
