namespace Highbyte.Wrighty.Errors;

public sealed class TrackerException(
    string code,
    string message,
    int exitCode = 10,
    IReadOnlyDictionary<string, object?>? details = null,
    Exception? innerException = null)
    : Exception(message, innerException)
{
    public string Code { get; } = code;

    public int ExitCode { get; } = exitCode;

    public IReadOnlyDictionary<string, object?> Details { get; } =
        details ?? new Dictionary<string, object?>();
}
