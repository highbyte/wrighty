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
        IReadOnlyList<ProbeTurn> turns,
        CancellationToken cancellationToken,
        TimeSpan? timeout = null);
}

/// <summary>
/// One step of a protocol handshake: lines to write, and optionally the reply to wait for before
/// the next step is written.
///
/// Sequencing is not optional politeness. Copilot's ACP server answers <c>initialize</c> but
/// silently ignores a <c>session/new</c> that arrived before that answer was read — measured, not
/// assumed. Codex tolerates a pipelined exchange, but ordering it the same way costs nothing and
/// removes the same latent race there.
/// </summary>
/// <param name="Requests">Lines to write, in order, each written as one line.</param>
/// <param name="AwaitReply">
/// Recognizes the reply that must arrive before continuing. Null for a notification, which by
/// definition is not answered. The last turn that declares one produces the probe's answer.
/// </param>
public sealed record ProbeTurn(
    IReadOnlyList<string> Requests,
    Func<JsonElement, bool>? AwaitReply = null);

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
    /// <param name="turns">The handshake, in order. The last answered turn yields the result.</param>
    /// <param name="timeout">Overrides <see cref="DefaultTimeout"/>, and covers the whole exchange.</param>
    public async Task<(JsonElement? Answer, ModelDiscoveryFailure Failure)> ExchangeAsync(
        string executable,
        IReadOnlyList<string> arguments,
        IReadOnlyList<ProbeTurn> turns,
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
            JsonElement? answer = null;
            var read = 0;
            foreach (var turn in turns)
            {
                foreach (var request in turn.Requests)
                {
                    await process.StandardInput.WriteLineAsync(request.AsMemory(), combined.Token);
                    await process.StandardInput.FlushAsync(combined.Token);
                }

                if (turn.AwaitReply is not { } isReply)
                {
                    continue;
                }

                bool spoke;
                (answer, read, spoke) = await ReadAnswerAsync(process, isReply, read, combined.Token);
                if (answer is null)
                {
                    // Which of these it was matters to the operator. A process that emitted JSON we
                    // could not interpret has changed shape; one that emitted nothing and exited
                    // was never able to answer. Reporting the second as the first tells them the
                    // vendor replied when it did not.
                    return (null, spoke
                        ? ModelDiscoveryFailure.Unrecognized
                        : ModelDiscoveryFailure.Unavailable);
                }
            }

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

    /// <summary>
    /// Reads until <paramref name="isAnswer"/> matches. Threads the byte budget through rather than
    /// holding it in a field: one probe instance serves every discovery, so a field would make the
    /// cap accumulate across unrelated exchanges and eventually reject a first response.
    /// </summary>
    /// <param name="read">Bytes consumed so far, threaded through so the cap spans the exchange.</param>
    /// <returns>
    /// The matched reply, the running byte count, and whether the process produced any parseable
    /// JSON at all — the last distinguishes a vendor that changed its protocol from one that never
    /// spoke.
    /// </returns>
    private static async Task<(JsonElement? Answer, int Read, bool Spoke)> ReadAnswerAsync(
        Process process, Func<JsonElement, bool> isAnswer, int read, CancellationToken cancellationToken)
    {
        var spoke = false;
        while (await process.StandardOutput.ReadLineAsync(cancellationToken) is { } line)
        {
            read += Encoding.UTF8.GetByteCount(line);
            if (read > MaxResponseBytes)
            {
                return (null, read, spoke);
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

            spoke = true;
            if (isAnswer(element))
            {
                return (element, read, spoke);
            }
        }

        return (null, read, spoke);
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
