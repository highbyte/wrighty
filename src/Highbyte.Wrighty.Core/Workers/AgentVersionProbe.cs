using System.Collections.Concurrent;
using System.Diagnostics;
using Highbyte.Wrighty.Processes;

namespace Highbyte.Wrighty.Workers;

public interface IAgentVersionProbe
{
    /// <summary>The vendor CLI's reported version, or null when it cannot be determined.</summary>
    Task<string?> TryGetVersionAsync(string agent, CancellationToken cancellationToken);
}

/// <summary>
/// Reads <c>&lt;agent&gt; --version</c> once per process and caches the answer.
///
/// Deliberately not part of <see cref="IAgentRuntimeCatalog"/>. That snapshot is synchronous and
/// read from more than twenty places, several on pre-claim paths that run for every candidate item;
/// making it spawn a process per call would turn a cheap filter into a per-item subprocess. Version
/// is only wanted when recording what a launch was given, which happens once per fresh run.
///
/// A failure is not an error. The version is a forensic note that lets a later reader distinguish
/// "this mapping was always wrong" from "the vendor changed underneath it", and a launch must never
/// fail because that note could not be taken.
/// </summary>
public sealed class AgentVersionProbe(
    IExecutableResolver executables,
    TimeSpan? timeout = null,
    AgentRegistry? registry = null) : IAgentVersionProbe
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);
    private readonly ConcurrentDictionary<string, string?> cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly IReadOnlyDictionary<string, string> executableByAgent =
        (registry?.Descriptors ?? BuiltInAgentRegistry.Descriptors).ToDictionary(
            descriptor => descriptor.Id,
            descriptor => descriptor.ExecutableName,
            StringComparer.OrdinalIgnoreCase);

    public async Task<string?> TryGetVersionAsync(string agent, CancellationToken cancellationToken)
    {
        if (cache.TryGetValue(agent, out var cached))
        {
            return cached;
        }

        var version = await ReadAsync(agent, cancellationToken);
        // Cached even when null: a vendor that is missing or refuses --version will keep doing so
        // for this process, and retrying per launch would pay the timeout every time.
        cache[agent] = version;
        return version;
    }

    private async Task<string?> ReadAsync(string agent, CancellationToken cancellationToken)
    {
        if (!executableByAgent.TryGetValue(agent, out var executable) ||
            !executables.TryResolve(executable, out var path) ||
            path is null)
        {
            return null;
        }

        try
        {
            using var process = Process.Start(new ProcessStartInfo(path, "--version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            });
            if (process is null)
            {
                return null;
            }

            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            limit.CancelAfter(timeout ?? DefaultTimeout);
            var output = await process.StandardOutput.ReadToEndAsync(limit.Token);
            await process.WaitForExitAsync(limit.Token);
            return process.ExitCode == 0 ? Normalize(output) : null;
        }
        catch (Exception exception) when (
            exception is OperationCanceledException or InvalidOperationException
                or System.ComponentModel.Win32Exception or IOException)
        {
            return null;
        }
    }

    /// <summary>
    /// The first non-empty line, bounded. Vendors differ — codex prints <c>codex-cli 0.145.0</c>,
    /// claude prints <c>2.1.222 (Claude Code)</c>, and copilot appends an update notice on a second
    /// line — so this keeps what they lead with rather than trying to parse a semantic version out
    /// of three different shapes.
    /// </summary>
    private static string? Normalize(string output)
    {
        var line = output
            .ReplaceLineEndings("\n")
            .Split('\n')
            .Select(value => value.Trim())
            .FirstOrDefault(value => value.Length > 0);
        return string.IsNullOrEmpty(line) ? null : line[..Math.Min(line.Length, 120)];
    }
}
