using System.Runtime.InteropServices;

namespace Highbyte.Wrighty.Caching;

public sealed class CachePaths
{
    public CachePaths(string? overrideRoot = null)
    {
        Root = overrideRoot ?? GetDefaultRoot();
    }

    public string Root { get; }

    public string NodeCachePath => Path.Combine(Root, "nodes-v1.json");

    public string IdentityPath => Path.Combine(Root, "identity-v1.json");

    public string WorkItemRuntimePath => Path.Combine(Root, "work-item-runtime-v1.json");

    public string ProviderCapacityPath => Path.Combine(Root, "provider-capacity-v1.json");

    internal string LegacySessionPath => Path.Combine(Root, "sessions-v1.json");

    internal string LegacyProviderAvailabilityPath =>
        Path.Combine(Root, "provider-availability-v1.json");

    public string ProviderCapacityLockPath =>
        Path.Combine(Root, "provider-capacity-v1.lock");

    private static string GetDefaultRoot()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "wrighty",
                "cache");
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Library",
                "Caches",
                "wrighty");
        }

        var xdgCache = Environment.GetEnvironmentVariable("XDG_CACHE_HOME");
        return !string.IsNullOrWhiteSpace(xdgCache)
            ? Path.Combine(xdgCache, "wrighty")
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".cache",
                "wrighty");
    }
}
