using Highbyte.Wrighty.Configuration;
using Highbyte.Wrighty.Errors;
using Highbyte.Wrighty.Settings;

namespace Highbyte.Wrighty.UnitTests.Settings;

/// <summary>
/// The console has never been able to edit a machine-local setting, so this is the scope's first
/// write path. Its whole reason for existing beyond <see cref="UserSettingsStore"/> is the revision
/// check: the settings file is hand-editable and shared by every Wrighty on the machine, and a web
/// process can hold a view of it for hours. Without the check, saving a form would overwrite
/// whatever the CLI did in the meantime, silently.
/// </summary>
public sealed class UserConfigurationServiceTests : IDisposable
{
    private readonly string root =
        Path.Combine(Path.GetTempPath(), $"wrighty-userconfig-{Guid.NewGuid():N}");

    private (UserConfigurationService Service, UserSettingsStore Store) Create()
    {
        var store = new UserSettingsStore(new UserConfigPaths(root));
        return (new UserConfigurationService(store), store);
    }

    [Fact]
    public async Task A_machine_with_no_settings_file_still_reports_its_scope()
    {
        // The console must be able to show and edit this scope before anything has been configured,
        // otherwise the first write has nowhere to happen from.
        var (service, _) = Create();

        var snapshot = await service.ReadAsync(CancellationToken.None);

        Assert.False(snapshot.Exists);
        Assert.Equal(UserConfigurationSnapshot.AbsentRevision, snapshot.Revision);
        Assert.NotEmpty(snapshot.Settings);
        Assert.All(snapshot.Settings, setting =>
            Assert.Equal(ConfigurationScope.User, setting.Scope));
    }

    [Fact]
    public async Task The_host_label_reports_stored_and_effective_separately()
    {
        // The same split repository scope uses: what is written down, and what actually applies.
        // Collapsing them would hide that an unset label still has a visible consequence.
        var (service, _) = Create();

        var before = await service.ReadAsync(CancellationToken.None);
        var unset = before.Settings.Single(setting => setting.Id == "hostLabel");
        Assert.Null(unset.StoredValue);
        Assert.Equal(HostLabelProvider.AnonymousLabel, unset.EffectiveValue);
        Assert.Equal("default", unset.DefaultSource);

        await service.MutateAsync(
            before.Revision, new HostLabelMutation("workstation-alpha"), false, CancellationToken.None);

        var after = await service.ReadAsync(CancellationToken.None);
        var set = after.Settings.Single(setting => setting.Id == "hostLabel");
        Assert.Equal("workstation-alpha", set.StoredValue);
        Assert.Equal("workstation-alpha", set.EffectiveValue);
        Assert.Equal("user", set.DefaultSource);
    }

    [Fact]
    public async Task A_first_write_succeeds_against_the_absent_revision()
    {
        var (service, store) = Create();
        var before = await service.ReadAsync(CancellationToken.None);

        var result = await service.MutateAsync(
            before.Revision, new HostLabelMutation("alpha"), false, CancellationToken.None);

        Assert.True(result.Saved);
        var change = Assert.Single(result.Changes);
        Assert.Equal("hostLabel", change.Id);
        Assert.Equal("alpha", (await store.LoadAsync(CancellationToken.None)).HostLabel);
        // The revision moves, so a second write holding the old one is refused.
        Assert.NotEqual(before.Revision, result.After.Revision);
    }

    [Fact]
    public async Task A_write_against_a_stale_revision_is_refused_and_changes_nothing()
    {
        // The scenario this exists for: a console page rendered, the CLI wrote, then the page was
        // submitted. The second write must not win by arriving last.
        var (service, store) = Create();
        var page = await service.ReadAsync(CancellationToken.None);

        await store.SaveAsync(new UserSettings("set-from-the-cli"), CancellationToken.None);

        var refusal = await Assert.ThrowsAsync<TrackerException>(() => service.MutateAsync(
            page.Revision, new HostLabelMutation("set-from-the-page"), false, CancellationToken.None));

        Assert.Equal(UserConfigurationService.RevisionConflict, refusal.Code);
        Assert.Equal("set-from-the-cli", (await store.LoadAsync(CancellationToken.None)).HostLabel);
    }

    [Fact]
    public async Task A_refusal_names_both_revisions_so_a_surface_can_explain_itself()
    {
        var (service, store) = Create();
        var page = await service.ReadAsync(CancellationToken.None);
        await store.SaveAsync(new UserSettings("elsewhere"), CancellationToken.None);

        var refusal = await Assert.ThrowsAsync<TrackerException>(() => service.MutateAsync(
            page.Revision, new HostLabelMutation("mine"), false, CancellationToken.None));

        Assert.NotNull(refusal.Details);
        Assert.Equal(page.Revision, refusal.Details!["expectedRevision"]);
        Assert.NotEqual(page.Revision, refusal.Details["actualRevision"]);
    }

    [Fact]
    public async Task A_dry_run_reports_the_change_without_making_it()
    {
        var (service, store) = Create();
        var before = await service.ReadAsync(CancellationToken.None);

        var result = await service.MutateAsync(
            before.Revision, new HostLabelMutation("alpha"), dryRun: true, CancellationToken.None);

        Assert.False(result.Saved);
        Assert.Single(result.Changes);
        Assert.Null((await store.LoadAsync(CancellationToken.None)).HostLabel);
    }

    [Fact]
    public async Task Resubmitting_an_unchanged_value_is_a_no_op_rather_than_an_error()
    {
        // A form submitted twice is a person clicking twice, not a mistake worth failing over.
        var (service, _) = Create();
        var first = await service.ReadAsync(CancellationToken.None);
        await service.MutateAsync(
            first.Revision, new HostLabelMutation("alpha"), false, CancellationToken.None);

        var second = await service.ReadAsync(CancellationToken.None);
        var result = await service.MutateAsync(
            second.Revision, new HostLabelMutation("alpha"), false, CancellationToken.None);

        Assert.False(result.Saved);
        Assert.Empty(result.Changes);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public async Task Clearing_the_label_restores_the_anonymous_default(string? cleared)
    {
        var (service, store) = Create();
        var first = await service.ReadAsync(CancellationToken.None);
        await service.MutateAsync(
            first.Revision, new HostLabelMutation("alpha"), false, CancellationToken.None);

        var second = await service.ReadAsync(CancellationToken.None);
        var result = await service.MutateAsync(
            second.Revision, new HostLabelMutation(cleared), false, CancellationToken.None);

        Assert.True(result.Saved);
        Assert.Null((await store.LoadAsync(CancellationToken.None)).HostLabel);
        Assert.Equal(
            HostLabelProvider.AnonymousLabel,
            result.After.Settings.Single(setting => setting.Id == "hostLabel").EffectiveValue);
    }

    [Fact]
    public async Task A_label_is_trimmed_before_it_is_stored()
    {
        var (service, store) = Create();
        var before = await service.ReadAsync(CancellationToken.None);

        await service.MutateAsync(
            before.Revision, new HostLabelMutation("  alpha  "), false, CancellationToken.None);

        Assert.Equal("alpha", (await store.LoadAsync(CancellationToken.None)).HostLabel);
    }

    [Fact]
    public async Task Profile_mappings_are_visible_here_but_not_editable_here()
    {
        // They belong to this scope and the console should say so, but editing them needs the model
        // discovery that names the choices. Read-only is the honest interim state: an operator can
        // see what their machine holds and is pointed at the command that changes it.
        var (service, store) = Create();
        await store.SaveAsync(
            new UserSettings
            {
                WorkerProfiles = new Dictionary<string, IReadOnlyDictionary<string, ExecutionProfileMapping>>
                {
                    ["deep"] = new Dictionary<string, ExecutionProfileMapping>
                    {
                        ["codex"] = new() { Model = "gpt-5.6-sol" }
                    }
                }
            },
            CancellationToken.None);

        var snapshot = await service.ReadAsync(CancellationToken.None);

        var mappings = snapshot.Settings.Single(setting => setting.Id == "workerProfiles");
        Assert.Equal(ConfigurationEditMode.ReadOnly, mappings.EditMode);
        Assert.Equal("user", mappings.DefaultSource);
        Assert.Contains("wrighty config profile set", mappings.Help);
        // The description has to say *why* it is machine-local, because that is the whole reason
        // there are two scopes rather than one file.
        Assert.Contains("entitlement", mappings.Help);
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, true);
        }
    }
}
