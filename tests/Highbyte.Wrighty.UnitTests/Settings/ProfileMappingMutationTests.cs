using Highbyte.Wrighty.Settings;
using Highbyte.Wrighty.Workers;

namespace Highbyte.Wrighty.UnitTests.Settings;

/// <summary>
/// Editing one (profile, agent) pair rather than replacing the whole map.
///
/// The narrow shape is the point: two surfaces write this file, and a whole-map write from a page
/// rendered minutes ago would drop a mapping the CLI added in between. The revision check catches a
/// concurrent write only if the page is reloaded first, so a narrow mutation is correct even where
/// the guard would have held.
/// </summary>
public sealed class ProfileMappingMutationTests : IDisposable
{
    private readonly string root =
        Path.Combine(Path.GetTempPath(), $"wrighty-mapping-{Guid.NewGuid():N}");

    private UserConfigurationService Service() =>
        new(new UserSettingsStore(new UserConfigPaths(root)));

    private static async Task<UserSettings> ApplyAsync(
        UserConfigurationService service, params ProfileMappingMutation[] mutations)
    {
        foreach (var mutation in mutations)
        {
            var snapshot = await service.ReadAsync(CancellationToken.None);
            await service.MutateAsync(snapshot.Revision, mutation, false, CancellationToken.None);
        }

        return (await service.ReadAsync(CancellationToken.None)).Stored;
    }

    [Fact]
    public async Task Editing_one_agent_leaves_its_siblings_alone()
    {
        var service = Service();

        var settings = await ApplyAsync(
            service,
            new ProfileMappingMutation("deep", "codex", "gpt-5.6-sol", ExecutionEffort.Ultra),
            new ProfileMappingMutation("deep", "claude", "opus", ExecutionEffort.XHigh),
            new ProfileMappingMutation("economy", "codex", null, ExecutionEffort.Low));

        Assert.Equal("gpt-5.6-sol", settings.FindMapping("deep", "codex")!.Model);
        Assert.Equal("opus", settings.FindMapping("deep", "claude")!.Model);
        Assert.Equal(ExecutionEffort.Low, settings.FindMapping("economy", "codex")!.Effort);
    }

    [Fact]
    public async Task Clearing_both_values_removes_the_mapping_rather_than_storing_an_empty_one()
    {
        // An entry saying nothing still lists as configured, and resolution would fail on it.
        var service = Service();

        var settings = await ApplyAsync(
            service,
            new ProfileMappingMutation("deep", "codex", "gpt-5.6-sol", ExecutionEffort.Ultra),
            new ProfileMappingMutation("deep", "codex", null, null));

        Assert.Null(settings.FindMapping("deep", "codex"));
        Assert.Empty(settings.WorkerProfiles);
    }

    [Fact]
    public async Task Removing_the_last_agent_drops_the_profile_but_keeps_the_others()
    {
        var service = Service();

        var settings = await ApplyAsync(
            service,
            new ProfileMappingMutation("deep", "codex", "gpt-5.6-sol", null),
            new ProfileMappingMutation("economy", "codex", "gpt-5-mini", null),
            new ProfileMappingMutation("deep", "codex", null, null));

        Assert.Null(settings.FindMapping("deep", "codex"));
        Assert.NotNull(settings.FindMapping("economy", "codex"));
        Assert.Single(settings.WorkerProfiles);
    }

    [Fact]
    public async Task A_profile_named_in_a_different_case_edits_the_same_entry()
    {
        // Settings round-trip through JSON, which drops a dictionary's comparer. This has been lost
        // twice elsewhere; here it would silently create a second entry for the same profile.
        var service = Service();

        var settings = await ApplyAsync(
            service,
            new ProfileMappingMutation("deep", "codex", "gpt-5.6-sol", null),
            new ProfileMappingMutation("DEEP", "CODEX", "gpt-5.4", null));

        Assert.Single(settings.WorkerProfiles);
        Assert.Equal("gpt-5.4", settings.FindMapping("deep", "codex")!.Model);
    }

    [Fact]
    public async Task A_change_is_reported_per_pair_rather_than_as_two_maps()
    {
        // The notice an operator reads should name what moved, not print the whole configuration.
        var service = Service();
        var first = await service.ReadAsync(CancellationToken.None);
        await service.MutateAsync(
            first.Revision,
            new ProfileMappingMutation("deep", "codex", "gpt-5.6-sol", ExecutionEffort.Ultra),
            false,
            CancellationToken.None);

        var second = await service.ReadAsync(CancellationToken.None);
        var result = await service.MutateAsync(
            second.Revision,
            new ProfileMappingMutation("deep", "codex", "gpt-5.4", ExecutionEffort.High),
            false,
            CancellationToken.None);

        var change = Assert.Single(result.Changes);
        Assert.Equal("workerProfiles.deep.codex", change.Id);
        Assert.Equal("gpt-5.6-sol / ultra", change.Before);
        Assert.Equal("gpt-5.4 / high", change.After);
    }

    [Fact]
    public async Task A_mapping_with_only_an_effort_reports_the_vendor_default_model()
    {
        var service = Service();
        var before = await service.ReadAsync(CancellationToken.None);

        var result = await service.MutateAsync(
            before.Revision,
            new ProfileMappingMutation("balanced", "claude", null, ExecutionEffort.Medium),
            false,
            CancellationToken.None);

        Assert.Equal("vendor default / medium", Assert.Single(result.Changes).After);
    }

    [Fact]
    public async Task A_blank_model_is_stored_as_no_model_rather_than_an_empty_string()
    {
        // An empty string would reach a vendor command line as an empty argument, which is not the
        // same as passing no model at all.
        var service = Service();

        var settings = await ApplyAsync(
            service, new ProfileMappingMutation("deep", "codex", "   ", ExecutionEffort.High));

        Assert.Null(settings.FindMapping("deep", "codex")!.Model);
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, true);
        }
    }
}
