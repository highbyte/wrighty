using Highbyte.Wrighty.Settings;
using Highbyte.Wrighty.Workers;

namespace Highbyte.Wrighty.UnitTests.Settings;

public sealed class UserSettingsTests : IDisposable
{
    private readonly string directory =
        Path.Combine(Path.GetTempPath(), $"wrighty-settings-{Guid.NewGuid():N}");

    private UserSettingsStore Store() => new(new UserConfigPaths(directory));

    [Fact]
    public async Task Load_returns_defaults_when_no_settings_file_exists()
    {
        var settings = await Store().LoadAsync(CancellationToken.None);
        Assert.Null(settings.HostLabel);
    }

    [Fact]
    public async Task Save_then_load_round_trips_the_host_label()
    {
        var store = Store();
        await store.SaveAsync(new UserSettings("symbolic-host"), CancellationToken.None);

        var reloaded = await store.LoadAsync(CancellationToken.None);
        Assert.Equal("symbolic-host", reloaded.HostLabel);
        Assert.True(File.Exists(new UserConfigPaths(directory).SettingsPath));
    }

    [Fact]
    public async Task Enabled_agents_round_trip_and_legacy_defaults_follow_detection()
    {
        var defaults = new UserSettings();
        Assert.True(defaults.IsAgentEnabled("claude", detected: true));
        Assert.False(defaults.IsAgentEnabled("claude", detected: false));

        var store = Store();
        await store.SaveAsync(
            defaults with { EnabledAgents = ["codex", "OpenCode"] },
            CancellationToken.None);

        var reloaded = await store.LoadAsync(CancellationToken.None);
        Assert.True(reloaded.IsAgentSelected("CODEX"));
        Assert.False(reloaded.IsAgentEnabled("CODEX", detected: false));
        Assert.True(reloaded.IsAgentEnabled("opencode", detected: true));
        Assert.False(reloaded.IsAgentEnabled("claude", detected: true));
    }

    [Fact]
    public void Explicit_agent_selection_overrides_the_automatic_enablement_allowlist()
    {
        var settings = new UserSettings { EnabledAgents = ["claude"] };

        Assert.False(AgentEnablementPolicy.AllowsManagedWork(
            settings, "opencode", explicitlySelectedAgent: null, detected: true));
        Assert.True(AgentEnablementPolicy.AllowsManagedWork(
            settings, "opencode", explicitlySelectedAgent: "OpenCode", detected: true));
        Assert.False(AgentEnablementPolicy.AllowsManagedWork(
            settings, "opencode", explicitlySelectedAgent: "OpenCode", detected: false));
        Assert.True(AgentEnablementPolicy.AllowsManagedWork(
            settings, "claude", explicitlySelectedAgent: null, detected: true));
    }

    [Fact]
    public async Task Corrupt_settings_file_degrades_to_defaults()
    {
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(
            new UserConfigPaths(directory).SettingsPath, "{ not json", CancellationToken.None);

        var settings = await Store().LoadAsync(CancellationToken.None);
        Assert.Null(settings.HostLabel);
    }

    [Fact]
    public async Task Host_label_provider_falls_back_to_anonymous_placeholder_when_unset()
    {
        var provider = new HostLabelProvider(Store());
        Assert.Equal(
            HostLabelProvider.AnonymousLabel,
            await provider.GetHostLabelAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Host_label_provider_returns_the_configured_label()
    {
        var store = Store();
        await store.SaveAsync(new UserSettings("  redacted-host  "), CancellationToken.None);

        var provider = new HostLabelProvider(store);
        Assert.Equal("redacted-host", await provider.GetHostLabelAsync(CancellationToken.None));
    }

    [Fact]
    public async Task A_version_1_file_migrates_forward_and_is_left_in_place()
    {
        var paths = new UserConfigPaths(directory);
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(
            paths.LegacySettingsPath,
            """{ "version": 1, "hostLabel": "legacy-host" }""",
            CancellationToken.None);

        var migrated = await Store().LoadAsync(CancellationToken.None);

        Assert.Equal("legacy-host", migrated.HostLabel);
        Assert.Empty(migrated.WorkerProfiles);
        // Reading must not consume the old file: an older Wrighty on this machine still needs it.
        Assert.True(File.Exists(paths.LegacySettingsPath));
        Assert.False(File.Exists(paths.SettingsPath));
    }

    [Fact]
    public async Task Saving_after_migration_leaves_the_version_1_file_readable_by_older_builds()
    {
        var paths = new UserConfigPaths(directory);
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(
            paths.LegacySettingsPath,
            """{ "version": 1, "hostLabel": "legacy-host" }""",
            CancellationToken.None);

        var store = Store();
        var migrated = await store.LoadAsync(CancellationToken.None);
        await store.SaveAsync(migrated with { HostLabel = "new-host" }, CancellationToken.None);

        Assert.True(File.Exists(paths.SettingsPath));
        // This is the whole reason v2 is a separate file rather than an in-place upgrade.
        var legacy = await File.ReadAllTextAsync(paths.LegacySettingsPath, CancellationToken.None);
        Assert.Contains("legacy-host", legacy);
        Assert.Equal("new-host", (await store.LoadAsync(CancellationToken.None)).HostLabel);
    }

    [Fact]
    public async Task An_unreadable_version_2_file_does_not_resurrect_version_1_settings()
    {
        var paths = new UserConfigPaths(directory);
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(
            paths.LegacySettingsPath,
            """{ "version": 1, "hostLabel": "stale-host" }""",
            CancellationToken.None);
        await File.WriteAllTextAsync(paths.SettingsPath, "{ not json", CancellationToken.None);

        var settings = await Store().LoadAsync(CancellationToken.None);

        // Falling back to v1 here would restore a label the operator may have since changed.
        Assert.Null(settings.HostLabel);
    }

    [Fact]
    public async Task A_pending_migration_does_not_report_itself_as_absent()
    {
        var paths = new UserConfigPaths(directory);
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(
            paths.LegacySettingsPath,
            """{ "version": 1, "hostLabel": "legacy-host" }""",
            CancellationToken.None);

        var store = Store();

        // Reporting "not present" while a host label is plainly in effect tells the operator two
        // contradictory things at once.
        Assert.True(store.Exists);
        Assert.True(store.AwaitingMigration);
        Assert.Equal("legacy-host", (await store.LoadAsync(CancellationToken.None)).HostLabel);

        await store.SaveAsync(new UserSettings("host"), CancellationToken.None);
        Assert.True(store.Exists);
        Assert.False(store.AwaitingMigration);
    }

    [Fact]
    public async Task Profile_mappings_round_trip_with_vendor_effort_tokens()
    {
        var store = Store();
        await store.SaveAsync(
            new UserSettings("host")
            {
                WorkerProfiles = new Dictionary<string, IReadOnlyDictionary<string, ExecutionProfileMapping>>
                {
                    ["deep"] = new Dictionary<string, ExecutionProfileMapping>
                    {
                        ["claude"] = new() { Model = "opus", Effort = ExecutionEffort.XHigh },
                        // An omitted model deliberately defers to the vendor CLI's own default.
                        ["codex"] = new() { Effort = ExecutionEffort.High }
                    }
                }
            },
            CancellationToken.None);

        var written = await File.ReadAllTextAsync(
            new UserConfigPaths(directory).SettingsPath, CancellationToken.None);
        Assert.Contains("\"xhigh\"", written);

        var reloaded = await store.LoadAsync(CancellationToken.None);
        var claude = reloaded.FindMapping("deep", "claude");
        Assert.Equal("opus", claude!.Model);
        Assert.Equal(ExecutionEffort.XHigh, claude.Effort);
        Assert.Null(reloaded.FindMapping("deep", "codex")!.Model);
        Assert.Null(reloaded.FindMapping("deep", "copilot"));
    }

    [Fact]
    public async Task Profile_and_agent_lookup_is_case_insensitive()
    {
        var store = Store();
        await store.SaveAsync(
            new UserSettings
            {
                WorkerProfiles = new Dictionary<string, IReadOnlyDictionary<string, ExecutionProfileMapping>>
                {
                    ["balanced"] = new Dictionary<string, ExecutionProfileMapping>(
                        StringComparer.OrdinalIgnoreCase)
                    {
                        ["claude"] = new() { Model = "sonnet" }
                    }
                }
            },
            CancellationToken.None);

        var reloaded = await store.LoadAsync(CancellationToken.None);
        Assert.NotNull(reloaded.FindMapping("BALANCED", "Claude"));
    }

    public void Dispose()
    {
        if (Directory.Exists(directory))
            Directory.Delete(directory, true);
    }
}
