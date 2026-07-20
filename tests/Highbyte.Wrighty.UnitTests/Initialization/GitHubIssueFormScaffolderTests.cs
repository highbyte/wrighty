using Highbyte.Wrighty.Configuration;
using Highbyte.Wrighty.Initialization;

namespace Highbyte.Wrighty.UnitTests.Initialization;

public sealed class GitHubIssueFormScaffolderTests
{
    [Fact]
    public async Task Scaffold_creates_one_static_worker_form_per_supported_agent()
    {
        var root = Path.Combine(Path.GetTempPath(), $"wrighty-forms-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var scaffolder = new GitHubIssueFormScaffolder(
                new Discovery(new DiscoveredGitHubRepository("github.com", "owner/repo")),
                new Git(root));

            var actions = await scaffolder.ScaffoldAsync(
                root,
                Config,
                "origin",
                CancellationToken.None);

            var directory = Path.Combine(root, ".github", "ISSUE_TEMPLATE");
            Assert.True(File.Exists(Path.Combine(directory, "wrighty-claude.yml")));
            Assert.True(File.Exists(Path.Combine(directory, "wrighty-codex.yml")));
            Assert.True(File.Exists(Path.Combine(directory, "wrighty-copilot.yml")));
            var codex = await File.ReadAllTextAsync(Path.Combine(directory, "wrighty-codex.yml"));
            Assert.Contains("wrighty:auto", codex);
            Assert.Contains("wrighty:agent=codex", codex);
            Assert.Contains("owner/12", codex);
            Assert.Contains(actions, action => action.StartsWith("created Wrighty Codex"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Scaffold_is_idempotent_and_does_not_overwrite_conflicts()
    {
        var root = Path.Combine(Path.GetTempPath(), $"wrighty-forms-{Guid.NewGuid():N}");
        var directory = Path.Combine(root, ".github", "ISSUE_TEMPLATE");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "wrighty-codex.yml"), "custom\n");
        try
        {
            var scaffolder = new GitHubIssueFormScaffolder(
                new Discovery(new DiscoveredGitHubRepository("github.com", "owner/repo")),
                new Git(root));

            var actions = await scaffolder.ScaffoldAsync(
                root,
                Config,
                "origin",
                CancellationToken.None);

            Assert.Equal("custom\n", await File.ReadAllTextAsync(
                Path.Combine(directory, "wrighty-codex.yml")));
            Assert.Contains(actions, action => action.Contains("Did not overwrite conflicting"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Scaffold_refuses_a_mismatched_local_remote()
    {
        var root = Path.Combine(Path.GetTempPath(), $"wrighty-forms-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var actions = await new GitHubIssueFormScaffolder(
                    new Discovery(new DiscoveredGitHubRepository("github.com", "owner/other")),
                    new Git(root))
                .ScaffoldAsync(root, Config, "origin", CancellationToken.None);

            Assert.Single(actions);
            Assert.Contains("does not identify", actions[0]);
            Assert.False(Directory.Exists(Path.Combine(root, ".github")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static readonly TrackerConfig Config = new()
    {
        Repository = "owner/repo",
        ProjectOwner = "owner",
        ProjectNumber = 12
    };

    private sealed class Discovery(DiscoveredGitHubRepository? result) : IRepositoryDiscovery
    {
        public Task<DiscoveredGitHubRepository?> DiscoverAsync(
            string directory,
            string remoteName,
            CancellationToken cancellationToken) => Task.FromResult(result);
    }

    private sealed class Git(string root) : IGitProcess
    {
        public Task<GitProcessResult> RunAsync(
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken) =>
            Task.FromResult(new GitProcessResult(0, root + Environment.NewLine, string.Empty));
    }
}
