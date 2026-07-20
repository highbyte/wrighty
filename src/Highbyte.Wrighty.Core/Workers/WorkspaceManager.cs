using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Highbyte.Wrighty.Configuration;
using Highbyte.Wrighty.Errors;
using Highbyte.Wrighty.Models;
using Highbyte.Wrighty.Processes;

namespace Highbyte.Wrighty.Workers;

public enum WorkspaceMode { Current, Shared, Worktree }

public sealed record WorkspaceRequest(
    WorkspaceMode Mode,
    string RepositoryPath,
    WorkItemId ItemId,
    string ClaimantId,
    string? ExistingPath = null,
    string? ItemTitle = null,
    string? AgentName = null,
    WorkerConfig? Worker = null);

public interface IWorkspaceManager
{
    Task<Workspace> PrepareAsync(WorkspaceRequest request, CancellationToken cancellationToken);

    Task<bool> CleanupAsync(Workspace workspace, CancellationToken cancellationToken) =>
        Task.FromResult(false);
}

public sealed class GitWorkspaceManager(IExecutableResolver executables) : IWorkspaceManager
{
    public const string DefaultWorktreeRoot = "{repoParent}/{repo}.worktrees";
    public const string DefaultWorktreeNameFormat = "{id}-{unique}";
    public const string DefaultBranchFormat = "wrighty-worker/{id}-{unique}";

    public static readonly IReadOnlyList<string> RootPlaceholders =
        ["repo", "repoParent", "home", "repoPathHash"];

    public static readonly IReadOnlyList<string> NamePlaceholders =
        ["id", "number", "title", "unique", "agent", "date"];

    public async Task<Workspace> PrepareAsync(
        WorkspaceRequest request,
        CancellationToken cancellationToken)
    {
        var repository = Path.GetFullPath(request.RepositoryPath);
        if (request.Mode is WorkspaceMode.Current or WorkspaceMode.Shared)
            return new Workspace(repository);
        if (!string.IsNullOrWhiteSpace(request.ExistingPath) && Directory.Exists(request.ExistingPath))
            return new Workspace(Path.GetFullPath(request.ExistingPath), true);

        var worktreeRoot = ExpandRoot(
            request.Worker?.WorktreeRoot ?? DefaultWorktreeRoot, repository);
        Directory.CreateDirectory(worktreeRoot);
        var values = NameValues(request);
        var nameFormat = request.Worker?.WorktreeNameFormat ?? DefaultWorktreeNameFormat;
        var branchFormat = request.Worker?.BranchFormat ?? DefaultBranchFormat;
        var name = SanitizeName(Expand(nameFormat, values));
        var branch = SanitizeBranch(Expand(branchFormat, values));
        var path = Path.Combine(worktreeRoot, name);
        if (Directory.Exists(path) || await BranchExistsAsync(repository, branch, cancellationToken))
        {
            // A retained worktree or branch from an earlier run is a normal state. When the
            // configured format has no {unique} of its own, disambiguate instead of failing.
            if (!nameFormat.Contains("{unique}", StringComparison.Ordinal) ||
                !branchFormat.Contains("{unique}", StringComparison.Ordinal))
            {
                name = SanitizeName($"{Expand(nameFormat, values)}-{values["unique"]}");
                branch = SanitizeBranch($"{Expand(branchFormat, values)}-{values["unique"]}");
                path = Path.Combine(worktreeRoot, name);
            }

            if (Directory.Exists(path))
                throw new TrackerException("WORKSPACE_EXISTS",
                    $"Worker worktree path already exists: {path}", 7);
        }

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

    public static string ResolveWorktreeRoot(WorkerConfig? worker, string repositoryPath) =>
        ExpandRoot(worker?.WorktreeRoot ?? DefaultWorktreeRoot, Path.GetFullPath(repositoryPath));

    private static string ExpandRoot(string template, string repository)
    {
        var parent = Directory.GetParent(repository)?.FullName
            ?? throw new TrackerException("WORKSPACE_ERROR", "The repository has no parent directory.", 7);
        var expanded = template
            .Replace("{repo}", Path.GetFileName(repository), StringComparison.Ordinal)
            .Replace("{repoParent}", parent, StringComparison.Ordinal)
            .Replace("{home}",
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                StringComparison.Ordinal)
            .Replace("{repoPathHash}", ShortHash(repository), StringComparison.Ordinal);
        return Path.GetFullPath(expanded, repository);
    }

    private static Dictionary<string, string> NameValues(WorkspaceRequest request)
    {
        var suffix = request.ClaimantId.Split(':').Last();
        if (suffix.Length > 8) suffix = suffix[..8];
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["id"] = Slug(request.ItemId.Value, 48),
            ["number"] = Number(request.ItemId.Value),
            ["title"] = Slug(request.ItemTitle ?? string.Empty, 30),
            ["unique"] = suffix,
            ["agent"] = Slug(request.AgentName ?? string.Empty, 16),
            ["date"] = DateTime.UtcNow.ToString("yyyyMMdd")
        };
    }

    private static string Expand(string template, IReadOnlyDictionary<string, string> values)
    {
        var expanded = template;
        foreach (var pair in values)
            expanded = expanded.Replace($"{{{pair.Key}}}", pair.Value, StringComparison.Ordinal);
        return expanded;
    }

    private static string Number(string id)
    {
        // Canonical IDs end in the item number: local:22, github:owner/repo#42.
        var end = id.Length;
        var start = end;
        while (start > 0 && char.IsDigit(id[start - 1])) start--;
        return start < end ? id[start..end] : Slug(id, 48);
    }

    private static string SanitizeName(string value)
    {
        var slug = Slug(value, 100);
        if (string.IsNullOrEmpty(slug))
            throw new TrackerException("WORKSPACE_ERROR",
                "The configured worktree name format produced an empty name.", 7);
        return slug;
    }

    private static string SanitizeBranch(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var ch in value)
            builder.Append(char.IsLetterOrDigit(ch) || ch is '/' or '.' or '_' or '-'
                ? ch
                : '-');
        var branch = builder.ToString();
        while (branch.Contains("//", StringComparison.Ordinal))
            branch = branch.Replace("//", "/", StringComparison.Ordinal);
        while (branch.Contains("..", StringComparison.Ordinal))
            branch = branch.Replace("..", ".", StringComparison.Ordinal);
        branch = branch.Trim('/', '.', '-');
        if (branch.Length > 200) branch = branch[..200];
        if (string.IsNullOrEmpty(branch) || branch.EndsWith(".lock", StringComparison.Ordinal))
            throw new TrackerException("WORKSPACE_ERROR",
                "The configured branch format produced an invalid git branch name.", 7);
        return branch;
    }

    private async Task<bool> BranchExistsAsync(
        string repository,
        string branch,
        CancellationToken cancellationToken)
    {
        var result = await GitAsync(repository,
            ["show-ref", "--verify", "--quiet", $"refs/heads/{branch}"], cancellationToken);
        return result.ExitCode == 0;
    }

    private static string ShortHash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..8]
            .ToLowerInvariant();

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

    private static string Slug(string value, int maximumLength)
    {
        var slug = string.Concat(value.ToLowerInvariant()
            .Select(ch => char.IsLetterOrDigit(ch) ? ch : '-')).Trim('-');
        while (slug.Contains("--", StringComparison.Ordinal))
            slug = slug.Replace("--", "-", StringComparison.Ordinal);
        return slug.Length <= maximumLength ? slug : slug[..maximumLength].Trim('-');
    }
}
