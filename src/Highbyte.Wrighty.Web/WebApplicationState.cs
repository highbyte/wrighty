using Highbyte.Wrighty.Configuration;
using Highbyte.Wrighty.AgentContext;
using Highbyte.Wrighty.Claims;
using Microsoft.AspNetCore.Http;
using System.Collections.Frozen;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace Highbyte.Wrighty.Web;

public sealed class WebApplicationState(
    TrackerConfig config,
    string token,
    string workingDirectory)
{
    private readonly ConcurrentDictionary<string, ClaimHandle> handles = new(StringComparer.Ordinal);
    public TrackerConfig Config { get; } = config;
    public string Token { get; } = token;
    public string WorkspacePath { get; } = ResolveWorkspacePath(config, workingDirectory);
    public string WorkspaceDisplayPath { get; } =
        DisplayWorkspacePath(ResolveWorkspacePath(config, workingDirectory));
    public string ClaimantId { get; } = $"web:{Guid.NewGuid():N}";
    public AgentExecutionContext ClaimantContext => new(null, null, AgentContextSource.ExplicitOption,
        ClaimantKind: ClaimantKind.Human, ClaimantId: ClaimantId);
    public void Retain(string itemId, ClaimResult result) =>
        handles[itemId] = new ClaimHandle(ClaimantContext, result.ClaimToken);
    public void Retain(string itemId, ClaimResult result, AgentExecutionContext claimantContext) =>
        handles[itemId] = new ClaimHandle(claimantContext, result.ClaimToken);
    public bool TryHandle(string itemId, out ClaimHandle handle) => handles.TryGetValue(itemId, out handle!);
    public void Forget(string itemId) => handles.TryRemove(itemId, out _);
    public string? Generation(string itemId) => TryHandle(itemId, out var handle) && handle.ClaimToken is { } tokenValue
        ? Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(tokenValue))) : null;
    public int Port { get; private set; }
    public FrozenSet<string> AllowedAuthorities { get; private set; } =
        Array.Empty<string>().ToFrozenSet(StringComparer.OrdinalIgnoreCase);
    public FrozenSet<string> AllowedOrigins { get; private set; } =
        Array.Empty<string>().ToFrozenSet(StringComparer.Ordinal);

    internal void ConfigureLoopbackEndpoint(int port)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(port);
        if (Port != 0)
        {
            throw new InvalidOperationException("The web endpoint has already been configured.");
        }

        Port = port;

        // This fixed set prevents DNS rebinding while accepting every spelling of the
        // loopback endpoint. Do not infer or add arbitrary host names here.
        AllowedAuthorities = new[]
        {
            Authority("127.0.0.1", port),
            Authority("localhost", port),
            Authority("::1", port)
        }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
        AllowedOrigins = new[]
        {
            $"http://{Authority("127.0.0.1", port)}",
            $"http://{Authority("localhost", port)}",
            $"http://{Authority("::1", port)}"
        }.ToFrozenSet(StringComparer.Ordinal);
    }

    internal bool AllowsAuthority(HostString authority)
    {
        if (authority.Port is null)
        {
            return false;
        }

        return AllowedAuthorities.Contains(Authority(authority.Host, authority.Port.Value));
    }

    private static string Authority(string host, int port) =>
        new HostString(host, port).ToUriComponent();

    private static string ResolveWorkspacePath(
        TrackerConfig config,
        string workingDirectory)
    {
        var fallback = Path.GetFullPath(workingDirectory);
        if (string.IsNullOrWhiteSpace(config.SourcePath))
        {
            return fallback;
        }

        return Path.GetDirectoryName(Path.GetFullPath(config.SourcePath)) ?? fallback;
    }

    private static string DisplayWorkspacePath(string workspacePath)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(home))
        {
            return workspacePath;
        }

        var normalizedHome = Path.GetFullPath(home)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (normalizedHome.Length == 0)
        {
            return workspacePath;
        }

        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (string.Equals(workspacePath, normalizedHome, comparison))
        {
            return "~";
        }

        var homePrefix = normalizedHome + Path.DirectorySeparatorChar;
        return workspacePath.StartsWith(homePrefix, comparison)
            ? $"~{Path.DirectorySeparatorChar}{workspacePath[homePrefix.Length..]}"
            : workspacePath;
    }
}
