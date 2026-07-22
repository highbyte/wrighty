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
    public async Task Bundled_skill_describes_collaborative_creation_and_explicit_worker_opt_in()
    {
        var skillRoot = Path.Combine(
            AppContext.BaseDirectory,
            "skills",
            SkillManager.SkillName);
        var skill = await File.ReadAllTextAsync(Path.Combine(skillRoot, "SKILL.md"));
        var workflow = await File.ReadAllTextAsync(
            Path.Combine(skillRoot, "references", "workflow.md"));

        Assert.Contains("settle the exact title, body, and metadata before", skill);
        Assert.Contains("Pass `--auto` only when the user explicitly authorizes", skill);
        Assert.Contains("separate collaborative authoring from the tracked mutation", workflow);
        Assert.Contains("show the proposed title and body before creating it", workflow);
        Assert.Contains("A preferred agent does not imply `--auto`", workflow);
        Assert.Contains("Draft-first is the default", workflow);
        Assert.Contains("worker eligibility and preferred agent are ordinary claim-aware edits", workflow);
        Assert.Contains("always acquire ordinary work", skill);
        Assert.Contains("Never pass `--claimant-kind human` merely because the user requested", skill);
        Assert.Contains("The AI session is still the claimant executing the mutation", workflow);
        Assert.Contains("first acquire it with", workflow);
        Assert.Contains("wrighty claim <id> --claimant-kind agent --json", workflow);
        Assert.Contains("offer three choices", skill);
        Assert.Contains("Never reduce this decision to a yes/no", skill);
        Assert.Contains("Start implementation in this session", workflow);
        Assert.Contains("Do not invoke `wrighty worker`, `claude`, `codex`, or", workflow);
        Assert.Contains("result.worker.defaultAgent", workflow);
        Assert.Contains("Use repository default (<vendor>)", workflow);
        Assert.Contains("Do nothing for now", workflow);
    }

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
        Assert.All(installed, result => Assert.Equal("0.9.0", result.Version));
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
        var manifest = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(
            root,
            ".agents",
            "skills",
            SkillManager.SkillName,
            ".wrighty-skill.json")))!.AsObject();
        Assert.Equal("0.9.0", manifest["skillVersion"]!.GetValue<string>());
    }

    [Fact]
    public async Task Install_derives_the_manifest_version_from_the_bundled_skill_marker()
    {
        var assets = Path.Combine(root, "assets");
        CopyDirectory(
            Path.Combine(AppContext.BaseDirectory, "skills", SkillManager.SkillName),
            assets);
        var skillPath = Path.Combine(assets, "SKILL.md");
        var skill = await File.ReadAllTextAsync(skillPath);
        await File.WriteAllTextAsync(
            skillPath,
            skill.Replace(
                "<!-- wrighty-skill-version: 0.9.0 -->",
                "<!-- wrighty-skill-version: 7.8.9-beta.1+build.2 -->",
                StringComparison.Ordinal));
        var manager = new SkillManager(assets, Path.Combine(root, "home"));

        var result = Assert.Single(await manager.InstallAsync(
            "codex", SkillScope.Project, root, root, false, CancellationToken.None));
        var manifestPath = Path.Combine(
            root,
            ".agents",
            "skills",
            SkillManager.SkillName,
            ".wrighty-skill.json");
        var manifest = JsonNode.Parse(await File.ReadAllTextAsync(manifestPath))!.AsObject();

        Assert.Equal("7.8.9-beta.1+build.2", result.Version);
        Assert.Equal("7.8.9-beta.1+build.2", manifest["skillVersion"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("")]
    [InlineData("<!-- wrighty-skill-version: 1.2 -->")]
    [InlineData("<!-- wrighty-skill-version: 01.2.3 -->")]
    [InlineData("<!-- wrighty-skill-version: 1.2.3-01 -->")]
    [InlineData("<!-- wrighty-skill-version: 1.2.3 -->\n<!-- wrighty-skill-version: 1.2.4 -->")]
    public async Task Operations_reject_invalid_or_duplicate_bundled_skill_version_markers(
        string replacement)
    {
        var assets = Path.Combine(root, "assets");
        CopyDirectory(
            Path.Combine(AppContext.BaseDirectory, "skills", SkillManager.SkillName),
            assets);
        var skillPath = Path.Combine(assets, "SKILL.md");
        var skill = await File.ReadAllTextAsync(skillPath);
        await File.WriteAllTextAsync(
            skillPath,
            skill.Replace(
                "<!-- wrighty-skill-version: 0.9.0 -->",
                replacement,
                StringComparison.Ordinal));
        var manager = new SkillManager(assets, Path.Combine(root, "home"));

        var exception = await Assert.ThrowsAsync<TrackerException>(() => manager.CheckAsync(
            "codex", SkillScope.Project, root, root, CancellationToken.None));

        Assert.Equal("SKILL_ASSETS_INVALID", exception.Code);
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

    [Theory]
    [InlineData("codex", ".agents")]
    [InlineData("claude", ".claude")]
    public async Task Update_detects_changed_bundled_assets_without_a_version_change(
        string agent,
        string agentDirectory)
    {
        var assets = Path.Combine(root, "assets");
        CopyDirectory(
            Path.Combine(AppContext.BaseDirectory, "skills", SkillManager.SkillName),
            assets);
        var manager = new SkillManager(assets, Path.Combine(root, "home"));
        await manager.InstallAsync(agent, SkillScope.Project, root, root, false, CancellationToken.None);
        var changedContent = $"{Environment.NewLine}same-version bundled update{Environment.NewLine}";
        await File.AppendAllTextAsync(Path.Combine(assets, "references", "workflow.md"), changedContent);

        var checkedResult = Assert.Single(await manager.CheckAsync(
            agent, SkillScope.Project, root, root, CancellationToken.None));
        var updated = Assert.Single(await manager.UpdateAsync(
            agent, SkillScope.Project, root, root, false, CancellationToken.None));
        var installedWorkflow = Path.Combine(
            root,
            agentDirectory,
            "skills",
            SkillManager.SkillName,
            "references",
            "workflow.md");
        var currentResult = Assert.Single(await manager.CheckAsync(
            agent, SkillScope.Project, root, root, CancellationToken.None));

        Assert.Equal(SkillInstallationState.Outdated, checkedResult.State);
        Assert.True(updated.Changed);
        Assert.Equal(SkillInstallationState.Outdated, updated.PreviousState);
        Assert.Contains("same-version bundled update", await File.ReadAllTextAsync(installedWorkflow));
        Assert.Equal(SkillInstallationState.Current, currentResult.State);
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

    private static void CopyDirectory(string source, string destination)
    {
        foreach (var sourceFile in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var destinationFile = Path.Combine(destination, Path.GetRelativePath(source, sourceFile));
            Directory.CreateDirectory(Path.GetDirectoryName(destinationFile)!);
            File.Copy(sourceFile, destinationFile);
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}
