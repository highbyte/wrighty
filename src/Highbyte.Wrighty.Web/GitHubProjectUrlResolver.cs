using System.Collections.Concurrent;
using Highbyte.Wrighty.Configuration;
using Highbyte.Wrighty.Errors;
using Highbyte.Wrighty.Initialization;

namespace Highbyte.Wrighty.Web;

public delegate Task<GitHubProjectInfo?> GitHubProjectLookup(
    string host,
    string owner,
    int number,
    CancellationToken cancellationToken);

public sealed class GitHubProjectUrlResolver(GitHubProjectLookup lookup)
{
    private readonly ConcurrentDictionary<string, string> urls =
        new(StringComparer.OrdinalIgnoreCase);

    public static GitHubProjectUrlResolver Unavailable { get; } = new(
        (_, _, _, _) => Task.FromResult<GitHubProjectInfo?>(null));

    public async Task<string?> ResolveAsync(
        TrackerConfig config,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(config.Backend, "github", StringComparison.OrdinalIgnoreCase))
            return null;

        var key = $"{config.GitHubHost}\n{config.EffectiveProjectOwner}\n{config.ProjectNumber}";
        if (urls.TryGetValue(key, out var cached))
            return cached;

        try
        {
            var project = await lookup(
                config.GitHubHost,
                config.EffectiveProjectOwner,
                config.ProjectNumber,
                cancellationToken);
            var url = TrustedProjectUrl(config.GitHubHost, project?.Url);
            if (url is not null)
                urls.TryAdd(key, url);
            return url;
        }
        catch (TrackerException)
        {
            // The operations view already reports backend failures. Keep its navigation usable
            // when this optional canonical-URL lookup cannot complete.
            return null;
        }
    }

    private static string? TrustedProjectUrl(string host, string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var candidate) ||
            !string.Equals(candidate.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !Uri.TryCreate(
                $"{Uri.UriSchemeHttps}{Uri.SchemeDelimiter}{host}",
                UriKind.Absolute,
                out var configuredOrigin) ||
            !string.Equals(candidate.IdnHost, configuredOrigin.IdnHost, StringComparison.OrdinalIgnoreCase) ||
            candidate.Port != configuredOrigin.Port)
            return null;

        return candidate.AbsoluteUri;
    }
}
