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

            var result = await scaffolder.ScaffoldAsync(
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
            Assert.Contains(result.Actions, action => action.StartsWith("created Wrighty Codex"));
            Assert.Equal(3, result.ChangedPaths.Count);
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

            var result = await scaffolder.ScaffoldAsync(
                root,
                Config,
                "origin",
                CancellationToken.None);

            Assert.Equal("custom\n", await File.ReadAllTextAsync(
                Path.Combine(directory, "wrighty-codex.yml")));
            Assert.Contains(result.Actions, action => action.Contains("Did not overwrite conflicting"));
            Assert.DoesNotContain(
                Path.Combine(directory, "wrighty-codex.yml"),
                result.ManagedPaths);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Scaffold_refreshes_an_unchanged_generated_form_for_a_new_project()
    {
        var root = Path.Combine(Path.GetTempPath(), $"wrighty-forms-{Guid.NewGuid():N}");
        var directory = Path.Combine(root, ".github", "ISSUE_TEMPLATE");
        Directory.CreateDirectory(directory);
        var previous = Config with { ProjectNumber = 11 };
        try
        {
            var scaffolder = new GitHubIssueFormScaffolder(
                new Discovery(new DiscoveredGitHubRepository("github.com", "owner/repo")),
                new Git(root));
            _ = await scaffolder.ScaffoldAsync(
                root,
                previous,
                "origin",
                CancellationToken.None);

            var result = await scaffolder.ScaffoldAsync(
                root,
                Config,
                "origin",
                CancellationToken.None);

            var codex = await File.ReadAllTextAsync(Path.Combine(directory, "wrighty-codex.yml"));
            Assert.Contains("owner/12", codex);
            Assert.DoesNotContain("owner/11", codex);
            Assert.Contains(result.Actions, action => action.StartsWith("updated Wrighty Codex"));
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
            var result = await new GitHubIssueFormScaffolder(
                    new Discovery(new DiscoveredGitHubRepository("github.com", "owner/other")),
                    new Git(root))
                .ScaffoldAsync(root, Config, "origin", CancellationToken.None);

            Assert.Single(result.Actions);
            Assert.Contains("does not identify", result.Actions[0]);
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

public sealed class GitHubIssueFormPublisherTests
{
    [Fact]
    public async Task Publish_commits_only_managed_forms_and_preserves_unrelated_staged_changes()
    {
        var root = Path.Combine(Path.GetTempPath(), $"wrighty-publish-{Guid.NewGuid():N}");
        var remote = Path.Combine(Path.GetTempPath(), $"wrighty-publish-remote-{Guid.NewGuid():N}.git");
        Directory.CreateDirectory(root);
        var git = new GitProcess();
        try
        {
            await GitAsync(git, root, "init", "--initial-branch=main");
            await GitAsync(git, root, "config", "user.name", "Wrighty Test");
            await GitAsync(git, root, "config", "user.email", "wrighty@example.invalid");
            await GitAsync(git, root, "init", "--bare", remote);
            await GitAsync(git, root, "remote", "add", "origin", remote);

            var unrelated = Path.Combine(root, "unrelated.txt");
            await File.WriteAllTextAsync(unrelated, "keep staged\n");
            await GitAsync(git, root, "add", "unrelated.txt");
            var directory = Path.Combine(root, ".github", "ISSUE_TEMPLATE");
            Directory.CreateDirectory(directory);
            var codex = Path.Combine(directory, "wrighty-codex.yml");
            var copilot = Path.Combine(directory, "wrighty-copilot.yml");
            await File.WriteAllTextAsync(codex, "name: Codex\n");
            await File.WriteAllTextAsync(copilot, "name: Copilot\n");

            var publisher = new GitHubIssueFormPublisher(git);
            var actions = await publisher.PublishAsync(
                root,
                [codex, copilot],
                "origin",
                CancellationToken.None);

            var committed = await GitAsync(git, root, "show", "--name-only", "--format=");
            Assert.Contains(".github/ISSUE_TEMPLATE/wrighty-codex.yml", committed);
            Assert.Contains(".github/ISSUE_TEMPLATE/wrighty-copilot.yml", committed);
            Assert.DoesNotContain("unrelated.txt", committed);
            var staged = await GitAsync(git, root, "diff", "--cached", "--name-only");
            Assert.Contains("unrelated.txt", staged);
            Assert.Contains(actions, action => action.StartsWith("pushed branch", StringComparison.Ordinal));
            Assert.Empty(await publisher.FindPendingAsync(
                root,
                [codex, copilot],
                CancellationToken.None));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
            Directory.Delete(remote, recursive: true);
        }
    }

    [Fact]
    public async Task Publish_reports_a_local_commit_when_push_fails()
    {
        var root = Path.Combine(Path.GetTempPath(), $"wrighty-publish-failure-{Guid.NewGuid():N}");
        var path = Path.Combine(root, ".github", "ISSUE_TEMPLATE", "wrighty-codex.yml");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, "name: Codex\n");
        var git = new ScriptedGit(
            new GitProcessResult(0, root + "\n", string.Empty),
            new GitProcessResult(0, "feature/forms\n", string.Empty),
            new GitProcessResult(0, "?? .github/ISSUE_TEMPLATE/wrighty-codex.yml\n", string.Empty),
            new GitProcessResult(0, string.Empty, string.Empty),
            new GitProcessResult(0, string.Empty, string.Empty),
            new GitProcessResult(1, string.Empty, "remote rejected"));

        try
        {
            var exception = await Assert.ThrowsAsync<Highbyte.Wrighty.Errors.TrackerException>(() =>
                new GitHubIssueFormPublisher(git).PublishAsync(
                    root,
                    [path],
                    "origin",
                    CancellationToken.None));

            Assert.Equal("PARTIAL_ISSUE_FORM_PUBLISH", exception.Code);
            Assert.Contains("committed locally", exception.Message);
            Assert.Contains("git push --set-upstream origin feature/forms", exception.Message);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task<string> GitAsync(GitProcess git, string root, params string[] arguments)
    {
        var all = new[] { "-C", root }.Concat(arguments).ToArray();
        var result = await git.RunAsync(all, CancellationToken.None);
        Assert.True(result.ExitCode == 0, result.StandardError);
        return result.StandardOutput;
    }

    private sealed class ScriptedGit(params GitProcessResult[] results) : IGitProcess
    {
        private readonly Queue<GitProcessResult> queue = new(results);

        public Task<GitProcessResult> RunAsync(
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken) => Task.FromResult(queue.Dequeue());
    }
}
