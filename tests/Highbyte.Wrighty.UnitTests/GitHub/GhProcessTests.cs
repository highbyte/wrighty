using Highbyte.Wrighty.Errors;
using Highbyte.Wrighty.GitHub;
using Highbyte.Wrighty.Processes;

namespace Highbyte.Wrighty.UnitTests.GitHub;

public sealed class GhProcessTests
{
    [Fact]
    public void Default_constructor_configures_the_path_resolver()
    {
        Assert.IsAssignableFrom<IGhProcess>(new GhProcess());
    }

    [Fact]
    public async Task Run_returns_stdout_stderr_and_exit_code()
    {
        var process = new GhProcess(new ShellExecutableResolver());

        var result = await process.RunAsync(
            ["-c", "printf 'standard output'; printf 'standard error' >&2; exit 7"],
            null,
            CancellationToken.None);

        Assert.Equal(7, result.ExitCode);
        Assert.Equal("standard output", result.StandardOutput);
        Assert.Equal("standard error", result.StandardError);
    }

    [Fact]
    public async Task Run_writes_standard_input_and_closes_the_stream()
    {
        var process = new GhProcess(new ShellExecutableResolver());

        var result = await process.RunAsync(
            ["-c", "cat"],
            "input with spaces\nand a second line",
            CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("input with spaces\nand a second line", result.StandardOutput);
        Assert.Empty(result.StandardError);
    }

    [Fact]
    public async Task Run_preserves_argument_boundaries()
    {
        var process = new GhProcess(new ShellExecutableResolver());

        var result = await process.RunAsync(
            ["-c", "printf '%s' \"$1\"", "ignored-zero", "value with spaces"],
            null,
            CancellationToken.None);

        Assert.Equal("value with spaces", result.StandardOutput);
    }

    [Fact]
    public async Task Run_reports_the_existing_not_found_error_when_resolution_fails()
    {
        var process = new GhProcess(new MissingExecutableResolver());

        var exception = await Assert.ThrowsAsync<TrackerException>(
            () => process.RunAsync([], null, CancellationToken.None));

        Assert.Equal("GH_NOT_FOUND", exception.Code);
        Assert.Equal(4, exception.ExitCode);
    }

    [Fact]
    public async Task Run_maps_process_start_failure_to_not_found_error()
    {
        var process = new GhProcess(new FixedExecutableResolver(
            Path.Combine(Path.GetTempPath(), $"wrighty-missing-{Guid.NewGuid():N}")));

        var exception = await Assert.ThrowsAsync<TrackerException>(
            () => process.RunAsync([], null, CancellationToken.None));

        Assert.Equal("GH_NOT_FOUND", exception.Code);
        Assert.Equal(4, exception.ExitCode);
    }

    private sealed class MissingExecutableResolver : IExecutableResolver
    {
        public string Resolve(string executableName) =>
            throw new FileNotFoundException("missing", executableName);
    }

    private sealed class ShellExecutableResolver : IExecutableResolver
    {
        public string Resolve(string executableName) => "/bin/sh";
    }

    private sealed class FixedExecutableResolver(string path) : IExecutableResolver
    {
        public string Resolve(string executableName) => path;
    }
}
