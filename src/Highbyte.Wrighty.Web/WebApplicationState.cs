using Highbyte.Wrighty.Configuration;
using Highbyte.Wrighty.AgentContext;
using Highbyte.Wrighty.Claims;
using Microsoft.AspNetCore.Http;
using System.Net;
using System.Collections.Frozen;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace Highbyte.Wrighty.Web;

public sealed class WebApplicationState(
    TrackerConfig config,
    string? token,
    string workingDirectory,
    bool tokenAuthenticationRequired = true,
    string? activeConfigurationRevision = null,
    string? localHostName = null)
{
    private readonly ConcurrentDictionary<string, ClaimHandle> handles = new(StringComparer.Ordinal);
    private readonly AsyncLocal<ActiveRepositoryConfiguration?> requestConfiguration = new();
    private ActiveRepositoryConfiguration activeConfiguration =
        new(config, activeConfigurationRevision, CompatibleRevisions(activeConfigurationRevision));

    /// <summary>
    /// The repository configuration captured for this request. Outside a web request this is the
    /// latest compatible configuration applied to the process.
    /// </summary>
    public TrackerConfig Config =>
        requestConfiguration.Value?.Config ?? ActiveConfiguration.Config;

    /// <summary>The latest compatible repository configuration applied to this web process.</summary>
    internal ActiveRepositoryConfiguration ActiveConfiguration =>
        Volatile.Read(ref activeConfiguration);
    public WebSurfaceCapabilities Capabilities { get; } =
        WebSurfaceCapabilities.Resolve(config);
    public string BackendLabel { get; } =
        string.Equals(config.Backend, "github", StringComparison.OrdinalIgnoreCase)
            ? "GITHUB PROJECT"
            : "LOCAL MARKDOWN";
    public string? ActiveConfigurationRevision => ActiveConfiguration.Revision;
    public string? ConfigurationRevision =>
        requestConfiguration.Value?.Revision ?? ActiveConfiguration.Revision;
    public string? Token { get; } = token;
    public bool TokenAuthenticationRequired { get; } = tokenAuthenticationRequired;
    public string WorkspacePath { get; } = ResolveWorkspacePath(config, workingDirectory);
    public string WorkspaceDisplayPath { get; } =
        DisplayWorkspacePath(ResolveWorkspacePath(config, workingDirectory));
    public string LocalHostName { get; } = SafeHostName(localHostName);
    public string ClaimantId { get; } = $"web:{Guid.NewGuid():N}";
    public BoardBatchStore BoardBatches { get; } = new();
    public AgentExecutionContext ClaimantContext => new(null, null, AgentContextSource.ExplicitOption,
        ClaimantKind: ClaimantKind.Human, ClaimantId: ClaimantId);

    /// <summary>
    /// Applies a newly read revision when it preserves the web host's structural backend. The
    /// immutable pair is exchanged atomically, so new requests and workers cannot combine a new
    /// configuration with an old revision.
    /// </summary>
    internal bool TryApplyConfiguration(
        TrackerConfig updated,
        string revision,
        bool restartRunningWorkers = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(revision);
        var current = ActiveConfiguration;
        if (!string.Equals(
                current.Config.Backend,
                updated.Backend,
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (string.Equals(current.Revision, revision, StringComparison.Ordinal))
            return true;

        var compatibleWorkerRevisions = restartRunningWorkers
            ? CompatibleRevisions(revision)
            : current.WorkerCompatibleRevisions
                .Append(revision)
                .ToFrozenSet(StringComparer.Ordinal);

        Interlocked.Exchange(
            ref activeConfiguration,
            new ActiveRepositoryConfiguration(
                updated,
                revision,
                compatibleWorkerRevisions));
        return true;
    }

    /// <summary>
    /// Pins one immutable configuration/revision pair for an entire request. A concurrent save
    /// becomes visible to the next request rather than halfway through the current operation.
    /// </summary>
    internal IDisposable CaptureConfigurationForRequest()
    {
        var previous = requestConfiguration.Value;
        requestConfiguration.Value = ActiveConfiguration;
        return new ConfigurationScope(this, previous);
    }
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

    internal void ConfigureEndpoint(
        IPAddress bindAddress,
        int port,
        IReadOnlyList<string> additionalHosts,
        Uri? publicUrl = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(port);
        if (Port != 0)
        {
            throw new InvalidOperationException("The web endpoint has already been configured.");
        }

        var hosts = IPAddress.IsLoopback(bindAddress)
            ? new[] { "127.0.0.1", "localhost", "::1" }
            : new[] { bindAddress.ToString() };
        hosts = hosts.Concat(additionalHosts).ToArray();

        // This startup-computed set prevents DNS rebinding: every entry is either a
        // closed loopback spelling, the address being bound, or a name the operator
        // explicitly allowed for this invocation.
        var authorities = hosts
            .Select(host => Authority(host, port))
            .ToList();
        var origins = hosts
            .Select(host => DirectOrigin(Authority(host, port)))
            .ToList();
        if (publicUrl is not null)
        {
            authorities.Add(Authority(
                publicUrl.Host,
                publicUrl.IsDefaultPort ? null : publicUrl.Port));
            origins.Add(publicUrl.GetLeftPart(UriPartial.Authority));
        }

        AllowedAuthorities = authorities.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
        AllowedOrigins = origins.ToFrozenSet(StringComparer.Ordinal);
        Port = port;
    }

    internal bool AllowsAuthority(HostString authority)
    {
        if (!authority.HasValue)
        {
            return false;
        }

        return AllowedAuthorities.Contains(Authority(authority.Host, authority.Port));
    }

    private static string Authority(string host, int? port) =>
        port is null
            ? new HostString(host).ToUriComponent()
            : new HostString(host, port.Value).ToUriComponent();

    // The embedded listener intentionally serves direct HTTP origins. A TLS-terminating
    // proxy contributes its separate HTTPS origin through --public-url.
    private static string DirectOrigin(string authority) =>
        string.Concat(Uri.UriSchemeHttp, Uri.SchemeDelimiter, authority);

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

    private static FrozenSet<string> CompatibleRevisions(string? revision) =>
        string.IsNullOrWhiteSpace(revision)
            ? Array.Empty<string>().ToFrozenSet(StringComparer.Ordinal)
            : new[] { revision }.ToFrozenSet(StringComparer.Ordinal);

    private static string SafeHostName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "Unknown host";
        var safe = new string(value.Trim().Where(character => !char.IsControl(character)).ToArray());
        if (string.IsNullOrWhiteSpace(safe))
            return "Unknown host";
        const int maximumLength = 100;
        return safe.Length <= maximumLength ? safe : safe[..maximumLength];
    }

    private sealed class ConfigurationScope(
        WebApplicationState owner,
        ActiveRepositoryConfiguration? previous) : IDisposable
    {
        private WebApplicationState? owner = owner;

        public void Dispose()
        {
            var captured = Interlocked.Exchange(ref owner, null);
            if (captured is not null)
                captured.requestConfiguration.Value = previous;
        }
    }
}

internal sealed record ActiveRepositoryConfiguration(
    TrackerConfig Config,
    string? Revision,
    FrozenSet<string> WorkerCompatibleRevisions);

public interface ILocalHostNameProvider
{
    string? GetHostName();
}

public sealed class SystemLocalHostNameProvider : ILocalHostNameProvider
{
    public static SystemLocalHostNameProvider Instance { get; } = new();

    public string? GetHostName()
    {
        try
        {
            return Environment.MachineName;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }
}
