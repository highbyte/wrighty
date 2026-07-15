using Highbyte.Wrighty.Cli.Skills;
using Highbyte.Wrighty.Errors;

namespace Highbyte.Wrighty.UnitTests.Skills;

public sealed class SkillManagerTests : IDisposable
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(),
        $"wrighty-skill-tests-{Guid.NewGuid():N}");

    [Fact]
    public async Task Install_all_creates_two_current_host_installations_idempotently()
    {
        var manager = Manager();

        var installed = await manager.InstallAsync(
            "all", SkillScope.Project, root, root, false, CancellationToken.None);
        var repeated = await manager.InstallAsync(
            "all", SkillScope.Project, root, root, false, CancellationToken.None);

        Assert.Equal(2, installed.Count);
        Assert.All(installed, result => Assert.True(result.Changed));
        Assert.All(repeated, result =>
        {
            Assert.False(result.Changed);
            Assert.Equal(SkillInstallationState.Current, result.State);
            Assert.Equal("project", result.Scope);
        });
        Assert.True(File.Exists(Path.Combine(
            root, ".agents", "skills", SkillManager.SkillName, "SKILL.md")));
        Assert.True(File.Exists(Path.Combine(
            root, ".claude", "skills", SkillManager.SkillName, "SKILL.md")));
        Assert.Contains(
            "disable-model-invocation: true",
            await File.ReadAllTextAsync(Path.Combine(
                root, ".claude", "skills", SkillManager.SkillName, "SKILL.md")));
    }

    [Fact]
    public async Task Update_requires_force_for_modified_mechanics_and_preserves_description()
    {
        var manager = Manager();
        await manager.InstallAsync(
            "codex", SkillScope.Project, root, root, false, CancellationToken.None);
        var skillPath = Path.Combine(root, ".agents", "skills", SkillManager.SkillName, "SKILL.md");
        var workflowPath = Path.Combine(
            root, ".agents", "skills", SkillManager.SkillName, "references", "workflow.md");
        var skill = await File.ReadAllTextAsync(skillPath);
        var descriptionStart = skill.IndexOf("description:", StringComparison.Ordinal);
        var descriptionEnd = skill.IndexOf('\n', descriptionStart);
        skill = skill[..descriptionStart] +
                "description: \"Only when explicitly requested. Use the Wrighty CLI.\"" +
                skill[descriptionEnd..];
        await File.WriteAllTextAsync(skillPath, skill);
        await File.AppendAllTextAsync(workflowPath, "\nlocal modification\n");

        var exception = await Assert.ThrowsAsync<TrackerException>(() => manager.UpdateAsync(
            "codex", SkillScope.Project, root, root, false, CancellationToken.None));
        var updated = await manager.UpdateAsync(
            "codex", SkillScope.Project, root, root, true, CancellationToken.None);

        Assert.Equal("SKILL_MODIFIED", exception.Code);
        Assert.True(Assert.Single(updated).DescriptionPreserved);
        Assert.Contains("Only when explicitly requested", await File.ReadAllTextAsync(skillPath));
        Assert.DoesNotContain("local modification", await File.ReadAllTextAsync(workflowPath));
    }

    [Fact]
    public async Task Check_is_read_only_and_reports_missing()
    {
        var manager = Manager();

        var result = Assert.Single(await manager.CheckAsync(
            "claude", SkillScope.Project, root, root, CancellationToken.None));

        Assert.Equal(SkillInstallationState.Missing, result.State);
        Assert.False(Directory.Exists(Path.Combine(root, ".claude")));
    }

    private SkillManager Manager() => new(
        Path.Combine(AppContext.BaseDirectory, "skills", SkillManager.SkillName),
        Path.Combine(root, "home"));

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}
