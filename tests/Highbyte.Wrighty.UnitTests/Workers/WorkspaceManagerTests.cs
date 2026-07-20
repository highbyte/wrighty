using System.Diagnostics;
using Highbyte.Wrighty.Configuration;
using Highbyte.Wrighty.Errors;
using Highbyte.Wrighty.Models;
using Highbyte.Wrighty.Processes;
using Highbyte.Wrighty.Workers;

namespace Highbyte.Wrighty.UnitTests.Workers;

public sealed class WorkspaceManagerTests : IDisposable
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(), $"wrighty-workspace-tests-{Guid.NewGuid():N}");
    private readonly string repository;
    private readonly GitWorkspaceManager manager = new(new PathExecutableResolver());

    public WorkspaceManagerTests()
    {
        repository = Path.Combine(root, "repo");
        Directory.CreateDirectory(repository);
        Git("init");
        Git("config", "user.name", "Wrighty Tests");
        Git("config", "user.email", "wrighty-tests@example.invalid");
        File.WriteAllText(Path.Combine(repository, "README.md"), "fixture\n");
        Git("add", "README.md");
        Git("commit", "-m", "fixture");
    }

    [Theory]
    [InlineData(WorkspaceMode.Current)]
    [InlineData(WorkspaceMode.Shared)]
    public async Task Nonisolated_modes_use_the_repository(WorkspaceMode mode)
    {
        var workspace = await manager.PrepareAsync(
            new WorkspaceRequest(mode, repository, new WorkItemId("local:1"),
                "agent:test:123456789"),
            CancellationToken.None);

        Assert.Equal(Path.GetFullPath(repository), workspace.Path);
        Assert.False(workspace.IsWorktree);
        Assert.False(await manager.CleanupAsync(workspace, CancellationToken.None));
    }

    [Fact]
    public async Task Worktree_mode_creates_branch_and_clean_worktree_can_be_removed()
    {
        var workspace = await manager.PrepareAsync(
            new WorkspaceRequest(WorkspaceMode.Worktree, repository,
                new WorkItemId("local:One/Two"), "agent:test:123456789"),
            CancellationToken.None);

        Assert.True(workspace.IsWorktree);
        Assert.Equal("wrighty-worker/local-one-two-12345678", workspace.Branch);
        Assert.True(Directory.Exists(workspace.Path));

        Assert.True(await manager.CleanupAsync(workspace, CancellationToken.None));
        Assert.False(Directory.Exists(workspace.Path));
    }

    [Fact]
    public async Task Existing_worktree_path_is_reused()
    {
        var existing = Path.Combine(root, "existing");
        Directory.CreateDirectory(existing);

        var workspace = await manager.PrepareAsync(
            new WorkspaceRequest(WorkspaceMode.Worktree, repository,
                new WorkItemId("local:2"), "agent:test:abcdef", existing),
            CancellationToken.None);

        Assert.Equal(Path.GetFullPath(existing), workspace.Path);
        Assert.True(workspace.IsWorktree);
    }

    [Fact]
    public async Task Existing_target_path_is_rejected()
    {
        var target = Path.Combine(
            root, "repo.worktrees", "local-3-abcdef");
        Directory.CreateDirectory(target);

        var exception = await Assert.ThrowsAsync<TrackerException>(
            () => manager.PrepareAsync(
                new WorkspaceRequest(WorkspaceMode.Worktree, repository,
                    new WorkItemId("local:3"), "agent:test:abcdef"),
                CancellationToken.None));

        Assert.Equal("WORKSPACE_EXISTS", exception.Code);
    }

    [Fact]
    public async Task Dirty_worktree_is_retained()
    {
        var workspace = await manager.PrepareAsync(
            new WorkspaceRequest(WorkspaceMode.Worktree, repository,
                new WorkItemId("local:4"), "agent:test:dirty"),
            CancellationToken.None);
        File.AppendAllText(Path.Combine(workspace.Path, "README.md"), "dirty\n");

        Assert.False(await manager.CleanupAsync(workspace, CancellationToken.None));
        Assert.True(Directory.Exists(workspace.Path));
    }

    [Fact]
    public async Task Worktree_creation_outside_a_repository_reports_git_error()
    {
        var notRepository = Path.Combine(root, "not-repository");
        Directory.CreateDirectory(notRepository);

        var exception = await Assert.ThrowsAsync<TrackerException>(
            () => manager.PrepareAsync(
                new WorkspaceRequest(WorkspaceMode.Worktree, notRepository,
                    new WorkItemId("local:5"), "agent:test:error"),
                CancellationToken.None));

        Assert.Equal("WORKSPACE_ERROR", exception.Code);
        Assert.Contains("git worktree add failed", exception.Message);
    }

    [Fact]
    public async Task Configured_root_and_formats_control_location_and_names()
    {
        var worker = new WorkerConfig
        {
            WorktreeRoot = "{repoParent}/{repo}-agents",
            BranchFormat = "feature/{number}-{title}",
            WorktreeNameFormat = "{number}-{title}"
        };

        var workspace = await manager.PrepareAsync(
            new WorkspaceRequest(WorkspaceMode.Worktree, repository,
                new WorkItemId("local:22"), "agent:test:123456789",
                ItemTitle: "Validate User Names!", AgentName: "claude", Worker: worker),
            CancellationToken.None);

        Assert.Equal("feature/22-validate-user-names", workspace.Branch);
        Assert.Equal(
            Path.Combine(root, "repo-agents", "22-validate-user-names"),
            workspace.Path);
        Assert.True(await manager.CleanupAsync(workspace, CancellationToken.None));
    }

    [Fact]
    public async Task Formats_without_unique_get_a_suffix_when_the_branch_already_exists()
    {
        var worker = new WorkerConfig
        {
            BranchFormat = "feature/{number}",
            WorktreeNameFormat = "item-{number}"
        };
        var request = new WorkspaceRequest(WorkspaceMode.Worktree, repository,
            new WorkItemId("local:7"), "agent:test:aaaa1111", Worker: worker);

        var first = await manager.PrepareAsync(request, CancellationToken.None);
        Assert.Equal("feature/7", first.Branch);
        Assert.True(await manager.CleanupAsync(first, CancellationToken.None));

        // The branch survives cleanup; a re-run must disambiguate instead of failing.
        var second = await manager.PrepareAsync(
            request with { ClaimantId = "agent:test:bbbb2222" }, CancellationToken.None);
        Assert.Equal("feature/7-bbbb2222", second.Branch);
        Assert.EndsWith("item-7-bbbb2222", second.Path);
        Assert.True(await manager.CleanupAsync(second, CancellationToken.None));
    }

    [Fact]
    public async Task Branch_format_producing_an_invalid_ref_is_rejected()
    {
        var worker = new WorkerConfig { BranchFormat = "///" };

        var exception = await Assert.ThrowsAsync<TrackerException>(
            () => manager.PrepareAsync(
                new WorkspaceRequest(WorkspaceMode.Worktree, repository,
                    new WorkItemId("local:8"), "agent:test:cccc3333", Worker: worker),
                CancellationToken.None));

        Assert.Equal("WORKSPACE_ERROR", exception.Code);
        Assert.Contains("invalid git branch name", exception.Message);
    }

    private void Git(params string[] arguments)
    {
        var start = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = repository,
            UseShellExecute = false,
            RedirectStandardError = true
        };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, error);
    }

    public void Dispose()
    {
        if (!Directory.Exists(root))
            return;
        try
        {
            Git("worktree", "prune");
        }
        catch
        {
            // The assertions report setup failures; cleanup remains best effort.
        }
        Directory.Delete(root, recursive: true);
    }
}
