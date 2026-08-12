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

    public string SettingsPath => Path.Combine(Root, "settings-v2.json");

    /// <summary>
    /// The version-1 file. Kept as a separate path rather than upgrading v1 in place so that
    /// downgrading Wrighty does not silently lose the host label: an older build keeps reading its
    /// own file. The cost is one stale file after migration, which is cheaper than the alternative,
    /// because a version mismatch degrades to defaults without telling anyone.
    /// </summary>
    public string LegacySettingsPath => Path.Combine(Root, "settings-v1.json");

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
/// Durable, user-scoped Wrighty settings.
///
/// <see cref="HostLabel"/> is a symbolic name the operator can choose so the real machine name is
/// not published to a (possibly public) GitHub issue in the handover comment.
///
/// <see cref="WorkerProfiles"/> maps a shared execution-profile name to this user's concrete
/// vendor selections. It lives here rather than in the repository's <c>.wrighty.json</c> because a
/// model name is a property of what this operator has installed and is entitled to, not of the
/// project: two people working the same repository can resolve <c>deep</c> differently, and
/// neither is wrong.
/// </summary>
public sealed record UserSettings(string? HostLabel = null)
{
    public int Version { get; init; } = UserSettingsStore.SchemaVersion;

    /// <summary>Profile name to agent name to that agent's mapping.</summary>
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, ExecutionProfileMapping>>
        WorkerProfiles
    { get; init; } =
        new Dictionary<string, IReadOnlyDictionary<string, ExecutionProfileMapping>>(
            StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Looks up one mapping case-insensitively. The comparison is done here rather than by relying
    /// on the dictionary's comparer because deserialization rebuilds these dictionaries with the
    /// default ordinal comparer, silently making a hand-edited <c>"Claude"</c> unmatchable.
    /// </summary>
    public ExecutionProfileMapping? FindMapping(string profile, string agent) =>
        Lookup(WorkerProfiles, profile) is { } agents ? Lookup(agents, agent) : null;

    private static TValue? Lookup<TValue>(IReadOnlyDictionary<string, TValue> source, string key)
        where TValue : class
    {
        if (source.TryGetValue(key, out var exact))
        {
            return exact;
        }

        foreach (var (candidate, value) in source)
        {
            if (string.Equals(candidate, key, StringComparison.OrdinalIgnoreCase))
            {
                return value;
            }
        }

        return null;
    }
}

public sealed class UserSettingsStore(UserConfigPaths paths)
{
    public const int SchemaVersion = 2;
    private const int LegacySchemaVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new ExecutionEffortJsonConverter() }
    };
    private readonly SemaphoreSlim gate = new(1, 1);

    public string SourcePath => Path.GetFullPath(paths.SettingsPath);

    public string LegacySourcePath => Path.GetFullPath(paths.LegacySettingsPath);

    /// <summary>
    /// Whether any settings are on disk, current or legacy. Includes the legacy file because a
    /// migrated-but-not-yet-saved install genuinely has settings: reporting "not present" while
    /// simultaneously displaying a host label read from v1 tells the operator two contradictory
    /// things.
    /// </summary>
    public bool Exists => File.Exists(SourcePath) || File.Exists(LegacySourcePath);

    /// <summary>True when the values in effect came from the legacy file and v2 is not written yet.</summary>
    public bool AwaitingMigration => !File.Exists(SourcePath) && File.Exists(LegacySourcePath);

    /// <summary>
    /// Reads the current settings, migrating a version-1 file forward when no version-2 file exists
    /// yet. Migration is read-only: the v1 file is left exactly where it is, so an older Wrighty on
    /// the same machine keeps working. The first <see cref="SaveAsync"/> materializes v2.
    /// </summary>
    public async Task<UserSettings> LoadAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            var current = await ReadAsync(paths.SettingsPath, SchemaVersion, cancellationToken);
            if (current is not null)
            {
                return current;
            }

            if (File.Exists(paths.SettingsPath))
            {
                // A v2 file exists but did not parse as v2 — a future version, or corrupt. Falling
                // back to the v1 file here would resurrect settings the operator has since changed,
                // which is worse than starting from defaults.
                return new UserSettings();
            }

            var legacy = await ReadAsync(
                paths.LegacySettingsPath, LegacySchemaVersion, cancellationToken);
            return legacy is null
                ? new UserSettings()
                // Carry every v1 field forward explicitly. v1 had no profile mappings, so the
                // migrated result is simply the host label plus an empty map.
                : new UserSettings(legacy.HostLabel);
        }
        finally
        {
            gate.Release();
        }
    }

    private static async Task<UserSettings?> ReadAsync(
        string path, int expectedVersion, CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            await using var stream = File.OpenRead(path);
            var settings = await JsonSerializer.DeserializeAsync<UserSettings>(
                stream, JsonOptions, cancellationToken);
            return settings?.Version == expectedVersion ? settings : null;
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            // Settings are best-effort and regenerable; a corrupt file degrades to defaults rather
            // than breaking every command that reads the host label.
            return null;
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
