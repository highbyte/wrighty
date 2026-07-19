using System.Diagnostics;
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
            mode, repository, new WorkItemId("local:1"), "agent:test:123456789",
            null, CancellationToken.None);

        Assert.Equal(Path.GetFullPath(repository), workspace.Path);
        Assert.False(workspace.IsWorktree);
        Assert.False(await manager.CleanupAsync(workspace, CancellationToken.None));
    }

    [Fact]
    public async Task Worktree_mode_creates_branch_and_clean_worktree_can_be_removed()
    {
        var workspace = await manager.PrepareAsync(
            WorkspaceMode.Worktree,
            repository,
            new WorkItemId("local:One/Two"),
            "agent:test:123456789",
            null,
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
            WorkspaceMode.Worktree,
            repository,
            new WorkItemId("local:2"),
            "agent:test:abcdef",
            existing,
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
                WorkspaceMode.Worktree,
                repository,
                new WorkItemId("local:3"),
                "agent:test:abcdef",
                null,
                CancellationToken.None));

        Assert.Equal("WORKSPACE_EXISTS", exception.Code);
    }

    [Fact]
    public async Task Dirty_worktree_is_retained()
    {
        var workspace = await manager.PrepareAsync(
            WorkspaceMode.Worktree,
            repository,
            new WorkItemId("local:4"),
            "agent:test:dirty",
            null,
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
                WorkspaceMode.Worktree,
                notRepository,
                new WorkItemId("local:5"),
                "agent:test:error",
                null,
                CancellationToken.None));

        Assert.Equal("WORKSPACE_ERROR", exception.Code);
        Assert.Contains("git worktree add failed", exception.Message);
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
