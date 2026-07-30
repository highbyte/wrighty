using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Highbyte.Wrighty.Errors;

namespace Highbyte.Wrighty.Web;

internal sealed record WebEndpointOptions(
    IPAddress BindAddress,
    int Port,
    bool IsLoopback,
    IReadOnlyList<string> AllowedHosts,
    Uri? PublicUrl);

internal static class WebEndpointOptionsResolver
{
    public static WebEndpointOptions Resolve(WebServerOptions options)
        => Resolve(options, LocalAddresses(), allocateLoopbackPort: true);

    internal static WebEndpointOptions Resolve(
        WebServerOptions options,
        IReadOnlyCollection<IPAddress> localAddresses)
        => Resolve(options, localAddresses, allocateLoopbackPort: false);

    private static WebEndpointOptions Resolve(
        WebServerOptions options,
        IReadOnlyCollection<IPAddress> localAddresses,
        bool allocateLoopbackPort)
    {
        var address = ResolveAddress(options.BindAddress);
        var loopback = IPAddress.IsLoopback(address);
        if (!loopback && !localAddresses.Contains(address))
        {
            throw new TrackerException(
                "WEB_BIND_ADDRESS_UNAVAILABLE",
                $"--bind address '{address}' is not assigned to a local network interface.",
                2);
        }

        return new WebEndpointOptions(
            address,
            allocateLoopbackPort && loopback && options.Port == 0
                ? AvailableLoopbackPort()
                : options.Port,
            loopback,
            ValidateAllowedHosts(options.AllowedHosts, address),
            ResolvePublicUrl(options.PublicUrl));
    }

    private static IPAddress ResolveAddress(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return IPAddress.Loopback;
        }

        if (!IPAddress.TryParse(value.Trim(), out var address))
        {
            throw new TrackerException(
                "WEB_BIND_INVALID",
                "--bind must be a specific IP address assigned to this machine.",
                2);
        }

        if (address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any))
        {
            throw new TrackerException(
                "WEB_BIND_WILDCARD_FORBIDDEN",
                "Wildcard bind addresses 0.0.0.0 and :: are refused because they expose " +
                "Wrighty on every network interface.",
                2);
        }

        return address;
    }

    private static List<string> ValidateAllowedHosts(
        IReadOnlyList<string>? values,
        IPAddress bindAddress)
    {
        if (values is null or { Count: 0 })
        {
            return [];
        }

        var hosts = new List<string>();
        foreach (var value in values)
        {
            var host = NormalizeAllowedHost(value, bindAddress);
            if (!hosts.Contains(host, StringComparer.OrdinalIgnoreCase))
            {
                hosts.Add(host);
            }
        }

        return hosts;
    }

    private static string NormalizeAllowedHost(string value, IPAddress bindAddress)
    {
        var host = value.Trim();
        var hostNameType = Uri.CheckHostName(host);
        if (host.Length == 0 ||
            host.Contains('*', StringComparison.Ordinal) ||
            hostNameType == UriHostNameType.Unknown)
        {
            throw InvalidAllowedHost(value);
        }

        if (!IPAddress.TryParse(host, out var address))
        {
            return hostNameType == UriHostNameType.Dns
                ? host.ToLowerInvariant()
                : host;
        }

        if (!address.Equals(bindAddress) &&
            !(IPAddress.IsLoopback(address) && IPAddress.IsLoopback(bindAddress)))
        {
            throw new TrackerException(
                "WEB_ALLOWED_HOST_INVALID",
                $"--allow-host '{value}' is an IP address that Wrighty is not binding.",
                2);
        }

        return address.ToString();
    }

    private static TrackerException InvalidAllowedHost(string value) =>
        new(
            "WEB_ALLOWED_HOST_INVALID",
            $"--allow-host '{value}' must be one exact host name without a scheme, port, or wildcard.",
            2);

    private static Uri? ResolvePublicUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
            string.IsNullOrEmpty(uri.Host) ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            uri.AbsolutePath != "/" ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new TrackerException(
                "WEB_PUBLIC_URL_INVALID",
                "--public-url must be an absolute http or https origin without a path, query, " +
                "fragment, or user information.",
                2);
        }

        return new Uri(uri.GetLeftPart(UriPartial.Authority), UriKind.Absolute);
    }

    private static HashSet<IPAddress> LocalAddresses() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .SelectMany(network => network.GetIPProperties().UnicastAddresses)
            .Select(address => address.Address)
            .ToHashSet();

    private static int AvailableLoopbackPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }
}
