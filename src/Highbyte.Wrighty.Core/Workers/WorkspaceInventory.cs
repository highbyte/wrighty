using System.Diagnostics;
using Highbyte.Wrighty.Errors;
using Highbyte.Wrighty.Processes;

namespace Highbyte.Wrighty.Workers;

public sealed record WorkerWorkspaceInfo(
    string Path,
    string? Branch,
    bool Dirty,
    bool MergedIntoHead);

/// <summary>The git-calculated state of a single worktree.</summary>
public sealed record WorkspaceStatus(bool Dirty, bool MergedIntoHead);

/// <summary>
/// The outcome of probing a single worktree's git state. Either <see cref="Status"/> is present,
/// or <see cref="Unavailable"/> explains why it could not be calculated (worktree absent on this
/// host, git timed out, git failed). Probing never throws for these cases.
/// </summary>
public sealed record WorkspaceStatusResult(
    WorkspaceStatus? Status,
    string? Unavailable,
    bool WorktreeAbsent = false)
{
    public bool IsAvailable => Status is not null;
}

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
        CancellationToken cancellationToken,
        bool force = false);

    /// <summary>
    /// Safely calculate one worktree's dirty/merged state for read-only display. Applies an
    /// internal timeout, kills a git process that overruns it, and never throws except when the
    /// caller's own token is cancelled: any other failure is reported through
    /// <see cref="WorkspaceStatusResult.Unavailable"/> so a caller (CLI or web) can degrade
    /// gracefully.
    /// </summary>
    Task<WorkspaceStatusResult> GetStatusAsync(
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
        CancellationToken cancellationToken,
        bool force = false)
    {
        var repository = Path.GetFullPath(repositoryPath);
        var workspaceRemoved = false;
        if (!string.IsNullOrWhiteSpace(workspacePath) && Directory.Exists(workspacePath))
        {
            // With force, override git's dirty-tree refusal (discards untracked/modified files);
            // without it, git protects the working directory and we surface the refusal.
            string[] removeArgs = force
                ? ["worktree", "remove", "--force", Path.GetFullPath(workspacePath)]
                : ["worktree", "remove", Path.GetFullPath(workspacePath)];
            var removal = await GitAsync(repository, removeArgs, cancellationToken);
            if (removal.ExitCode != 0)
                throw new TrackerException(
                    "WORKSPACE_NOT_CLEAN",
                    $"git refused to remove the worktree: {removal.Error.Trim()} " +
                    "Commit or discard its changes first, or rerun with --force to discard them; " +
                    "Wrighty never force-removes agent work unless you ask.",
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
                // Force switches -d (refuses unmerged) to -D (discards unmerged commits).
                var deletion = await GitAsync(
                    repository, ["branch", force ? "-D" : "-d", branch], cancellationToken);
                if (deletion.ExitCode != 0)
                    throw new TrackerException(
                        "WORKSPACE_BRANCH_UNMERGED",
                        $"git refused to delete branch '{branch}': {deletion.Error.Trim()} " +
                        "Merge or push it first, or rerun with --force to discard the unmerged " +
                        "commits; Wrighty never force-deletes unmerged work unless you ask.",
                        7);
                branchDeleted = true;
            }
        }

        return (workspaceRemoved, branchDeleted);
    }

    private static readonly TimeSpan StatusProbeTimeout = TimeSpan.FromSeconds(5);

    public async Task<WorkspaceStatusResult> GetStatusAsync(
        string repositoryPath,
        string? workspacePath,
        string? branch,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(workspacePath))
            return new WorkspaceStatusResult(null, "No worktree is recorded for this item.");
        var full = Path.GetFullPath(workspacePath);
        // A missing directory means the worktree was removed (cleaned up or on another host); a
        // present directory without a .git link is a recorded path that is not a worktree at all.
        // Only the former is "removed" — callers relabel it, so keep the two cases distinct.
        if (!Directory.Exists(full))
            return new WorkspaceStatusResult(
                null, "The recorded worktree is not present on this host.", WorktreeAbsent: true);
        if (!File.Exists(Path.Combine(full, ".git")))
            return new WorkspaceStatusResult(
                null, "The recorded path is not a git worktree on this host.");

        // Bound the git calls with an internal timeout so a hung or slow repository never blocks
        // the caller. Anything other than genuine caller-cancellation degrades to an "unavailable"
        // reason instead of throwing.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(StatusProbeTimeout);
        try
        {
            var status = await GitAsync(full, ["status", "--porcelain"], timeout.Token);
            if (status.ExitCode != 0)
                return new WorkspaceStatusResult(null, "git could not read the worktree status.");
            var dirty = !string.IsNullOrWhiteSpace(status.Output);

            var merged = false;
            if (!string.IsNullOrWhiteSpace(branch))
            {
                var ancestor = await GitAsync(Path.GetFullPath(repositoryPath),
                    ["merge-base", "--is-ancestor", branch, "HEAD"], timeout.Token);
                merged = ancestor.ExitCode == 0;
            }

            return new WorkspaceStatusResult(new WorkspaceStatus(dirty, merged), null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return new WorkspaceStatusResult(null, "Timed out while reading git worktree status.");
        }
        catch (Exception)
        {
            return new WorkspaceStatusResult(null, "Could not read git worktree status.");
        }
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
        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Do not leave an overrunning or cancelled git process behind.
            try { process.Kill(entireProcessTree: true); }
            catch { /* best effort: the process may already have exited */ }
            throw;
        }
        return (process.ExitCode, await outputTask, await errorTask);
    }
}
