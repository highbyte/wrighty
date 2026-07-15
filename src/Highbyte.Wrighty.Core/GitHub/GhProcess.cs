using System.ComponentModel;
using System.Diagnostics;
using Highbyte.Wrighty.Errors;
using Highbyte.Wrighty.Processes;

namespace Highbyte.Wrighty.GitHub;

public sealed class GhProcess : IGhProcess
{
    private readonly IExecutableResolver executableResolver;

    public GhProcess()
        : this(new PathExecutableResolver())
    {
    }

    public GhProcess(IExecutableResolver executableResolver)
    {
        this.executableResolver = executableResolver;
    }

    public async Task<GhProcessResult> RunAsync(
        IReadOnlyList<string> arguments,
        string? standardInput,
        CancellationToken cancellationToken)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = executableResolver.Resolve("gh"),
                RedirectStandardInput = standardInput is not null,
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

            if (standardInput is not null)
            {
                await process.StandardInput.WriteAsync(standardInput.AsMemory(), cancellationToken);
                process.StandardInput.Close();
            }

            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);

            return new GhProcessResult(
                process.ExitCode,
                await outputTask,
                await errorTask);
        }
        catch (Exception exception) when (exception is Win32Exception or FileNotFoundException)
        {
            throw new TrackerException(
                "GH_NOT_FOUND",
                "GitHub CLI (gh) was not found. Install it and authenticate before using Wrighty.",
                4,
                innerException: exception);
        }
    }
}
