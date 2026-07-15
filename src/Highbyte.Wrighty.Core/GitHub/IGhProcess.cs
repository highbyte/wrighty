namespace Highbyte.Wrighty.GitHub;

public interface IGhProcess
{
    Task<GhProcessResult> RunAsync(
        IReadOnlyList<string> arguments,
        string? standardInput,
        CancellationToken cancellationToken);
}

public sealed record GhProcessResult(int ExitCode, string StandardOutput, string StandardError);
