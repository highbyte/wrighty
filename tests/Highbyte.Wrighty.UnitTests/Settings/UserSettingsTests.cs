using Highbyte.Wrighty.Settings;

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

    public void Dispose()
    {
        if (Directory.Exists(directory))
            Directory.Delete(directory, true);
    }
}
