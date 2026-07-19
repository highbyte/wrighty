using Highbyte.Wrighty.Errors;
using Highbyte.Wrighty.Processes;
using Highbyte.Wrighty.Workers;

namespace Highbyte.Wrighty.UnitTests.Workers;

public sealed class WorkerServiceCheckTests
{
    private static readonly IAgentAdapter[] Adapters =
    [
        new ClaudeAgentAdapter(),
        new CodexAgentAdapter(),
        new CopilotAgentAdapter()
    ];

    [Fact]
    public async Task Check_without_selection_probes_every_adapter()
    {
        var runner = new SuccessfulCheckRunner();
        var events = new List<WorkerEvent>();
        var service = Service(runner, new RecordingResolver());

        await service.CheckAsync(
            null,
            Path.GetTempPath(),
            value =>
            {
                events.Add(value);
                return Task.CompletedTask;
            },
            CancellationToken.None);

        Assert.Equal(["claude", "codex", "copilot"], events.Select(value => value.Agent));
        Assert.Equal(3, runner.Invocations.Count);
        Assert.All(events, value => Assert.Contains("session=", value.Message));
    }

    [Fact]
    public async Task Check_with_selection_only_probes_requested_adapter()
    {
        var runner = new SuccessfulCheckRunner();
        var events = new List<WorkerEvent>();

        await Service(runner, new RecordingResolver()).CheckAsync(
            " CODEX ",
            Path.GetTempPath(),
            value =>
            {
                events.Add(value);
                return Task.CompletedTask;
            },
            CancellationToken.None);

        Assert.Equal("codex", Assert.Single(events).Agent);
        Assert.Equal("codex", Assert.Single(runner.Invocations).Executable);
    }

    [Fact]
    public async Task Check_rejects_unknown_agent_or_missing_check_services()
    {
        var unknown = await Assert.ThrowsAsync<TrackerException>(
            () => Service(new SuccessfulCheckRunner(), new RecordingResolver()).CheckAsync(
                "other", Path.GetTempPath(), _ => Task.CompletedTask, CancellationToken.None));
        Assert.Equal("AGENT_UNSUPPORTED", unknown.Code);

        var unavailable = await Assert.ThrowsAsync<TrackerException>(
            () => new WorkerService(
                    null!, new SuccessfulCheckRunner(), null!, Adapters)
                .CheckAsync(
                    "claude", Path.GetTempPath(), _ => Task.CompletedTask,
                    CancellationToken.None));
        Assert.Equal("WORKER_UNAVAILABLE", unavailable.Code);
    }

    [Theory]
    [InlineData(AgentOutcome.Failed, "session")]
    [InlineData(AgentOutcome.Succeeded, null)]
    [InlineData(AgentOutcome.Succeeded, "wrong-claude-session")]
    public async Task Check_rejects_failed_or_invalid_probe_results(
        AgentOutcome outcome,
        string? sessionId)
    {
        var runner = new FixedResultRunner(
            new AgentRunResult(outcome, sessionId, null));

        var exception = await Assert.ThrowsAsync<TrackerException>(
            () => Service(runner, new RecordingResolver()).CheckAsync(
                "claude", Path.GetTempPath(), _ => Task.CompletedTask,
                CancellationToken.None));

        Assert.Equal("AGENT_CHECK_FAILED", exception.Code);
        Assert.Equal("claude", exception.Details["agent"]);
    }

    private static WorkerService Service(
        IAgentProcessRunner runner,
        IExecutableResolver resolver) =>
        new(null!, runner, null!, Adapters, executables: resolver);

    private sealed class SuccessfulCheckRunner : IAgentProcessRunner
    {
        public List<AgentInvocation> Invocations { get; } = [];

        public Task<AgentRunResult> RunAsync(
            AgentInvocation invocation,
            IAgentAdapter adapter,
            TimeSpan timeout,
            IReadOnlyDictionary<string, string> grantEnvironment,
            Func<string, CancellationToken, Task>? sessionStarted,
            bool killOnCancellation,
            CancellationToken cancellationToken)
        {
            Invocations.Add(invocation);
            var sessionId = adapter.AgentType == "claude"
                ? invocation.Arguments[
                    invocation.Arguments.ToList().IndexOf("--session-id") + 1]
                : $"session-{adapter.AgentType}";
            return Task.FromResult(
                new AgentRunResult(AgentOutcome.Succeeded, sessionId, "OK"));
        }
    }

    private sealed class FixedResultRunner(AgentRunResult result) : IAgentProcessRunner
    {
        public Task<AgentRunResult> RunAsync(
            AgentInvocation invocation,
            IAgentAdapter adapter,
            TimeSpan timeout,
            IReadOnlyDictionary<string, string> grantEnvironment,
            Func<string, CancellationToken, Task>? sessionStarted,
            bool killOnCancellation,
            CancellationToken cancellationToken) =>
            Task.FromResult(result);
    }

    private sealed class RecordingResolver : IExecutableResolver
    {
        public string Resolve(string executableName) => $"/tools/{executableName}";
    }
}
