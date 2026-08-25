using System.Diagnostics;
using System.Text;
using Highbyte.Wrighty.Processes;

namespace Highbyte.Wrighty.Workers;

internal enum BoundedAgentCommandStatus
{
    Completed,
    NotInstalled,
    TimedOut,
    OutputTooLarge,
    Unavailable
}

internal sealed record BoundedAgentCommandResult(
    BoundedAgentCommandStatus Status,
    int ExitCode = -1,
    string StandardOutput = "",
    string StandardError = "");

/// <summary>Runs a non-interactive vendor metadata command with bounded output and lifetime.</summary>
internal interface IBoundedAgentCommand
{
    Task<BoundedAgentCommandResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        int maximumOutputBytes,
        TimeSpan timeout,
        CancellationToken cancellationToken);
}

internal sealed class BoundedAgentCommand(IExecutableResolver executables) : IBoundedAgentCommand
{
    private const int MaximumErrorBytes = 64 * 1024;

    public async Task<BoundedAgentCommandResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        int maximumOutputBytes,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (!executables.TryResolve(executable, out var path) || path is null)
            return new BoundedAgentCommandResult(BoundedAgentCommandStatus.NotInstalled);

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(path)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };
        foreach (var argument in arguments)
            process.StartInfo.ArgumentList.Add(argument);

        try
        {
            if (!process.Start())
                return new BoundedAgentCommandResult(BoundedAgentCommandStatus.Unavailable);
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return new BoundedAgentCommandResult(BoundedAgentCommandStatus.Unavailable);
        }

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(timeout);
        try
        {
            var standardOutput = ReadBoundedAsync(
                process.StandardOutput,
                maximumOutputBytes,
                budget.Token);
            var standardError = ReadBoundedAsync(
                process.StandardError,
                Math.Min(maximumOutputBytes, MaximumErrorBytes),
                budget.Token);
            await process.WaitForExitAsync(budget.Token);
            var output = await standardOutput;
            var error = await standardError;
            if (output.Exceeded || error.Exceeded)
            {
                return new BoundedAgentCommandResult(
                    BoundedAgentCommandStatus.OutputTooLarge,
                    process.ExitCode);
            }
            return new BoundedAgentCommandResult(
                BoundedAgentCommandStatus.Completed,
                process.ExitCode,
                output.Text,
                error.Text);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Kill(process);
            throw;
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            return new BoundedAgentCommandResult(BoundedAgentCommandStatus.TimedOut);
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException)
        {
            Kill(process);
            return new BoundedAgentCommandResult(BoundedAgentCommandStatus.Unavailable);
        }
        finally
        {
            Kill(process);
        }
    }

    private static async Task<(string Text, bool Exceeded)> ReadBoundedAsync(
        StreamReader reader,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        var builder = new StringBuilder(Math.Min(maximumBytes, 64 * 1024));
        var buffer = new char[4096];
        var bytes = 0;
        var exceeded = false;
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken)) > 0)
        {
            if (exceeded)
                continue;
            bytes += Encoding.UTF8.GetByteCount(buffer.AsSpan(0, read));
            if (bytes > maximumBytes)
            {
                exceeded = true;
                builder.Clear();
                continue;
            }
            builder.Append(buffer, 0, read);
        }
        return (builder.ToString(), exceeded);
    }

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or NotSupportedException)
        {
            // The process already exited, or the platform cannot enumerate its tree.
        }
    }
}
