using System.Diagnostics;
using Highbyte.Wrighty.Errors;
using Highbyte.Wrighty.Processes;

namespace Highbyte.Wrighty.Workers;

public sealed record WorkerWorkspaceInfo(
    string Path,
    string? Branch,
    bool Dirty,
    bool MergedIntoHead);

public interface IWorkspaceInventory
{
    Task<IReadOnlyList<WorkerWorkspaceInfo>> ListAsync(
        string repositoryPath,
        string worktreeRoot,
        CancellationToken cancellationToken);

    Task<(bool WorkspaceRemoved, bool BranchDeleted)> CleanupAsync(
        string repositoryPath,
        string? workspacePath,
        string? branch,
        CancellationToken cancellationToken);
}

/// <summary>
/// Lists and removes retained worker worktrees and branches by delegating every safety decision
/// to git itself: `git worktree remove` refuses a dirty tree and `git branch -d` refuses an
/// unmerged branch, so no merge policy lives here.
/// </summary>
public sealed class GitWorkspaceInventory(IExecutableResolver executables) : IWorkspaceInventory
{
    public async Task<IReadOnlyList<WorkerWorkspaceInfo>> ListAsync(
        string repositoryPath,
        string worktreeRoot,
        CancellationToken cancellationToken)
    {
        var repository = Path.GetFullPath(repositoryPath);
        var root = Path.GetFullPath(worktreeRoot);
        if (!Directory.Exists(root))
            return [];

        var results = new List<WorkerWorkspaceInfo>();
        foreach (var path in Directory.EnumerateDirectories(root).Order(StringComparer.Ordinal))
        {
            // Linked worktrees carry a .git file (not a directory) pointing at the repository.
            if (!File.Exists(Path.Combine(path, ".git")))
                continue;
            var branchResult = await GitAsync(path,
                ["symbolic-ref", "--short", "-q", "HEAD"], cancellationToken);
            var branch = branchResult.ExitCode == 0 && !string.IsNullOrWhiteSpace(branchResult.Output)
                ? branchResult.Output.Trim()
                : null;
            var status = await GitAsync(path, ["status", "--porcelain"], cancellationToken);
            var dirty = status.ExitCode == 0 && !string.IsNullOrWhiteSpace(status.Output);
            var merged = false;
            if (branch is not null)
            {
                var ancestor = await GitAsync(repository,
                    ["merge-base", "--is-ancestor", branch, "HEAD"], cancellationToken);
                merged = ancestor.ExitCode == 0;
            }

            results.Add(new WorkerWorkspaceInfo(path, branch, dirty, merged));
        }

        return results;
    }

    public async Task<(bool WorkspaceRemoved, bool BranchDeleted)> CleanupAsync(
        string repositoryPath,
        string? workspacePath,
        string? branch,
        CancellationToken cancellationToken)
    {
        var repository = Path.GetFullPath(repositoryPath);
        var workspaceRemoved = false;
        if (!string.IsNullOrWhiteSpace(workspacePath) && Directory.Exists(workspacePath))
        {
            var removal = await GitAsync(repository,
                ["worktree", "remove", Path.GetFullPath(workspacePath)], cancellationToken);
            if (removal.ExitCode != 0)
                throw new TrackerException(
                    "WORKSPACE_NOT_CLEAN",
                    $"git refused to remove the worktree: {removal.Error.Trim()} " +
                    "Commit or discard its changes first; Wrighty never force-removes agent work.",
                    7);
            workspaceRemoved = true;
        }

        var branchDeleted = false;
        if (!string.IsNullOrWhiteSpace(branch))
        {
            var exists = await GitAsync(repository,
                ["show-ref", "--verify", "--quiet", $"refs/heads/{branch}"], cancellationToken);
            if (exists.ExitCode == 0)
            {
                var deletion = await GitAsync(repository, ["branch", "-d", branch], cancellationToken);
                if (deletion.ExitCode != 0)
                    throw new TrackerException(
                        "WORKSPACE_BRANCH_UNMERGED",
                        $"git refused to delete branch '{branch}': {deletion.Error.Trim()} " +
                        "Merge or push it first; Wrighty never force-deletes unmerged work.",
                        7);
                branchDeleted = true;
            }
        }

        return (workspaceRemoved, branchDeleted);
    }

    private async Task<(int ExitCode, string Output, string Error)> GitAsync(
        string cwd,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo
        {
            FileName = executables.Resolve("git"),
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)
            ?? throw new TrackerException("WORKSPACE_ERROR", "Could not start git.", 7);
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        return (process.ExitCode, await outputTask, await errorTask);
    }
}
