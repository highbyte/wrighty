using System.Diagnostics;
using Highbyte.Wrighty.Errors;
using Highbyte.Wrighty.Models;
using Highbyte.Wrighty.Processes;

namespace Highbyte.Wrighty.Workers;

public enum WorkspaceMode { Current, Shared, Worktree }

public interface IWorkspaceManager
{
    Task<Workspace> PrepareAsync(
        WorkspaceMode mode,
        string repositoryPath,
        WorkItemId itemId,
        string claimantId,
        string? existingPath,
        CancellationToken cancellationToken);

    Task<bool> CleanupAsync(Workspace workspace, CancellationToken cancellationToken) =>
        Task.FromResult(false);
}

public sealed class GitWorkspaceManager(IExecutableResolver executables) : IWorkspaceManager
{
    public async Task<Workspace> PrepareAsync(
        WorkspaceMode mode,
        string repositoryPath,
        WorkItemId itemId,
        string claimantId,
        string? existingPath,
        CancellationToken cancellationToken)
    {
        var repository = Path.GetFullPath(repositoryPath);
        if (mode is WorkspaceMode.Current or WorkspaceMode.Shared)
            return new Workspace(repository);
        if (!string.IsNullOrWhiteSpace(existingPath) && Directory.Exists(existingPath))
            return new Workspace(Path.GetFullPath(existingPath), true);

        var root = Directory.GetParent(repository)?.FullName
            ?? throw new TrackerException("WORKSPACE_ERROR", "The repository has no parent directory.", 7);
        var worktreeRoot = Path.Combine(root, $"{Path.GetFileName(repository)}.worktrees");
        Directory.CreateDirectory(worktreeRoot);
        var suffix = claimantId.Split(':').Last();
        if (suffix.Length > 8) suffix = suffix[..8];
        var slug = Slug(itemId.Value);
        var path = Path.Combine(worktreeRoot, $"{slug}-{suffix}");
        var branch = $"wrighty-worker/{slug}-{suffix}";
        if (Directory.Exists(path))
            throw new TrackerException("WORKSPACE_EXISTS",
                $"Worker worktree path already exists: {path}", 7);

        var result = await GitAsync(repository,
            ["worktree", "add", "-b", branch, path, "HEAD"], cancellationToken);
        if (result.ExitCode != 0)
            throw new TrackerException("WORKSPACE_ERROR",
                $"git worktree add failed: {result.Error.Trim()}", 7);
        return new Workspace(path, true, branch);
    }

    public async Task<bool> CleanupAsync(Workspace workspace, CancellationToken cancellationToken)
    {
        if (!workspace.IsWorktree || !Directory.Exists(workspace.Path)) return false;
        var result = await GitAsync(workspace.Path, ["worktree", "remove", workspace.Path],
            cancellationToken);
        // A dirty worktree is deliberately retained. Never force removal of agent changes.
        return result.ExitCode == 0;
    }

    private async Task<(int ExitCode, string Error)> GitAsync(
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
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.StandardOutput.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        return (process.ExitCode, await errorTask);
    }

    private static string Slug(string value)
    {
        var slug = string.Concat(value.ToLowerInvariant()
            .Select(ch => char.IsLetterOrDigit(ch) ? ch : '-')).Trim('-');
        return string.IsNullOrEmpty(slug) ? "item" : slug.Length <= 48 ? slug : slug[..48];
    }
}
