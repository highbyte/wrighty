using System.Diagnostics;
using System.Text;
using Highbyte.Wrighty.Errors;
using Highbyte.Wrighty.Processes;

namespace Highbyte.Wrighty.Workers;

public sealed record AgentProcessCallbacks(
    Func<CancellationToken, Task>? ProcessStarted = null,
    Func<string, CancellationToken, Task>? SessionStarted = null);

public interface IAgentProcessRunner
{
    Task<AgentRunResult> RunAsync(
        AgentInvocation invocation,
        IAgentAdapter adapter,
        TimeSpan timeout,
        IReadOnlyDictionary<string, string> grantEnvironment,
        Func<string, CancellationToken, Task>? sessionStarted,
        bool killOnCancellation,
        CancellationToken cancellationToken);

    Task<AgentRunResult> RunWithCallbacksAsync(
        AgentInvocation invocation,
        IAgentAdapter adapter,
        TimeSpan timeout,
        IReadOnlyDictionary<string, string> grantEnvironment,
        AgentProcessCallbacks callbacks,
        bool killOnCancellation,
        CancellationToken cancellationToken) =>
        RunAsync(invocation, adapter, timeout, grantEnvironment, callbacks.SessionStarted,
            killOnCancellation, cancellationToken);
}

public sealed class AgentProcessRunner(IExecutableResolver executables) : IAgentProcessRunner
{
    public Task<AgentRunResult> RunAsync(
        AgentInvocation invocation,
        IAgentAdapter adapter,
        TimeSpan timeout,
        IReadOnlyDictionary<string, string> grantEnvironment,
        Func<string, CancellationToken, Task>? sessionStarted,
        bool killOnCancellation,
        CancellationToken cancellationToken) =>
        RunWithCallbacksAsync(invocation, adapter, timeout, grantEnvironment,
            new AgentProcessCallbacks(SessionStarted: sessionStarted),
            killOnCancellation, cancellationToken);

    public async Task<AgentRunResult> RunWithCallbacksAsync(
        AgentInvocation invocation,
        IAgentAdapter adapter,
        TimeSpan timeout,
        IReadOnlyDictionary<string, string> grantEnvironment,
        AgentProcessCallbacks callbacks,
        bool killOnCancellation,
        CancellationToken cancellationToken)
    {
        if (timeout <= TimeSpan.Zero)
            throw new TrackerException("ARGUMENT_INVALID", "--item-timeout must be positive.", 2);

        using var process = StartProcess(invocation, grantEnvironment);
        if (callbacks.ProcessStarted is not null)
        {
            try
            {
                await callbacks.ProcessStarted(cancellationToken);
            }
            catch
            {
                Kill(process);
                await process.WaitForExitAsync(CancellationToken.None);
                throw;
            }
        }

        // The prompt goes here when there is one: an approved context on the command line would be
        // readable by every process on the machine, and would appear in the argument list worker
        // events print. Writing it and then closing gives the vendor a complete, terminated stream.
        //
        // With no prompt to write, standard input is closed immediately as before — none of the
        // headless adapters accept interactive input, and leaving it open makes codex exec hang on
        // a non-TTY with "Reading additional input from stdin...".
        if (invocation.StandardInput is { } promptText)
        {
            await process.StandardInput.WriteAsync(promptText.AsMemory(), cancellationToken);
            await process.StandardInput.FlushAsync(cancellationToken);
        }
        process.StandardInput.Close();
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await using var captured = new MemoryStream();
        await using var writer = new StreamWriter(captured, new UTF8Encoding(false), leaveOpen: true)
        {
            AutoFlush = true
        };
        var capture = new SessionCapture();
        var stdoutTask = CaptureStdoutAsync(
            process, writer, adapter, capture, callbacks.SessionStarted, cancellationToken);

        using var timeoutCts = new CancellationTokenSource(timeout);
        using var combined = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, timeoutCts.Token);
        try
        {
            await process.WaitForExitAsync(combined.Token);
            await stdoutTask;
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            Kill(process);
            await process.WaitForExitAsync(CancellationToken.None);
            return new AgentRunResult(AgentOutcome.TimedOut, capture.SessionId,
                $"Agent exceeded the {timeout} item timeout.", process.ExitCode);
        }
        catch (OperationCanceledException)
        {
            if (killOnCancellation)
            {
                Kill(process);
                await process.WaitForExitAsync(CancellationToken.None);
            }
            return new AgentRunResult(AgentOutcome.Rejected, capture.SessionId,
                "Agent run was fenced or cancelled.", process.HasExited ? process.ExitCode : -1);
        }

        await writer.FlushAsync(cancellationToken);
        captured.Position = 0;
        var rawStdout = Encoding.UTF8.GetString(captured.ToArray()).Trim();
        var interpreted = await adapter.InterpretAsync(captured, process.ExitCode, cancellationToken);
        return IncludeProcessDiagnostics(interpreted, rawStdout, await stderrTask);
    }

    private Process StartProcess(
        AgentInvocation invocation,
        IReadOnlyDictionary<string, string> grantEnvironment)
    {
        Process? process = null;
        try
        {
            var start = new ProcessStartInfo
            {
                FileName = executables.Resolve(invocation.Executable),
                WorkingDirectory = invocation.WorkingDirectory,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            foreach (var argument in invocation.Arguments)
                start.ArgumentList.Add(argument);
            foreach (var variable in invocation.EnvironmentVariablesToRemove ?? [])
                start.Environment.Remove(variable);
            foreach (var pair in invocation.Environment.Concat(grantEnvironment))
                start.Environment[pair.Key] = pair.Value;
            process = new Process { StartInfo = start, EnableRaisingEvents = true };
            if (!process.Start())
                throw new TrackerException("AGENT_START_FAILED",
                    $"Could not start {invocation.Executable}.", 7);
            return process;
        }
        catch (Exception exception)
        {
            process?.Dispose();
            if (exception is TrackerException)
                throw;
            throw new TrackerException("AGENT_START_FAILED",
                $"Could not start {invocation.Executable}: {exception.Message}", 7,
                innerException: exception);
        }
    }

    private static Task CaptureStdoutAsync(
        Process process,
        TextWriter writer,
        IAgentAdapter adapter,
        SessionCapture capture,
        Func<string, CancellationToken, Task>? sessionStarted,
        CancellationToken cancellationToken) =>
        Task.Run(async () =>
        {
            while (await process.StandardOutput.ReadLineAsync(cancellationToken) is { } line)
            {
                await writer.WriteLineAsync(line);
                if (capture.SessionId is not null ||
                    adapter.TryExtractSessionId(line) is not { } session)
                    continue;
                capture.SessionId = session;
                if (sessionStarted is not null)
                    await sessionStarted(session, cancellationToken);
            }
        }, CancellationToken.None);

    private static AgentRunResult IncludeProcessDiagnostics(
        AgentRunResult interpreted,
        string stdout,
        string stderr)
    {
        var error = Diagnostic(stderr);
        if (string.IsNullOrWhiteSpace(interpreted.FinalMessage))
        {
            var message = error ?? Diagnostic(stdout);
            return message is null ? interpreted : interpreted with { FinalMessage = message };
        }
        if (interpreted.Outcome != AgentOutcome.Rejected)
            return interpreted;

        var diagnostic = error is not null
            ? $"stderr: {error}"
            : Diagnostic(stdout) is { } output
                ? $"stdout: {output}"
                : null;
        return diagnostic is null
            ? interpreted
            : interpreted with { FinalMessage = $"{interpreted.FinalMessage} {diagnostic}" };
    }

    private static string? Diagnostic(string value)
    {
        const int maxLength = 2000;
        var trimmed = value.Trim();
        if (trimmed.Length == 0)
            return null;
        return trimmed.Length <= maxLength ? trimmed : $"{trimmed[..maxLength]}…";
    }

    private static void Kill(Process process)
    {
        if (!process.HasExited)
            process.Kill(entireProcessTree: true);
    }

    private sealed class SessionCapture
    {
        public string? SessionId { get; set; }
    }
}
