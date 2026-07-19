using Highbyte.Wrighty.Processes;
using Highbyte.Wrighty.Errors;
using Highbyte.Wrighty.Workers;

namespace Highbyte.Wrighty.UnitTests.Workers;

public sealed class AgentProcessRunnerTests
{
    [Fact]
    public async Task Successful_process_captures_session_and_merges_environment()
    {
        if (OperatingSystem.IsWindows() || !File.Exists("/bin/sh"))
            return;
        var runner = new AgentProcessRunner(new FixedExecutableResolver("/bin/sh"));
        var invocation = new AgentInvocation(
            "codex",
            [
                "-c",
                """
                printf '{"type":"thread.started","thread_id":"%s"}\n' "$SESSION_FROM_GRANT"
                printf '{"type":"turn.completed"}\n'
                """
            ],
            Path.GetTempPath(),
            new Dictionary<string, string> { ["INVOCATION_VALUE"] = "present" });
        string? announced = null;

        var result = await runner.RunAsync(
            invocation,
            new CodexAgentAdapter(),
            TimeSpan.FromSeconds(5),
            new Dictionary<string, string> { ["SESSION_FROM_GRANT"] = "thread-42" },
            (session, _) =>
            {
                announced = session;
                return Task.CompletedTask;
            },
            true,
            CancellationToken.None);

        Assert.Equal(AgentOutcome.Succeeded, result.Outcome);
        Assert.Equal("thread-42", result.SessionId);
        Assert.Equal("thread-42", announced);
    }

    [Fact]
    public async Task Timeout_kills_process_tree_and_returns_timed_out()
    {
        if (OperatingSystem.IsWindows() || !File.Exists("/bin/sh"))
            return;
        var runner = new AgentProcessRunner(new FixedExecutableResolver("/bin/sh"));
        var invocation = new AgentInvocation(
            "claude",
            ["-c", "sleep 30"],
            Path.GetTempPath(),
            new Dictionary<string, string>());

        var result = await runner.RunAsync(
            invocation,
            new ClaudeAgentAdapter(),
            TimeSpan.FromMilliseconds(100),
            new Dictionary<string, string>(),
            null,
            true,
            CancellationToken.None);

        Assert.Equal(AgentOutcome.TimedOut, result.Outcome);
        Assert.Contains("item timeout", result.FinalMessage);
    }

    [Fact]
    public async Task Cancellation_kills_process_and_returns_rejected()
    {
        if (OperatingSystem.IsWindows() || !File.Exists("/bin/sh"))
            return;
        var runner = new AgentProcessRunner(new FixedExecutableResolver("/bin/sh"));
        var invocation = new AgentInvocation(
            "claude",
            ["-c", "sleep 30"],
            Path.GetTempPath(),
            new Dictionary<string, string>());
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        var result = await runner.RunAsync(
            invocation,
            new ClaudeAgentAdapter(),
            TimeSpan.FromSeconds(10),
            new Dictionary<string, string>(),
            null,
            true,
            cancellation.Token);

        Assert.Equal(AgentOutcome.Rejected, result.Outcome);
        Assert.Contains("fenced or cancelled", result.FinalMessage);
    }

    [Fact]
    public async Task Missing_executable_is_reported_as_start_failure()
    {
        var runner = new AgentProcessRunner(
            new ThrowingExecutableResolver(new FileNotFoundException("missing")));
        var invocation = new AgentInvocation(
            "claude",
            [],
            Path.GetTempPath(),
            new Dictionary<string, string>());

        var exception = await Assert.ThrowsAsync<TrackerException>(
            () => runner.RunAsync(
                invocation,
                new ClaudeAgentAdapter(),
                TimeSpan.FromSeconds(1),
                new Dictionary<string, string>(),
                null,
                true,
                CancellationToken.None));

        Assert.Equal("AGENT_START_FAILED", exception.Code);
        Assert.Contains("missing", exception.Message);
    }

    [Fact]
    public async Task Nonpositive_timeout_is_rejected_before_resolution()
    {
        var runner = new AgentProcessRunner(
            new ThrowingExecutableResolver(new InvalidOperationException()));

        var exception = await Assert.ThrowsAsync<TrackerException>(
            () => runner.RunAsync(
                new AgentInvocation(
                    "claude", [], Path.GetTempPath(), new Dictionary<string, string>()),
                new ClaudeAgentAdapter(),
                TimeSpan.Zero,
                new Dictionary<string, string>(),
                null,
                true,
                CancellationToken.None));

        Assert.Equal("ARGUMENT_INVALID", exception.Code);
    }

    [Fact]
    public async Task Rejected_vendor_output_includes_standard_error_diagnostic()
    {
        if (OperatingSystem.IsWindows() || !File.Exists("/bin/sh"))
            return;

        var runner = new AgentProcessRunner(new FixedExecutableResolver("/bin/sh"));
        var invocation = new AgentInvocation(
            "claude",
            ["-c", "printf 'Session ID already exists' >&2; exit 23"],
            Path.GetTempPath(),
            new Dictionary<string, string>());

        var result = await runner.RunAsync(
            invocation,
            new ClaudeAgentAdapter(),
            TimeSpan.FromSeconds(5),
            new Dictionary<string, string>(),
            null,
            true,
            CancellationToken.None);

        Assert.Equal(AgentOutcome.Rejected, result.Outcome);
        Assert.Equal(23, result.ExitCode);
        Assert.Contains("Claude returned invalid JSON", result.FinalMessage);
        Assert.Contains("stderr: Session ID already exists", result.FinalMessage);
    }

    private sealed class FixedExecutableResolver(string path) : IExecutableResolver
    {
        public string Resolve(string executableName) => path;
    }

    private sealed class ThrowingExecutableResolver(Exception exception) : IExecutableResolver
    {
        public string Resolve(string executableName) => throw exception;
    }
}
