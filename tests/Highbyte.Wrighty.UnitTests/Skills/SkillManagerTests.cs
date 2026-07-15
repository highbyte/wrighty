using Highbyte.Wrighty.Cli.Skills;
using Highbyte.Wrighty.Errors;
using System.Text.Json;
using System.Text.Json.Nodes;

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

    [Fact]
    public async Task Install_supports_user_scope_and_discovers_project_root()
    {
        var manager = Manager();
        var project = Path.Combine(root, "project");
        var nested = Path.Combine(project, "src", "nested");
        Directory.CreateDirectory(Path.Combine(project, ".git"));
        Directory.CreateDirectory(nested);

        var projectResult = Assert.Single(await manager.InstallAsync(
            "codex", SkillScope.Project, nested, null, false, CancellationToken.None));
        var userResult = Assert.Single(await manager.InstallAsync(
            "claude", SkillScope.User, nested, null, false, CancellationToken.None));

        Assert.Equal(Path.Combine(project, ".agents", "skills", "wrighty"), projectResult.Path);
        Assert.Equal("project", projectResult.Scope);
        Assert.Equal(Path.Combine(root, "home", ".claude", "skills", "wrighty"), userResult.Path);
        Assert.Equal("user", userResult.Scope);
    }

    [Theory]
    [InlineData("auto", "SKILL_AGENT_REQUIRED")]
    [InlineData("unknown", "ARGUMENT_INVALID")]
    public async Task Operations_reject_unsupported_agent_selection(string agent, string expectedCode)
    {
        var exception = await Assert.ThrowsAsync<TrackerException>(() => Manager().CheckAsync(
            agent, SkillScope.Project, root, root, CancellationToken.None));

        Assert.Equal(expectedCode, exception.Code);
    }

    [Fact]
    public async Task Operations_require_packaged_assets()
    {
        var manager = new SkillManager(Path.Combine(root, "missing-assets"), Path.Combine(root, "home"));

        var exception = await Assert.ThrowsAsync<TrackerException>(() => manager.InstallAsync(
            "codex", SkillScope.Project, root, root, false, CancellationToken.None));

        Assert.Equal("SKILL_ASSETS_MISSING", exception.Code);
    }

    [Fact]
    public async Task Install_rejects_malformed_and_modified_existing_destinations()
    {
        var manager = Manager();
        var destination = Path.Combine(root, ".agents", "skills", SkillManager.SkillName);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        await File.WriteAllTextAsync(destination, "not a directory");

        var malformed = await Assert.ThrowsAsync<TrackerException>(() => manager.InstallAsync(
            "codex", SkillScope.Project, root, root, false, CancellationToken.None));
        Assert.Equal("SKILL_ALREADY_EXISTS", malformed.Code);

        File.Delete(destination);
        await manager.InstallAsync("codex", SkillScope.Project, root, root, false, CancellationToken.None);
        await File.AppendAllTextAsync(Path.Combine(destination, "references", "workflow.md"), "modified");
        var modified = await Assert.ThrowsAsync<TrackerException>(() => manager.InstallAsync(
            "codex", SkillScope.Project, root, root, false, CancellationToken.None));
        Assert.Equal("SKILL_MODIFIED", modified.Code);
    }

    [Fact]
    public async Task Update_reports_missing_and_unrecognized_installations()
    {
        var manager = Manager();
        var missing = await Assert.ThrowsAsync<TrackerException>(() => manager.UpdateAsync(
            "codex", SkillScope.Project, root, root, false, CancellationToken.None));
        Assert.Equal("SKILL_NOT_FOUND", missing.Code);

        var destination = Path.Combine(root, ".agents", "skills", SkillManager.SkillName);
        Directory.CreateDirectory(destination);
        await File.WriteAllTextAsync(Path.Combine(destination, "SKILL.md"), "not frontmatter");
        var invalid = await Assert.ThrowsAsync<TrackerException>(() => manager.UpdateAsync(
            "codex", SkillScope.Project, root, root, false, CancellationToken.None));
        Assert.Equal("SKILL_INVALID", invalid.Code);
    }

    [Fact]
    public async Task Update_current_installation_is_idempotent()
    {
        var manager = Manager();
        await manager.InstallAsync("codex", SkillScope.Project, root, root, false, CancellationToken.None);

        var result = Assert.Single(await manager.UpdateAsync(
            "codex", SkillScope.Project, root, root, false, CancellationToken.None));

        Assert.False(result.Changed);
        Assert.True(result.DescriptionPreserved);
        Assert.Equal(SkillInstallationState.Current, result.PreviousState);
    }

    [Fact]
    public async Task Update_replaces_outdated_manifest_and_reports_previous_version()
    {
        var manager = Manager();
        await manager.InstallAsync("codex", SkillScope.Project, root, root, false, CancellationToken.None);
        var manifestPath = Path.Combine(
            root, ".agents", "skills", SkillManager.SkillName, ".wrighty-skill.json");
        var manifest = JsonNode.Parse(await File.ReadAllTextAsync(manifestPath))!.AsObject();
        manifest["skillVersion"] = "0.0.1";
        await File.WriteAllTextAsync(
            manifestPath,
            manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

        var checkedResult = Assert.Single(await manager.CheckAsync(
            "codex", SkillScope.Project, root, root, CancellationToken.None));
        var updated = Assert.Single(await manager.UpdateAsync(
            "codex", SkillScope.Project, root, root, false, CancellationToken.None));

        Assert.Equal(SkillInstallationState.Outdated, checkedResult.State);
        Assert.True(updated.Changed);
        Assert.Equal("0.0.1", updated.PreviousVersion);
        Assert.Equal(SkillInstallationState.Current, updated.State);
    }

    [Fact]
    public async Task Check_reports_malformed_manifest_and_description()
    {
        var manager = Manager();
        await manager.InstallAsync("codex", SkillScope.Project, root, root, false, CancellationToken.None);
        var destination = Path.Combine(root, ".agents", "skills", SkillManager.SkillName);
        var manifestPath = Path.Combine(destination, ".wrighty-skill.json");
        await File.WriteAllTextAsync(manifestPath, "{ invalid");

        var malformedJson = Assert.Single(await manager.CheckAsync(
            "codex", SkillScope.Project, root, root, CancellationToken.None));
        Assert.Equal(SkillInstallationState.Malformed, malformedJson.State);

        Directory.Delete(destination, true);
        await manager.InstallAsync("codex", SkillScope.Project, root, root, false, CancellationToken.None);
        await File.WriteAllTextAsync(Path.Combine(destination, "SKILL.md"), "---\nname: wrong\n---\n");
        var malformedDescription = Assert.Single(await manager.CheckAsync(
            "codex", SkillScope.Project, root, root, CancellationToken.None));
        Assert.Equal(SkillInstallationState.Malformed, malformedDescription.State);
    }

    private SkillManager Manager() => new(
        Path.Combine(AppContext.BaseDirectory, "skills", SkillManager.SkillName),
        Path.Combine(root, "home"));

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}
