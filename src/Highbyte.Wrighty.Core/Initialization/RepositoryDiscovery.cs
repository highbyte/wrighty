using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Highbyte.Wrighty.Initialization;

public sealed record DiscoveredGitHubRepository(string Host, string Repository);

public interface IRepositoryDiscovery
{
    Task<DiscoveredGitHubRepository?> DiscoverAsync(
        string directory,
        string remoteName,
        CancellationToken cancellationToken);
}

public interface IGitProcess
{
    Task<GitProcessResult> RunAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken);
}

public sealed record GitProcessResult(int ExitCode, string StandardOutput, string StandardError);

public sealed class GitProcess : IGitProcess
{
    public async Task<GitProcessResult> RunAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        process.Start();
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        return new GitProcessResult(
            process.ExitCode,
            await outputTask,
            await errorTask);
    }
}

public sealed partial class GitRepositoryDiscovery(IGitProcess process) : IRepositoryDiscovery
{
    public async Task<DiscoveredGitHubRepository?> DiscoverAsync(
        string directory,
        string remoteName,
        CancellationToken cancellationToken)
    {
        var result = await process.RunAsync(
            ["-C", directory, "config", "--get", $"remote.{remoteName}.url"],
            cancellationToken);
        if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.StandardOutput))
        {
            return null;
        }

        return Parse(result.StandardOutput.Trim());
    }

    public static DiscoveredGitHubRepository? Parse(string remoteUrl)
    {
        string host;
        string path;
        var scp = ScpRemote().Match(remoteUrl);
        if (scp.Success && !remoteUrl.Contains("://", StringComparison.Ordinal))
        {
            host = scp.Groups["host"].Value;
            path = scp.Groups["path"].Value;
        }
        else if (Uri.TryCreate(remoteUrl, UriKind.Absolute, out var uri) &&
                 (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeSsh))
        {
            host = uri.Host;
            path = uri.AbsolutePath.Trim('/');
        }
        else
        {
            return null;
        }

        if (path.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            path = path[..^4];
        }

        var parts = path.Split('/');
        if (parts.Length != 2 || parts.Any(part => !RepositorySegment().IsMatch(part)))
        {
            return null;
        }

        return new DiscoveredGitHubRepository(host, $"{parts[0]}/{parts[1]}");
    }

    [GeneratedRegex(@"^(?:[^@/:]+@)?(?<host>[^/:]+):(?<path>.+)$", RegexOptions.CultureInvariant)]
    private static partial Regex ScpRemote();

    [GeneratedRegex(@"^[A-Za-z0-9_.-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex RepositorySegment();
}
