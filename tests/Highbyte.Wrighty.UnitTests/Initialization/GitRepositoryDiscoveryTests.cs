using Highbyte.Wrighty.Initialization;

namespace Highbyte.Wrighty.UnitTests.Initialization;

public sealed class GitRepositoryDiscoveryTests
{
    [Theory]
    [InlineData("https://github.com/owner/repo.git", "github.com", "owner/repo")]
    [InlineData("https://user:token@github.com/owner/repo.git", "github.com", "owner/repo")]
    [InlineData("git@github.com:owner/repo.git", "github.com", "owner/repo")]
    [InlineData("ssh://git@github.example.test/owner/repo.git", "github.example.test", "owner/repo")]
    public void Parse_supports_common_remote_forms_without_retaining_credentials(
        string remote,
        string host,
        string repository)
    {
        var result = GitRepositoryDiscovery.Parse(remote);

        Assert.NotNull(result);
        Assert.Equal(host, result.Host);
        Assert.Equal(repository, result.Repository);
        Assert.DoesNotContain("token", result.ToString());
    }

    [Theory]
    [InlineData("file:///tmp/repo")]
    [InlineData("https://github.com/owner")]
    [InlineData("https://github.com/owner/repo/extra")]
    [InlineData("not a remote")]
    public void Parse_rejects_non_remote_or_non_repository_values(string remote)
    {
        Assert.Null(GitRepositoryDiscovery.Parse(remote));
    }

    [Fact]
    public async Task Discover_returns_null_when_git_has_no_selected_remote()
    {
        var discovery = new GitRepositoryDiscovery(
            new FakeGitProcess(new GitProcessResult(1, "", "missing")));

        var result = await discovery.DiscoverAsync("/tmp", "origin", CancellationToken.None);

        Assert.Null(result);
    }

    private sealed class FakeGitProcess(GitProcessResult result) : IGitProcess
    {
        public Task<GitProcessResult> RunAsync(
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken) => Task.FromResult(result);
    }
}
