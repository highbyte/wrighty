using Highbyte.Wrighty.Configuration;
using Highbyte.Wrighty.Web;

namespace Highbyte.Wrighty.UnitTests.Web;

/// <summary>
/// The sentence a configuration save leaves on screen. It has to account for a save that changed
/// nothing but still migrated the file, because that is precisely the save the legacy-properties
/// notice asks for.
/// </summary>
public sealed class ConfigurationSaveNoticeTests
{
    [Fact]
    public void A_save_that_changed_values_leaves_process_specific_guidance_to_the_settings_view()
    {
        var notice = ConfigurationSaveNotice.Describe(Result(
            changes: [new ConfigurationChange("worker.defaultAgent", "claude", "codex")]));

        Assert.Equal("Configuration saved and applied to this web console.", notice);
    }

    [Fact]
    public void A_save_that_changed_nothing_says_that_plainly()
    {
        var notice = ConfigurationSaveNotice.Describe(Result());

        Assert.Equal("Configuration already matched the submitted values.", notice);
    }

    [Fact]
    public void A_dynamic_save_says_that_no_restart_is_needed()
    {
        var notice = ConfigurationSaveNotice.Describe(Result(
            changes: [new ConfigurationChange("testing.notInstalledAgents", null, new[] { "codex" })],
            restartRequired: false));

        Assert.Equal(
            "Configuration saved. The change applies without restarting Wrighty.",
            notice);
    }

    [Fact]
    public void A_save_that_only_migrated_reports_the_migration()
    {
        // The case the operator reaches by following the legacy-properties notice: nothing edited,
        // so nothing changed, but the file was still rewritten to drop what an earlier version
        // wrote. Reporting only "already matched" made a successful migration look like a no-op.
        var notice = ConfigurationSaveNotice.Describe(Result(
            migrated: ["worker.effectiveUsageFailure", "worker.effectiveHandoverComment"]));

        Assert.Equal(
            "Configuration already matched the submitted values. " +
            "Removed 2 values written by an earlier Wrighty version.",
            notice);
    }

    [Fact]
    public void A_single_migrated_value_reads_as_one()
    {
        var notice = ConfigurationSaveNotice.Describe(Result(
            migrated: ["worker.effectiveHandoverComment"]));

        Assert.EndsWith(
            "Removed 1 value written by an earlier Wrighty version.",
            notice,
            StringComparison.Ordinal);
    }

    [Fact]
    public void A_save_that_both_changed_and_migrated_reports_both()
    {
        var notice = ConfigurationSaveNotice.Describe(Result(
            changes: [new ConfigurationChange("worker.defaultAgent", "claude", "codex")],
            migrated: ["worker.effectiveContext"]));

        Assert.StartsWith("Configuration saved", notice, StringComparison.Ordinal);
        Assert.EndsWith(
            "Removed 1 value written by an earlier Wrighty version.",
            notice,
            StringComparison.Ordinal);
    }

    [Fact]
    public void An_empty_migration_list_adds_nothing()
    {
        Assert.Equal(
            ConfigurationSaveNotice.Describe(Result()),
            ConfigurationSaveNotice.Describe(Result(migrated: [])));
    }

    private static RepositoryConfigurationMutationResult Result(
        IReadOnlyList<ConfigurationChange>? changes = null,
        IReadOnlyList<string>? migrated = null,
        bool? restartRequired = null)
    {
        var snapshot = new RepositoryConfigurationSnapshot(
            "/tmp/.wrighty.json",
            new TrackerConfig { Backend = "local-markdown" },
            "revision",
            1,
            false,
            false,
            false,
            [],
            [],
            null);
        return new RepositoryConfigurationMutationResult(
            snapshot,
            snapshot,
            changes ?? [],
            Saved: true,
            RestartRequired: restartRequired ?? changes is { Count: > 0 },
            MigratedLegacyProperties: migrated);
    }
}
