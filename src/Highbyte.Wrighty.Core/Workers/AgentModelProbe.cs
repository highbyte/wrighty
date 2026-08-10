using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Highbyte.Wrighty.Processes;

namespace Highbyte.Wrighty.Workers;

/// <summary>
/// The bounded exchange an adapter needs, behind an interface so a vendor's response shape can be
/// tested without spawning that vendor. Parsing is where the per-vendor bugs live; requiring a real
/// CLI to reach it would leave it covered only by tests that cannot run in CI.
/// </summary>
public interface IAgentModelProbe
{
    Task<(JsonElement? Answer, ModelDiscoveryFailure Failure)> ExchangeAsync(
        string executable,
        IReadOnlyList<string> arguments,
        IReadOnlyList<string> requests,
        Func<JsonElement, bool> isAnswer,
        CancellationToken cancellationToken,
        TimeSpan? timeout = null);
}

/// <summary>
/// Runs one bounded request/response exchange against an agent CLI over stdin/stdout newline JSON.
///
/// This is the only piece the three vendor protocols share — spawn, write some lines, read lines
/// until one matches, and never outlive the budget. What is *said* over that channel differs
/// completely per vendor, which is why the adapters own their own messages and this owns none.
///
/// Every failure is returned, not thrown. A discovery probe is an enrichment; an operator who
/// cannot reach a vendor must still be able to configure a mapping by hand.
/// </summary>
public sealed class AgentModelProbe(IExecutableResolver executables) : IAgentModelProbe
{
    /// <summary>
    /// Long enough for a cold CLI start on a loaded machine, short enough that a config command
    /// does not appear to hang. A probe that exceeds it is killed with its whole process tree:
    /// these vendors start helper children, and orphaning them would leak a process per attempt.
    /// </summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Refuses to buffer an unbounded response. The output is a child process's, so its size is not
    /// Wrighty's to trust; a vendor that streams indefinitely must cost a bounded amount of memory.
    /// </summary>
    private const int MaxResponseBytes = 4 * 1024 * 1024;

    /// <param name="executable">Vendor CLI name, resolved on PATH.</param>
    /// <param name="arguments">Arguments placing the CLI in its protocol mode.</param>
    /// <param name="requests">Lines to write, in order. Each is written as one line.</param>
    /// <param name="isAnswer">
    /// Recognizes the reply this probe is waiting for. Every vendor interleaves notifications and
    /// progress events with replies, so reading "the next line" would take whichever arrived first.
    /// </param>
    /// <param name="timeout">Overrides <see cref="DefaultTimeout"/>.</param>
    public async Task<(JsonElement? Answer, ModelDiscoveryFailure Failure)> ExchangeAsync(
        string executable,
        IReadOnlyList<string> arguments,
        IReadOnlyList<string> requests,
        Func<JsonElement, bool> isAnswer,
        CancellationToken cancellationToken,
        TimeSpan? timeout = null)
    {
        if (!executables.TryResolve(executable, out var path) || path is null)
        {
            return (null, ModelDiscoveryFailure.NotInstalled);
        }

        var start = new ProcessStartInfo
        {
            FileName = path,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = start, EnableRaisingEvents = true };
        try
        {
            if (!process.Start())
            {
                return (null, ModelDiscoveryFailure.Unavailable);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return (null, ModelDiscoveryFailure.Unavailable);
        }

        using var budget = new CancellationTokenSource(timeout ?? DefaultTimeout);
        using var combined =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, budget.Token);
        try
        {
            foreach (var request in requests)
            {
                await process.StandardInput.WriteLineAsync(
                    request.AsMemory(), combined.Token);
                await process.StandardInput.FlushAsync(combined.Token);
            }

            var answer = await ReadAnswerAsync(process, isAnswer, combined.Token);
            return answer is null
                ? (null, ModelDiscoveryFailure.Unrecognized)
                : (answer, ModelDiscoveryFailure.None);
        }
        catch (OperationCanceledException) when (budget.IsCancellationRequested)
        {
            return (null, ModelDiscoveryFailure.TimedOut);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // An IO failure here means the child died or closed the pipe mid-exchange. Which of
            // those it was does not change what the caller can do about it.
            return (null, ModelDiscoveryFailure.Unavailable);
        }
        finally
        {
            Kill(process);
        }
    }

    private static async Task<JsonElement?> ReadAnswerAsync(
        Process process, Func<JsonElement, bool> isAnswer, CancellationToken cancellationToken)
    {
        var read = 0;
        while (await process.StandardOutput.ReadLineAsync(cancellationToken) is { } line)
        {
            read += Encoding.UTF8.GetByteCount(line);
            if (read > MaxResponseBytes)
            {
                return null;
            }

            if (line.Length == 0)
            {
                continue;
            }

            JsonElement element;
            try
            {
                // Cloned because the document is disposed on the way out of this scope, and a
                // JsonElement does not outlive the document that owns its buffer.
                using var document = JsonDocument.Parse(line);
                element = document.RootElement.Clone();
            }
            catch (JsonException)
            {
                // Vendors write human-readable banners onto this channel. A line that is not JSON
                // is noise, not a protocol violation.
                continue;
            }

            if (isAnswer(element))
            {
                return element;
            }
        }

        return null;
    }

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException)
        {
            // Already gone, or the platform will not enumerate the tree. Either way there is
            // nothing left to do, and a probe must not fail over its own cleanup.
        }
    }
}
