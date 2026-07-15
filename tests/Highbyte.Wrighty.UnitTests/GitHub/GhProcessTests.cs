using Highbyte.Wrighty.Errors;
using Highbyte.Wrighty.GitHub;
using Highbyte.Wrighty.Processes;

namespace Highbyte.Wrighty.UnitTests.GitHub;

public sealed class GhProcessTests
{
    [Fact]
    public async Task Run_reports_the_existing_not_found_error_when_resolution_fails()
    {
        var process = new GhProcess(new MissingExecutableResolver());

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
}
