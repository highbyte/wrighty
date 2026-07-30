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
    public async Task Read_accepts_a_file_written_by_an_earlier_wrighty_and_names_its_legacy_values()
    {
        // Through v0.9.1-alpha the computed worker effective* getters lacked [JsonIgnore], so every
        // config write persisted their derived values into the file. Rejecting them as unknown
        // would refuse this tool's own previous output and read as the operator's mistake — they
        // are reported as migratable instead, and never fail the read.
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(PathName, """
            {
              "backend": "local-markdown",
              "localMarkdown": { "path": "items" },
              "worker": {
                "defaultAgent": "codex",
                "effectiveUsageFailure": { "action": "retry", "maxAttempts": 3 },
                "effectiveSessionReportMode": "off",
                "effectiveContext": { "maxDiscussionComments": 100 },
                "effectiveHandoverComment": "Full"
              }
            }
            """);

        var result = await Service().ReadPathAsync(PathName, CancellationToken.None);

        Assert.Empty(result.UnknownProperties);
        Assert.Equal(
            [
                "worker.effectiveUsageFailure",
                "worker.effectiveSessionReportMode",
                "worker.effectiveContext",
                "worker.effectiveHandoverComment"
            ],
            result.LegacyProperties);
        // The legacy values are serialized defaults, not settings: nothing inside them may leak
        // into the catalogue or change an effective value.
        var defaultAgent = Assert.Single(result.Settings, value => value.Id == "worker.defaultAgent");
        Assert.Equal("codex", defaultAgent.StoredValue);
    }

    [Fact]
    public async Task A_genuinely_unknown_property_still_fails_even_beside_legacy_ones()
    {
        // The tolerance is a named list, not a softening: anything not on it fails exactly as
        // before, and the failure names only the genuinely unknown properties so the operator is
        // not sent chasing values wrighty wrote itself.
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(PathName, """
            {
              "backend": "local-markdown",
              "localMarkdown": { "path": "items" },
              "worker": {
                "effectiveHandoverComment": "Full",
                "futureWorker": 1
              }
            }
            """);

        var exception = await Assert.ThrowsAsync<TrackerException>(
            () => Service().ReadPathAsync(PathName, CancellationToken.None));

        Assert.Equal("CONFIG_UNKNOWN_PROPERTIES", exception.Code);
        var properties = Assert.IsType<IReadOnlyList<string>>(
            exception.Details["unknownProperties"], exactMatch: false);
        Assert.Equal(["worker.futureWorker"], properties);
    }

    [Fact]
    public async Task Saving_a_legacy_file_drops_the_legacy_values_and_keeps_the_settings()
    {
        // The migration itself: the canonical writer serializes the typed configuration, where the
        // legacy properties no longer exist, so any successful save heals the file. The real
        // settings survive untouched.
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(PathName, """
            {
              "backend": "local-markdown",
              "localMarkdown": { "path": "items" },
              "worker": {
                "defaultAgent": "codex",
                "effectiveUsageFailure": { "action": "retry" },
                "effectiveHandoverComment": "Full"
              }
            }
            """);
        var service = Service();
        var snapshot = await service.ReadPathAsync(PathName, CancellationToken.None);

        await service.MutateAsync(
            PathName,
            snapshot.Revision,
            new WorkerDefaultsMutation(true, "codex", "worktree"),
            approveCanonicalization: false,
            dryRun: false,
            CancellationToken.None);

        var written = await File.ReadAllTextAsync(PathName);
        Assert.DoesNotContain("effective", written, StringComparison.OrdinalIgnoreCase);
        var healed = await service.ReadPathAsync(PathName, CancellationToken.None);
        Assert.Empty(healed.LegacyProperties ?? []);
        var defaultAgent = Assert.Single(healed.Settings, value => value.Id == "worker.defaultAgent");
        Assert.Equal("codex", defaultAgent.StoredValue);
    }

    [Fact]
    public async Task Saving_without_changing_anything_still_removes_legacy_values()
    {
        // What an operator actually does after reading "saving removes them": press Save on a
        // section without editing a field. Writing only when a value changed made that a no-op, so
        // the notice survived every save and the migration depended on an unrelated edit.
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(PathName, """
            {
              "backend": "local-markdown",
              "localMarkdown": { "path": "items" },
              "worker": {
                "defaultAgent": "codex",
                "workspaceMode": "worktree",
                "effectiveUsageFailure": { "action": "retry" },
                "effectiveHandoverComment": "Full"
              }
            }
            """);
        var service = Service();
        var snapshot = await service.ReadPathAsync(PathName, CancellationToken.None);

        // Exactly the values already stored: the submission a Save button makes untouched.
        var result = await service.MutateAsync(
            PathName,
            snapshot.Revision,
            new WorkerDefaultsMutation(true, "codex", "worktree"),
            approveCanonicalization: false,
            dryRun: false,
            CancellationToken.None);

        Assert.Empty(result.Changes);
        Assert.True(result.Saved);
        Assert.Equal(
            ["worker.effectiveUsageFailure", "worker.effectiveHandoverComment"],
            result.MigratedLegacyProperties);
        // Dropping ignored values changes nothing a running process would do.
        Assert.False(result.RestartRequired);

        var written = await File.ReadAllTextAsync(PathName);
        Assert.DoesNotContain("effective", written, StringComparison.OrdinalIgnoreCase);
        var healed = await service.ReadPathAsync(PathName, CancellationToken.None);
        Assert.Empty(healed.LegacyProperties ?? []);
        var defaultAgent = Assert.Single(healed.Settings, value => value.Id == "worker.defaultAgent");
        Assert.Equal("codex", defaultAgent.StoredValue);
    }

    [Fact]
    public async Task Saving_an_unchanged_file_with_nothing_to_migrate_writes_nothing()
    {
        // The other half: the write must stay gated. A save that changes no value and has no
        // legacy values to drop leaves the file untouched, so an idle Save cannot churn its
        // revision and invalidate another editor's in-flight one.
        await WriteValidAsync();
        var service = Service();
        var snapshot = await service.ReadPathAsync(PathName, CancellationToken.None);
        var original = await File.ReadAllTextAsync(PathName);

        var result = await service.MutateAsync(
            PathName,
            snapshot.Revision,
            new WorkerDefaultsMutation(
                true,
                snapshot.StoredConfiguration.EffectiveWorker.DefaultAgent,
                snapshot.StoredConfiguration.EffectiveWorker.WorkspaceMode),
            approveCanonicalization: false,
            dryRun: false,
            CancellationToken.None);

        Assert.Empty(result.Changes);
        Assert.False(result.Saved);
        Assert.Empty(result.MigratedLegacyProperties ?? []);
        Assert.Equal(original, await File.ReadAllTextAsync(PathName));
        Assert.Equal(snapshot.Revision, (await service.ReadPathAsync(
            PathName, CancellationToken.None)).Revision);
    }

    [Fact]
    public async Task A_dry_run_reports_the_pending_migration_without_writing()
    {
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(PathName, """
            {
              "backend": "local-markdown",
              "localMarkdown": { "path": "items" },
              "worker": { "effectiveHandoverComment": "Full" }
            }
            """);
        var service = Service();
        var snapshot = await service.ReadPathAsync(PathName, CancellationToken.None);
        var original = await File.ReadAllTextAsync(PathName);

        var result = await service.MutateAsync(
            PathName,
            snapshot.Revision,
            new WorkerDefaultsMutation(false, null, null),
            approveCanonicalization: false,
            dryRun: true,
            CancellationToken.None);

        Assert.False(result.Saved);
        Assert.Equal(["worker.effectiveHandoverComment"], result.MigratedLegacyProperties);
        Assert.Equal(original, await File.ReadAllTextAsync(PathName));
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

    [Fact]
    public async Task Supported_mutations_update_each_typed_policy_and_report_no_op()
    {
        await WriteValidAsync();
        var service = Service();

        await MutateAsync(new WorkflowDefaultsMutation("Ready", "Doing", "Complete"));
        await MutateAsync(new ArchivePolicyMutation(["Done", "Todo"]));
        await MutateAsync(new WorkerDefaultsMutation(true, " CODEX ", "worktree"));
        await MutateAsync(new CompletionPolicyMutation("agent", "merge-local"));
        await MutateAsync(new WebPolicyMutation(false));

        var updated = await service.ReadPathAsync(PathName, CancellationToken.None);
        Assert.Equal("Ready", updated.StoredConfiguration.DefaultPickFrom);
        Assert.Equal("Doing", updated.StoredConfiguration.DefaultPickTo);
        Assert.Equal("Complete", updated.StoredConfiguration.DefaultFinishTo);
        Assert.Equal(["Done", "Todo"], updated.StoredConfiguration.Archive.OnStatuses);
        Assert.Equal("codex", updated.StoredConfiguration.EffectiveWorker.DefaultAgent);
        Assert.Equal("worktree", updated.StoredConfiguration.EffectiveWorker.WorkspaceMode);
        Assert.Equal("agent", updated.StoredConfiguration.EffectiveWorker.Completion?.Commit);
        Assert.Equal(
            "merge-local",
            updated.StoredConfiguration.EffectiveWorker.Completion?.Integration);
        Assert.False(updated.StoredConfiguration.EffectiveWeb.ProtectNonHumanClaims);

        var noOp = await service.MutateAsync(
            PathName,
            updated.Revision,
            new WorkflowDefaultsMutation("Ready", null, null),
            approveCanonicalization: false,
            dryRun: false,
            CancellationToken.None);
        Assert.False(noOp.Saved);
        Assert.False(noOp.RestartRequired);
        Assert.Empty(noOp.Changes);

        async Task MutateAsync(RepositoryConfigurationMutation mutation)
        {
            var before = await service.ReadPathAsync(PathName, CancellationToken.None);
            var result = await service.MutateAsync(
                PathName,
                before.Revision,
                mutation,
                approveCanonicalization: false,
                dryRun: false,
                CancellationToken.None);
            Assert.True(result.Saved);
            Assert.True(result.RestartRequired);
            Assert.NotEmpty(result.Changes);
        }
    }

    [Fact]
    public async Task GitHub_configuration_catalogues_backend_specific_settings_and_sensitivity()
    {
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(PathName, """
            {
              "backend": "github",
              "github": {
                "repository": "owner/repo",
                "projectNumber": 12,
                "trustedCommentAuthors": ["owner"]
              },
              "worker": {
                "agents": {
                  "codex": { "permissions": "full" }
                }
              }
            }
            """);

        var result = await Service().ReadPathAsync(PathName, CancellationToken.None);

        Assert.Contains(result.Settings, setting =>
            setting.Id == "github.repository" &&
            Equals(setting.StoredValue, "owner/repo") &&
            setting.EditMode == ConfigurationEditMode.MigrationOnly);
        Assert.Contains(result.Settings, setting =>
            setting.Id == "github.claimHistoryLimit" &&
            setting.RequiresQuiescence);
        Assert.Contains(result.Settings, setting =>
            setting.Id == "github.trustedCommentAuthors" &&
            setting.Sensitivity is not null);
        Assert.Contains(result.Settings, setting =>
            setting.Id == "worker.agents.codex.permissions" &&
            Equals(setting.EffectiveValue, "full") &&
            setting.Sensitivity is not null);
        Assert.DoesNotContain(result.Settings, setting =>
            setting.Id.StartsWith("localMarkdown.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Read_reports_missing_and_malformed_configuration_with_stable_codes()
    {
        var missing = await Assert.ThrowsAsync<TrackerException>(
            () => Service().ReadPathAsync(PathName, CancellationToken.None));
        Assert.Equal("CONFIG_NOT_FOUND", missing.Code);
        Assert.Equal(Path.GetFullPath(PathName), missing.Details["configPath"]);

        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(PathName, "{ invalid");
        var malformed = await Assert.ThrowsAsync<TrackerException>(
            () => Service().ReadPathAsync(PathName, CancellationToken.None));
        Assert.Equal("CONFIG_INVALID", malformed.Code);
        Assert.Equal(Path.GetFullPath(PathName), malformed.Details["configPath"]);
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
