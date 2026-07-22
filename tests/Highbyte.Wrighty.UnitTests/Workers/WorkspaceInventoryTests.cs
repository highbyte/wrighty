using System.Diagnostics;
using Highbyte.Wrighty.Errors;
using Highbyte.Wrighty.Models;
using Highbyte.Wrighty.Processes;
using Highbyte.Wrighty.Workers;

namespace Highbyte.Wrighty.UnitTests.Workers;

public sealed class WorkspaceInventoryTests : IDisposable
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(), $"wrighty-inventory-tests-{Guid.NewGuid():N}");
    private readonly string repository;
    private readonly GitWorkspaceManager manager = new(new PathExecutableResolver());
    private readonly GitWorkspaceInventory inventory = new(new PathExecutableResolver());
    private readonly string worktreeRoot;

    public WorkspaceInventoryTests()
    {
        repository = Path.Combine(root, "repo");
        worktreeRoot = Path.Combine(root, "repo.worktrees");
        Directory.CreateDirectory(repository);
        Git("init");
        Git("config", "user.name", "Wrighty Tests");
        Git("config", "user.email", "wrighty-tests@example.invalid");
        File.WriteAllText(Path.Combine(repository, "README.md"), "fixture\n");
        Git("add", "README.md");
        Git("commit", "-m", "fixture");
    }

    [Fact]
    public async Task Lists_retained_worktrees_with_dirty_and_merged_state()
    {
        var clean = await Prepare("local:1", "agent:test:aaaa1111");
        var dirty = await Prepare("local:2", "agent:test:bbbb2222");
        File.AppendAllText(Path.Combine(dirty.Path, "README.md"), "dirty\n");

        var entries = await inventory.ListAsync(repository, worktreeRoot, CancellationToken.None);

        Assert.Equal(2, entries.Count);
        var cleanEntry = Assert.Single(entries, entry => entry.Path == clean.Path);
        Assert.Equal(clean.Branch, cleanEntry.Branch);
        Assert.False(cleanEntry.Dirty);
        Assert.True(cleanEntry.MergedIntoHead);
        var dirtyEntry = Assert.Single(entries, entry => entry.Path == dirty.Path);
        Assert.True(dirtyEntry.Dirty);
    }

    [Fact]
    public async Task Cleanup_removes_a_clean_worktree_and_its_merged_branch()
    {
        var workspace = await Prepare("local:3", "agent:test:cccc3333");

        var (workspaceRemoved, branchDeleted) = await inventory.CleanupAsync(
            repository, workspace.Path, workspace.Branch, CancellationToken.None);

        Assert.True(workspaceRemoved);
        Assert.True(branchDeleted);
        Assert.False(Directory.Exists(workspace.Path));
        Assert.Empty(await inventory.ListAsync(repository, worktreeRoot, CancellationToken.None));
    }

    [Fact]
    public async Task Cleanup_refuses_a_dirty_worktree()
    {
        var workspace = await Prepare("local:4", "agent:test:dddd4444");
        File.AppendAllText(Path.Combine(workspace.Path, "README.md"), "dirty\n");

        var exception = await Assert.ThrowsAsync<TrackerException>(() =>
            inventory.CleanupAsync(
                repository, workspace.Path, workspace.Branch, CancellationToken.None));

        Assert.Equal("WORKSPACE_NOT_CLEAN", exception.Code);
        Assert.True(Directory.Exists(workspace.Path));
    }

    [Fact]
    public async Task Cleanup_refuses_an_unmerged_branch_after_removing_the_worktree()
    {
        var workspace = await Prepare("local:5", "agent:test:eeee5555");
        File.WriteAllText(Path.Combine(workspace.Path, "new.txt"), "work\n");
        GitIn(workspace.Path, "add", "new.txt");
        GitIn(workspace.Path, "commit", "-m", "unmerged work");

        var exception = await Assert.ThrowsAsync<TrackerException>(() =>
            inventory.CleanupAsync(
                repository, workspace.Path, workspace.Branch, CancellationToken.None));

        Assert.Equal("WORKSPACE_BRANCH_UNMERGED", exception.Code);
        Assert.False(Directory.Exists(workspace.Path));

        var entries = await inventory.ListAsync(repository, worktreeRoot, CancellationToken.None);
        Assert.Empty(entries);
    }

    [Fact]
    public async Task Cleanup_with_force_discards_a_dirty_worktree_and_unmerged_branch()
    {
        var workspace = await Prepare("local:11", "agent:test:bbbb1111");
        // Unmerged commit on the branch...
        File.WriteAllText(Path.Combine(workspace.Path, "new.txt"), "work\n");
        GitIn(workspace.Path, "add", "new.txt");
        GitIn(workspace.Path, "commit", "-m", "unmerged work");
        // ...plus an uncommitted change, so the default cleanup would be refused twice over.
        File.AppendAllText(Path.Combine(workspace.Path, "README.md"), "dirty\n");

        var (workspaceRemoved, branchDeleted) = await inventory.CleanupAsync(
            repository, workspace.Path, workspace.Branch, CancellationToken.None, force: true);

        Assert.True(workspaceRemoved);
        Assert.True(branchDeleted);
        Assert.False(Directory.Exists(workspace.Path));
        Assert.Empty(await inventory.ListAsync(repository, worktreeRoot, CancellationToken.None));
    }

    [Fact]
    public async Task Cleanup_of_an_absent_workspace_and_branch_reports_nothing_done()
    {
        var (workspaceRemoved, branchDeleted) = await inventory.CleanupAsync(
            repository, Path.Combine(root, "missing"), "wrighty-worker/missing",
            CancellationToken.None);

        Assert.False(workspaceRemoved);
        Assert.False(branchDeleted);
    }

    [Fact]
    public async Task Status_reports_clean_and_merged_for_a_fresh_worktree()
    {
        var workspace = await Prepare("local:6", "agent:test:ffff6666");

        var result = await inventory.GetStatusAsync(
            repository, workspace.Path, workspace.Branch, CancellationToken.None);

        Assert.True(result.IsAvailable);
        Assert.False(result.Status!.Dirty);
        Assert.True(result.Status.MergedIntoHead);
        Assert.Null(result.Unavailable);
    }

    [Fact]
    public async Task Status_reports_dirty_when_the_worktree_has_uncommitted_changes()
    {
        var workspace = await Prepare("local:7", "agent:test:7777aaaa");
        File.AppendAllText(Path.Combine(workspace.Path, "README.md"), "dirty\n");

        var result = await inventory.GetStatusAsync(
            repository, workspace.Path, workspace.Branch, CancellationToken.None);

        Assert.True(result.IsAvailable);
        Assert.True(result.Status!.Dirty);
    }

    [Fact]
    public async Task Status_reports_unmerged_after_a_commit_on_the_worker_branch()
    {
        var workspace = await Prepare("local:8", "agent:test:8888bbbb");
        File.WriteAllText(Path.Combine(workspace.Path, "new.txt"), "work\n");
        GitIn(workspace.Path, "add", "new.txt");
        GitIn(workspace.Path, "commit", "-m", "committed work");

        var result = await inventory.GetStatusAsync(
            repository, workspace.Path, workspace.Branch, CancellationToken.None);

        Assert.True(result.IsAvailable);
        Assert.False(result.Status!.Dirty);
        Assert.False(result.Status.MergedIntoHead);
    }

    [Fact]
    public async Task Status_is_unavailable_when_no_workspace_is_recorded()
    {
        var result = await inventory.GetStatusAsync(
            repository, null, null, CancellationToken.None);

        Assert.False(result.IsAvailable);
        Assert.NotNull(result.Unavailable);
    }

    [Fact]
    public async Task Status_is_unavailable_when_the_worktree_is_absent_on_this_host()
    {
        var result = await inventory.GetStatusAsync(
            repository, Path.Combine(root, "missing"), "wrighty-worker/missing",
            CancellationToken.None);

        Assert.False(result.IsAvailable);
        Assert.Contains("not present", result.Unavailable);
        Assert.True(result.WorktreeAbsent);
    }

    [Fact]
    public async Task Status_present_path_without_a_git_link_is_unavailable_but_not_absent()
    {
        var present = Path.Combine(root, "present-not-a-worktree");
        Directory.CreateDirectory(present);

        var result = await inventory.GetStatusAsync(
            repository, present, "wrighty-worker/present", CancellationToken.None);

        Assert.False(result.IsAvailable);
        // The directory exists, so it is not "removed" — callers must not relabel it.
        Assert.False(result.WorktreeAbsent);
    }

    [Fact]
    public async Task Status_propagates_genuine_caller_cancellation()
    {
        var workspace = await Prepare("local:9", "agent:test:9999cccc");
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            inventory.GetStatusAsync(
                repository, workspace.Path, workspace.Branch, cancelled.Token));
    }

    [Fact]
    public async Task Status_is_unavailable_when_git_cannot_be_executed()
    {
        var workspace = await Prepare("local:10", "agent:test:aaaa0000");
        var broken = new GitWorkspaceInventory(
            new FixedExecutableResolver(Path.Combine(root, "no-such-git")));

        var result = await broken.GetStatusAsync(
            repository, workspace.Path, workspace.Branch, CancellationToken.None);

        Assert.False(result.IsAvailable);
        Assert.NotNull(result.Unavailable);
    }

    private sealed class FixedExecutableResolver(string path) : IExecutableResolver
    {
        public string Resolve(string executableName) => path;
    }

    private async Task<Workspace> Prepare(string id, string claimantId) =>
        await manager.PrepareAsync(
            new WorkspaceRequest(WorkspaceMode.Worktree, repository,
                new WorkItemId(id), claimantId),
            CancellationToken.None);

    private void Git(params string[] arguments) => GitIn(repository, arguments);

    private static void GitIn(string directory, params string[] arguments)
    {
        var start = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = directory,
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
