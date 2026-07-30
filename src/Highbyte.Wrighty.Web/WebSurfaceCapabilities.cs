using Highbyte.Wrighty.Configuration;

namespace Highbyte.Wrighty.Web;

public sealed record WebSurfaceCapabilities(
    bool ConfigurationRead,
    bool ConfigurationWrite,
    bool WorkerInstances,
    bool OperationalItems,
    bool ProviderCapacity,
    bool LocalBoard,
    bool LocalItemMutation,
    bool GitHubTarget)
{
    public static WebSurfaceCapabilities Resolve(TrackerConfig config)
    {
        var local = string.Equals(
            config.Backend,
            "local-markdown",
            StringComparison.OrdinalIgnoreCase);
        var github = string.Equals(
            config.Backend,
            "github",
            StringComparison.OrdinalIgnoreCase);
        return new WebSurfaceCapabilities(
            ConfigurationRead: true,
            ConfigurationWrite: true,
            WorkerInstances: true,
            OperationalItems: true,
            ProviderCapacity: true,
            LocalBoard: local,
            LocalItemMutation: local,
            GitHubTarget: github);
    }
}
