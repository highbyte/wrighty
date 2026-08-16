using Highbyte.Wrighty.Configuration;
using Highbyte.Wrighty.Initialization;
using Highbyte.Wrighty.Web;

namespace Highbyte.Wrighty.UnitTests.Web;

public sealed class GitHubProjectUrlResolverTests
{
    [Fact]
    public async Task ResolveAsync_returns_and_caches_the_canonical_project_url()
    {
        var lookups = 0;
        var resolver = new GitHubProjectUrlResolver((host, owner, number, _) =>
        {
            lookups++;
            Assert.Equal("github.com", host);
            Assert.Equal("acme", owner);
            Assert.Equal(42, number);
            return Task.FromResult<GitHubProjectInfo?>(new(
                "project-id",
                owner,
                number,
                "Roadmap",
                "https://github.com/orgs/acme/projects/42",
                ["acme/widget"],
                "Organization"));
        });
        var config = GitHubConfig();

        var first = await resolver.ResolveAsync(config, CancellationToken.None);
        var second = await resolver.ResolveAsync(config, CancellationToken.None);

        Assert.Equal("https://github.com/orgs/acme/projects/42", first);
        Assert.Equal(first, second);
        Assert.Equal(1, lookups);
    }

    [Fact]
    public async Task ResolveAsync_rejects_a_project_url_from_another_origin()
    {
        var resolver = new GitHubProjectUrlResolver((_, owner, number, _) =>
            Task.FromResult<GitHubProjectInfo?>(new(
                "project-id",
                owner,
                number,
                "Roadmap",
                "https://example.com/orgs/acme/projects/42",
                ["acme/widget"],
                "Organization")));

        var result = await resolver.ResolveAsync(GitHubConfig(), CancellationToken.None);

        Assert.Null(result);
    }

    private static TrackerConfig GitHubConfig() => new()
    {
        Backend = "github",
        Repository = "acme/widget",
        ProjectOwner = "acme",
        ProjectNumber = 42
    };
}
