using System.Runtime.InteropServices;
using System.Text.Json;

namespace Highbyte.Wrighty.Settings;

/// <summary>
/// The OS-appropriate, user-scoped configuration directory (distinct from the regenerable cache
/// dir): durable settings the operator sets deliberately. Overridable via WRIGHTY_CONFIG_DIR for
/// tests and non-standard layouts.
/// </summary>
public sealed class UserConfigPaths
{
    public UserConfigPaths(string? overrideRoot = null)
    {
        Root = string.IsNullOrWhiteSpace(overrideRoot) ? GetDefaultRoot() : overrideRoot;
    }

    public string Root { get; }

    public string SettingsPath => Path.Combine(Root, "settings-v1.json");

    private static string GetDefaultRoot()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "wrighty");
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Library",
                "Application Support",
                "wrighty");
        }

        var xdgConfig = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        return !string.IsNullOrWhiteSpace(xdgConfig)
            ? Path.Combine(xdgConfig, "wrighty")
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".config",
                "wrighty");
    }
}

/// <summary>
/// Durable, user-scoped Wrighty settings. Currently only a <see cref="HostLabel"/>: a symbolic name
/// the operator can choose so the real machine name is not published to a (possibly public) GitHub
/// issue in the handover comment.
/// </summary>
public sealed record UserSettings(string? HostLabel = null)
{
    public int Version { get; init; } = 1;
}

public sealed class UserSettingsStore(UserConfigPaths paths)
{
    private const int SchemaVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };
    private readonly SemaphoreSlim gate = new(1, 1);

    public async Task<UserSettings> LoadAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (!File.Exists(paths.SettingsPath))
            {
                return new UserSettings();
            }

            await using var stream = File.OpenRead(paths.SettingsPath);
            var settings = await JsonSerializer.DeserializeAsync<UserSettings>(
                stream, JsonOptions, cancellationToken);
            return settings is { Version: SchemaVersion } ? settings : new UserSettings();
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            // Settings are best-effort and regenerable; a corrupt file degrades to defaults rather
            // than breaking every command that reads the host label.
            return new UserSettings();
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task SaveAsync(UserSettings settings, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(paths.Root);
            var temporaryPath = $"{paths.SettingsPath}.{Guid.NewGuid():N}.tmp";
            try
            {
                await using (var stream = new FileStream(
                    temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    await JsonSerializer.SerializeAsync(
                        stream, settings with { Version = SchemaVersion }, JsonOptions, cancellationToken);
                    await stream.FlushAsync(cancellationToken);
                }

                File.Move(temporaryPath, paths.SettingsPath, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
        }
        finally
        {
            gate.Release();
        }
    }
}
