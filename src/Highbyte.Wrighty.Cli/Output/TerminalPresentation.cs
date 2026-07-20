using System.Runtime.InteropServices;
using Highbyte.Wrighty.Workers;

namespace Highbyte.Wrighty.Cli.Output;

public enum WorkerColorMode
{
    Auto,
    Always,
    Never
}

public sealed record TerminalStreamCapability(
    bool IsRedirected,
    bool SupportsAnsi);

public sealed record TerminalCapabilities(
    TerminalStreamCapability Output,
    TerminalStreamCapability Error,
    string? NoColor = null,
    string? Term = null)
{
    public static TerminalCapabilities Plain { get; } = new(
        new TerminalStreamCapability(true, false),
        new TerminalStreamCapability(true, false));

    public static TerminalCapabilities Detect()
    {
        var outputRedirected = Console.IsOutputRedirected;
        var errorRedirected = Console.IsErrorRedirected;
        return new TerminalCapabilities(
            new TerminalStreamCapability(
                outputRedirected,
                !outputRedirected && SupportsAnsi(StandardHandle.Output)),
            new TerminalStreamCapability(
                errorRedirected,
                !errorRedirected && SupportsAnsi(StandardHandle.Error)),
            Environment.GetEnvironmentVariable("NO_COLOR"),
            Environment.GetEnvironmentVariable("TERM"));
    }

    private static bool SupportsAnsi(StandardHandle handle)
    {
        if (!OperatingSystem.IsWindows())
            return true;

        var stream = GetStdHandle((int)handle);
        return stream != IntPtr.Zero &&
               stream != new IntPtr(-1) &&
               GetConsoleMode(stream, out var mode) &&
               (mode & EnableVirtualTerminalProcessing) != 0;
    }

    private const uint EnableVirtualTerminalProcessing = 0x0004;

    private enum StandardHandle
    {
        Output = -11,
        Error = -12
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int standardHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetConsoleMode(IntPtr consoleHandle, out uint mode);
}

internal sealed class WorkerTerminalStyler(
    TerminalCapabilities capabilities,
    WorkerColorMode colorMode)
{
    private const string Reset = "\u001b[0m";

    public string EventPrefix(string eventType, bool errorStream = false)
    {
        var semantic = WorkerEventClassifier.Classify(eventType);
        if (semantic is null || !UseColor(errorStream))
            return $"{eventType}:";

        var start = semantic.Value switch
        {
            WorkerEventSemantic.Success => "\u001b[32m",
            WorkerEventSemantic.Info => "\u001b[36m",
            WorkerEventSemantic.Warning => "\u001b[33m",
            WorkerEventSemantic.Danger => "\u001b[31m",
            WorkerEventSemantic.Muted => "\u001b[2m",
            _ => ""
        };
        return $"{start}{eventType}:{Reset}";
    }

    public string WarningPrefix() =>
        UseColor(errorStream: true) ? $"\u001b[33mwarning:{Reset}" : "warning:";

    private bool UseColor(bool errorStream)
    {
        if (colorMode == WorkerColorMode.Never)
            return false;
        if (colorMode == WorkerColorMode.Always)
            return true;

        var stream = errorStream ? capabilities.Error : capabilities.Output;
        return !stream.IsRedirected &&
               stream.SupportsAnsi &&
               capabilities.NoColor is null &&
               !string.Equals(capabilities.Term, "dumb", StringComparison.OrdinalIgnoreCase);
    }
}
