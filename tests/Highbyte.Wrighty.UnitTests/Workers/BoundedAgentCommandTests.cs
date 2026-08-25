using Highbyte.Wrighty.Processes;
using Highbyte.Wrighty.Workers;

namespace Highbyte.Wrighty.UnitTests.Workers;

public sealed class BoundedAgentCommandTests
{
    [Fact]
    public async Task Captures_successful_output_and_error_with_structured_arguments()
    {
        if (OperatingSystem.IsWindows())
            return;
        var command = new BoundedAgentCommand(new ShellResolver());

        var result = await command.RunAsync(
            "vendor",
            ["-c", "printf result; printf diagnostic >&2"],
            1024,
            TimeSpan.FromSeconds(5),
            CancellationToken.None);

        Assert.Equal(BoundedAgentCommandStatus.Completed, result.Status);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("result", result.StandardOutput);
        Assert.Equal("diagnostic", result.StandardError);
    }

    [Fact]
    public async Task Missing_executable_is_an_ordinary_result()
    {
        var result = await new BoundedAgentCommand(new MissingResolver()).RunAsync(
            "vendor",
            [],
            1024,
            TimeSpan.FromSeconds(1),
            CancellationToken.None);

        Assert.Equal(BoundedAgentCommandStatus.NotInstalled, result.Status);
    }

    [Fact]
    public async Task Output_over_the_byte_budget_is_drained_but_not_retained()
    {
        if (OperatingSystem.IsWindows())
            return;
        var result = await new BoundedAgentCommand(new ShellResolver()).RunAsync(
            "vendor",
            ["-c", "printf 123456789"],
            4,
            TimeSpan.FromSeconds(5),
            CancellationToken.None);

        Assert.Equal(BoundedAgentCommandStatus.OutputTooLarge, result.Status);
        Assert.Empty(result.StandardOutput);
    }

    [Fact]
    public async Task Timeout_kills_the_process_and_returns_a_bounded_failure()
    {
        if (OperatingSystem.IsWindows())
            return;
        var result = await new BoundedAgentCommand(new ShellResolver()).RunAsync(
            "vendor",
            ["-c", "sleep 5"],
            1024,
            TimeSpan.FromMilliseconds(20),
            CancellationToken.None);

        Assert.Equal(BoundedAgentCommandStatus.TimedOut, result.Status);
    }

    [Fact]
    public async Task Caller_cancellation_propagates_instead_of_becoming_a_timeout()
    {
        if (OperatingSystem.IsWindows())
            return;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(20));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new BoundedAgentCommand(new ShellResolver()).RunAsync(
                "vendor",
                ["-c", "sleep 5"],
                1024,
                TimeSpan.FromSeconds(5),
                cancellation.Token));
    }

    private sealed class ShellResolver : IExecutableResolver
    {
        public string Resolve(string executableName) => "/bin/sh";
    }

    private sealed class MissingResolver : IExecutableResolver
    {
        public string Resolve(string executableName) => throw new FileNotFoundException();
    }
}
