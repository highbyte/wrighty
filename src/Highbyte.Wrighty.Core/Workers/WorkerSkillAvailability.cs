using System.Diagnostics;
using Highbyte.Wrighty.Errors;
using Highbyte.Wrighty.Processes;

namespace Highbyte.Wrighty.Workers;

public interface IWorkerSkillAvailability
{
    void EnsureWorktreeReady(
        string agentType,
        string repositoryPath,
        string? existingWorkspacePath = null);
}

public sealed class FileWorkerSkillAvailability(
    IExecutableResolver executables,
    string? userHome = null) : IWorkerSkillAvailability
{
    private readonly string home = userHome ??
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public void EnsureWorktreeReady(
        string agentType,
        string repositoryPath,
        string? existingWorkspacePath = null)
    {
        var relativeSkillPath = RelativeSkillPath(agentType);
        var userSkillPath = Path.Combine(home, relativeSkillPath);
        if (File.Exists(userSkillPath))
            return;

        if (!string.IsNullOrWhiteSpace(existingWorkspacePath) &&
            File.Exists(Path.Combine(existingWorkspacePath, relativeSkillPath)))
            return;

        var repository = Path.GetFullPath(repositoryPath);
        if (ExistsAtHead(repository, relativeSkillPath))
            return;

        var projectSkillPath = Path.Combine(repository, relativeSkillPath);
        throw new TrackerException(
            "WORKER_SKILL_UNAVAILABLE",
            $"The Wrighty skill for {agentType} will not be available in a new worktree. " +
            $"Commit '{relativeSkillPath}' to the repository or install the user-scoped skill with " +
            $"'wrighty skill update --agent {agentType} --scope user'.",
            9,
            new Dictionary<string, object?>
            {
                ["agentType"] = agentType,
                ["projectSkillPath"] = projectSkillPath,
                ["userSkillPath"] = userSkillPath
            });
    }

    private bool ExistsAtHead(string repositoryPath, string relativeSkillPath)
    {
        try
        {
            var start = new ProcessStartInfo
            {
                FileName = executables.Resolve("git"),
                WorkingDirectory = repositoryPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            start.ArgumentList.Add("cat-file");
            start.ArgumentList.Add("-e");
            start.ArgumentList.Add($"HEAD:{relativeSkillPath.Replace('\\', '/')}");
            using var process = Process.Start(start)
                ?? throw new InvalidOperationException("Could not start git.");
            process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            process.WaitForExit();
            return process.ExitCode == 0;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            throw new TrackerException(
                "WORKER_SKILL_CHECK_FAILED",
                $"Could not verify worktree skill availability in '{repositoryPath}': {exception.Message}",
                9,
                new Dictionary<string, object?> { ["repositoryPath"] = repositoryPath },
                exception);
        }
    }

    private static string RelativeSkillPath(string agentType) =>
        agentType.ToLowerInvariant() switch
        {
            "claude" => Path.Combine(".claude", "skills", "wrighty", "SKILL.md"),
            "codex" or "copilot" => Path.Combine(".agents", "skills", "wrighty", "SKILL.md"),
            _ => throw new TrackerException(
                "AGENT_UNSUPPORTED",
                $"Unsupported worker agent '{agentType}'.",
                3)
        };
}

internal sealed class NoOpWorkerSkillAvailability : IWorkerSkillAvailability
{
    public static NoOpWorkerSkillAvailability Instance { get; } = new();

    public void EnsureWorktreeReady(
        string agentType,
        string repositoryPath,
        string? existingWorkspacePath = null)
    {
    }
}
