using System.Text.Json;
using Highbyte.Wrighty.Configuration;
using Highbyte.Wrighty.Errors;

namespace Highbyte.Wrighty.UnitTests.Configuration;

public sealed class RepositoryConfigurationServiceTests : IDisposable
{
    private readonly string directory = Path.Combine(
        Path.GetTempPath(),
        $"wrighty-config-service-{Guid.NewGuid():N}");

    private string PathName => Path.Combine(directory, TrackerConfigLoader.FileName);

    [Fact]
    public async Task Read_reports_revision_implicit_schema_and_stored_vs_effective_values()
    {
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(PathName, """
            {
              "backend": "local-markdown",
              "localMarkdown": { "path": "items" },
              "worker": { "defaultAgent": "codex" }
            }
            """);
        var service = Service();

        var result = await service.ReadPathAsync(PathName, CancellationToken.None);

        Assert.Equal(TrackerConfigLoader.CurrentSchemaVersion, result.SchemaVersion);
        Assert.False(result.SchemaVersionWasExplicit);
        Assert.Equal(
            RepositoryConfigurationService.Revision(await File.ReadAllBytesAsync(PathName)),
            result.Revision);
        Assert.Equal(result.Revision, result.StoredConfiguration.SourceRevision);
        var defaultAgent = Assert.Single(result.Settings, value => value.Id == "worker.defaultAgent");
        Assert.Equal("codex", defaultAgent.StoredValue);
        Assert.Equal("codex", defaultAgent.EffectiveValue);
        var workspace = Assert.Single(result.Settings, value => value.Id == "worker.workspaceMode");
        Assert.Null(workspace.StoredValue);
        Assert.Equal("current", workspace.EffectiveValue);
        Assert.Equal("wrighty-default", workspace.DefaultSource);
    }

    [Fact]
    public async Task Read_reports_all_unknown_properties_before_typed_loading()
    {
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(PathName, """
            {
              "backend": "local-markdown",
              "futureRoot": true,
              "localMarkdown": {
                "path": "items",
                "futureLocal": "value"
              },
              "worker": {
                "futureWorker": 1,
                "agents": {
                  "codex": { "permissions": "workspace", "futureAgent": true }
                }
              }
            }
            """);

        var exception = await Assert.ThrowsAsync<TrackerException>(
            () => Service().ReadPathAsync(PathName, CancellationToken.None));

        Assert.Equal("CONFIG_UNKNOWN_PROPERTIES", exception.Code);
        var properties = Assert.IsAssignableFrom<IReadOnlyList<string>>(
            exception.Details["unknownProperties"]);
        Assert.Contains("futureRoot", properties);
        Assert.Contains("localMarkdown.futureLocal", properties);
        Assert.Contains("worker.futureWorker", properties);
        Assert.Contains("worker.agents.codex.futureAgent", properties);
    }

    [Fact]
    public async Task Read_catalogues_the_Claude_desktop_session_setting_from_main()
    {
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(PathName, """
            {
              "backend": "local-markdown",
              "localMarkdown": { "path": "items" },
              "worker": {
                "desktopSessions": {
                  "claude": "experimental"
                }
              }
            }
            """);

        var result = await Service().ReadPathAsync(PathName, CancellationToken.None);

        var setting = Assert.Single(
            result.Settings,
            value => value.Id == "worker.desktopSessions.claude");
        Assert.Equal("experimental", setting.StoredValue);
        Assert.Equal("experimental", setting.EffectiveValue);
        Assert.Equal(ConfigurationEditMode.ReadOnly, setting.EditMode);
        Assert.Equal(
            ConfigurationEffectiveBoundary.NewWebProcess,
            setting.EffectiveBoundary);
    }

    [Fact]
    public async Task Mutation_is_revision_checked_and_validates_before_writing()
    {
        await WriteValidAsync();
        var service = Service();
        var snapshot = await service.ReadPathAsync(PathName, CancellationToken.None);
        await File.AppendAllTextAsync(PathName, Environment.NewLine);

        var conflict = await Assert.ThrowsAsync<TrackerException>(() =>
            service.MutateAsync(
                PathName,
                snapshot.Revision,
                new WorkerDefaultsMutation(true, "codex", "worktree"),
                approveCanonicalization: false,
                dryRun: false,
                CancellationToken.None));

        Assert.Equal("CONFIG_CONFLICT", conflict.Code);

        snapshot = await service.ReadPathAsync(PathName, CancellationToken.None);
        var original = await File.ReadAllTextAsync(PathName);
        var invalid = await Assert.ThrowsAsync<TrackerException>(() =>
            service.MutateAsync(
                PathName,
                snapshot.Revision,
                new WorkerDefaultsMutation(true, "other", null),
                approveCanonicalization: false,
                dryRun: false,
                CancellationToken.None));
        Assert.Equal("CONFIG_INVALID", invalid.Code);
        Assert.Equal(original, await File.ReadAllTextAsync(PathName));
    }

    [Fact]
    public async Task Dry_run_previews_comments_but_save_requires_approval_and_writes_schema()
    {
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(PathName, """
            {
              // hand-authored policy
              "backend": "local-markdown",
              "localMarkdown": {
                "path": "items",
              },
            }
            """);
        var service = Service();
        var snapshot = await service.ReadPathAsync(PathName, CancellationToken.None);
        Assert.True(snapshot.ContainsComments);
        Assert.True(snapshot.ContainsTrailingCommas);

        var preview = await service.MutateAsync(
            PathName,
            snapshot.Revision,
            new WebPolicyMutation(false),
            approveCanonicalization: false,
            dryRun: true,
            CancellationToken.None);

        Assert.False(preview.Saved);
        Assert.Contains(preview.Changes, change => change.Id == "web.protectNonHumanClaims");
        Assert.Contains("// hand-authored", await File.ReadAllTextAsync(PathName));

        var confirmation = await Assert.ThrowsAsync<TrackerException>(() =>
            service.MutateAsync(
                PathName,
                snapshot.Revision,
                new WebPolicyMutation(false),
                approveCanonicalization: false,
                dryRun: false,
                CancellationToken.None));
        Assert.Equal("CONFIG_CANONICALIZATION_CONFIRMATION_REQUIRED", confirmation.Code);

        var saved = await service.MutateAsync(
            PathName,
            snapshot.Revision,
            new WebPolicyMutation(false),
            approveCanonicalization: true,
            dryRun: false,
            CancellationToken.None);

        Assert.True(saved.Saved);
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(PathName));
        Assert.Equal(
            TrackerConfigLoader.CurrentSchemaVersion,
            document.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.False(document.RootElement.GetProperty("web")
            .GetProperty("protectNonHumanClaims").GetBoolean());
        Assert.DoesNotContain("// hand-authored", await File.ReadAllTextAsync(PathName));
    }

    [Fact]
    public async Task Loader_rejects_newer_schema_and_save_writes_current_schema()
    {
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(PathName, """
            {
              "schemaVersion": 99,
              "backend": "local-markdown",
              "localMarkdown": {}
            }
            """);

        var exception = await Assert.ThrowsAsync<TrackerException>(() =>
            new TrackerConfigLoader().LoadAsync(directory, CancellationToken.None));
        Assert.Equal("CONFIG_VERSION_UNSUPPORTED", exception.Code);

        await new TrackerConfigLoader().SaveAsync(
            PathName,
            new TrackerConfig
            {
                Backend = "local-markdown",
                LocalMarkdown = new LocalMarkdownBackendConfig()
            },
            CancellationToken.None);
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(PathName));
        Assert.Equal(1, document.RootElement.GetProperty("schemaVersion").GetInt32());
    }

    private RepositoryConfigurationService Service() => new(new TrackerConfigLoader());

    private async Task WriteValidAsync()
    {
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(PathName, """
            {
              "backend": "local-markdown",
              "localMarkdown": { "path": "items" }
            }
            """);
    }

    public void Dispose()
    {
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }
}
