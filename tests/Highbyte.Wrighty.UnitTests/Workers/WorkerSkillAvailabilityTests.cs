using System.Diagnostics;
using Highbyte.Wrighty.Errors;
using Highbyte.Wrighty.Processes;
using Highbyte.Wrighty.Workers;

namespace Highbyte.Wrighty.UnitTests.Workers;

public sealed class WorkerSkillAvailabilityTests : IDisposable
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(),
        $"wrighty-worker-skill-tests-{Guid.NewGuid():N}");

    [Theory]
    [InlineData("claude", ".claude")]
    [InlineData("codex", ".agents")]
    [InlineData("copilot", ".agents")]
    public void User_scoped_skill_is_available_without_a_project_copy(
        string agentType,
        string skillRoot)
    {
        var home = Path.Combine(root, "home");
        var skill = Path.Combine(home, skillRoot, "skills", "wrighty", "SKILL.md");
        Directory.CreateDirectory(Path.GetDirectoryName(skill)!);
        File.WriteAllText(skill, "# Wrighty");
        var availability = new FileWorkerSkillAvailability(
            new ThrowingExecutableResolver(),
            home);

        availability.EnsureWorktreeReady(agentType, Path.Combine(root, "not-a-repository"));
    }

    [Fact]
    public void Ignored_project_skill_is_rejected_until_it_is_committed()
    {
        var repository = Path.Combine(root, "repo");
        var home = Path.Combine(root, "home");
        Directory.CreateDirectory(repository);
        RunGit(repository, "init", "-q", "-b", "main");
        RunGit(repository, "config", "user.name", "Wrighty test");
        RunGit(repository, "config", "user.email", "wrighty@example.invalid");
        File.WriteAllText(Path.Combine(repository, ".gitignore"), ".claude/\n");
        RunGit(repository, "add", ".gitignore");
        RunGit(repository, "commit", "-q", "-m", "Initialize");

        var skill = Path.Combine(repository, ".claude", "skills", "wrighty", "SKILL.md");
        Directory.CreateDirectory(Path.GetDirectoryName(skill)!);
        File.WriteAllText(skill, "# Wrighty");
        var availability = new FileWorkerSkillAvailability(
            new PathExecutableResolver(),
            home);

        var unavailable = Assert.Throws<TrackerException>(() =>
            availability.EnsureWorktreeReady("claude", repository));

        Assert.Equal("WORKER_SKILL_UNAVAILABLE", unavailable.Code);
        RunGit(repository, "add", "-f", ".claude/skills/wrighty/SKILL.md");
        RunGit(repository, "commit", "-q", "-m", "Add Wrighty skill");

        availability.EnsureWorktreeReady("claude", repository);
    }

    private static void RunGit(string workingDirectory, params string[] arguments)
    {
        var start = new ProcessStartInfo
        {
            FileName = new PathExecutableResolver().Resolve("git"),
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(
            process.ExitCode == 0,
            $"git {string.Join(' ', arguments)} failed: {output}{error}");
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
            Directory.Delete(root, true);
    }

    private sealed class ThrowingExecutableResolver : IExecutableResolver
    {
        public string Resolve(string executableName) =>
            throw new Xunit.Sdk.XunitException("Git should not be used for a user-scoped skill.");
    }
}
