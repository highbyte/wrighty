using Highbyte.Wrighty.Processes;
using Highbyte.Wrighty.Workers;

namespace Highbyte.Wrighty.UnitTests.Workers;

public sealed class AgentProcessRunnerTests
{
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
}
