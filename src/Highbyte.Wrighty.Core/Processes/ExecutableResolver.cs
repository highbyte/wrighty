using System.Collections.Concurrent;

namespace Highbyte.Wrighty.Processes;

public interface IExecutableResolver
{
    string Resolve(string executableName);
}

public sealed class PathExecutableResolver : IExecutableResolver
{
    private static readonly UnixFileMode ExecutePermissions =
        UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;

    private readonly ConcurrentDictionary<string, string> resolvedPaths;
    private readonly string[] searchDirectories;
    private readonly string[] executableExtensions;

    public PathExecutableResolver()
        : this(
            Environment.GetEnvironmentVariable("PATH"),
            OperatingSystem.IsWindows() ? [".exe"] : [""])
    {
    }

    public PathExecutableResolver(
        string? searchPath,
        IReadOnlyList<string> executableExtensions)
    {
        ArgumentNullException.ThrowIfNull(executableExtensions);

        var comparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        resolvedPaths = new ConcurrentDictionary<string, string>(comparer);
        searchDirectories = (searchPath ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(path => path.Trim('"'))
            .Where(Path.IsPathFullyQualified)
            .Select(Path.GetFullPath)
            .Distinct(comparer)
            .ToArray();
        this.executableExtensions = executableExtensions.Count == 0
            ? [string.Empty]
            : executableExtensions.ToArray();
    }

    public string Resolve(string executableName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executableName);
        if (!string.Equals(Path.GetFileName(executableName), executableName, StringComparison.Ordinal))
        {
            throw new ArgumentException("The executable name must not contain a path.", nameof(executableName));
        }

        return resolvedPaths.GetOrAdd(executableName, ResolveCore);
    }

    private string ResolveCore(string executableName)
    {
        foreach (var directory in searchDirectories)
        {
            foreach (var extension in executableExtensions)
            {
                var candidate = Path.GetFullPath(Path.Combine(directory, executableName + extension));
                if (IsExecutable(candidate))
                {
                    return candidate;
                }
            }
        }

        throw new FileNotFoundException(
            $"Executable '{executableName}' was not found in an absolute PATH directory.",
            executableName);
    }

    private static bool IsExecutable(string path)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        return OperatingSystem.IsWindows() ||
               (File.GetUnixFileMode(path) & ExecutePermissions) != 0;
    }
}
