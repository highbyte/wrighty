using System.Diagnostics;
using System.Collections.Concurrent;
using Highbyte.Wrighty.Errors;
using Highbyte.Wrighty.Processes;

namespace Highbyte.Wrighty.Workers;

public enum SessionLaunchStatus
{
    Launched,
    Unsupported,
    ApplicationMissing,
    Failed
}

public sealed record SessionLaunchResult(SessionLaunchStatus Status, string? Message = null)
{
    public bool Launched => Status == SessionLaunchStatus.Launched;
}

public sealed record LocalSessionLaunchCapabilities(
    bool CanLaunchCli,
    bool CanLaunchDesktop,
    string? CliUnavailableReason = null,
    string? DesktopUnavailableReason = null);

/// <summary>
/// Launches only adapter-produced local session addresses. Direct execution is used by the CLI;
/// web console launches hand off to a new terminal or an allowlisted Desktop URI and return.
/// </summary>
public interface ILocalAgentSessionLauncher
{
    LocalSessionLaunchCapabilities GetCapabilities(string agentType);

    Task<int> ExecuteAsync(
        LocalAgentInvocation invocation,
        CancellationToken cancellationToken);

    Task<SessionLaunchResult> LaunchCliAsync(
        LocalAgentInvocation invocation,
        CancellationToken cancellationToken);

    Task<SessionLaunchResult> LaunchDesktopAsync(
        DesktopLaunchAddress address,
        CancellationToken cancellationToken);
}

internal sealed record LocalAgentLaunchPlatform(
    bool IsMacOS,
    Func<string, bool> IsApplicationAvailable,
    Func<
        string,
        IReadOnlyList<string>,
        SessionLaunchStatus,
        string,
        CancellationToken,
        Task<SessionLaunchResult>> RunHandoffAsync);

public sealed class LocalAgentSessionLauncher : ILocalAgentSessionLauncher
{
    private const string ClaudeAgent = "claude";
    private const string CodexAgent = "codex";
    private const string CopilotAgent = "copilot";
    private static readonly HashSet<string> AllowedExecutables =
        [ClaudeAgent, CodexAgent, CopilotAgent];
    private static readonly Dictionary<string, string> AllowedDesktopSchemes =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [ClaudeAgent] = ClaudeAgent,
            [CodexAgent] = CodexAgent,
            [CopilotAgent] = "ghapp"
        };
    private static readonly Dictionary<string, string> DesktopApplications =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [ClaudeAgent] = "Claude",
            [CodexAgent] = "ChatGPT",
            [CopilotAgent] = "GitHub Copilot"
        };
    private readonly IExecutableResolver executables;
    private readonly LocalAgentLaunchPlatform platform;
    private readonly ConcurrentDictionary<string, bool> applicationAvailability =
        new(StringComparer.OrdinalIgnoreCase);

    public LocalAgentSessionLauncher(IExecutableResolver executables)
        : this(
            executables,
            new LocalAgentLaunchPlatform(
                OperatingSystem.IsMacOS(),
                IsApplicationAvailable,
                RunHandoffAsync))
    {
    }

    internal LocalAgentSessionLauncher(
        IExecutableResolver executables,
        LocalAgentLaunchPlatform platform)
    {
        this.executables = executables;
        this.platform = platform;
    }

    public LocalSessionLaunchCapabilities GetCapabilities(string agentType)
    {
        if (!platform.IsMacOS)
            return new LocalSessionLaunchCapabilities(
                false,
                false,
                "Opening a new agent terminal is currently supported on macOS only.",
                "Opening an agent Desktop session is currently supported on macOS only.");
        var desktopAvailable =
            DesktopApplications.TryGetValue(agentType, out var application) &&
            applicationAvailability.GetOrAdd(application, platform.IsApplicationAvailable);
        return new LocalSessionLaunchCapabilities(
            true,
            desktopAvailable,
            DesktopUnavailableReason: desktopAvailable
                ? null
                : $"{(application ?? "The required Desktop application")} is not installed.");
    }

    public async Task<int> ExecuteAsync(
        LocalAgentInvocation invocation,
        CancellationToken cancellationToken)
    {
        ValidateInvocation(invocation);
        using var process = StartInvocation(invocation);
        await process.WaitForExitAsync(cancellationToken);
        return process.ExitCode;
    }

    public async Task<SessionLaunchResult> LaunchCliAsync(
        LocalAgentInvocation invocation,
        CancellationToken cancellationToken)
    {
        ValidateInvocation(invocation);
        if (!platform.IsMacOS)
            return new SessionLaunchResult(
                SessionLaunchStatus.Unsupported,
                "Opening a new agent terminal is currently supported on macOS only.");
        if (!executables.TryResolve(invocation.Executable, out var executablePath))
            return new SessionLaunchResult(
                SessionLaunchStatus.Unsupported,
                "The recorded agent executable is not installed.");

        var vendorCommand = string.Join(
            " ",
            new[] { InteractiveAgentCommand.Quote(executablePath!) }
                .Concat(invocation.Arguments.Select(InteractiveAgentCommand.Quote)));
        var command = InteractiveAgentCommand.Build(
            new Workspace(invocation.WorkingDirectory),
            vendorCommand,
            invocation.Environment);
        var appleScript =
            "tell application \"Terminal\" to do script " + AppleScriptString(command);
        return await platform.RunHandoffAsync(
            "/usr/bin/osascript",
            ["-e", appleScript],
            SessionLaunchStatus.Failed,
            "TERMINAL_LAUNCH_UNSUPPORTED",
            cancellationToken);
    }

    public async Task<SessionLaunchResult> LaunchDesktopAsync(
        DesktopLaunchAddress address,
        CancellationToken cancellationToken)
    {
        ValidateDesktopAddress(address);
        if (!platform.IsMacOS)
            return new SessionLaunchResult(
                SessionLaunchStatus.Unsupported,
                "Opening an agent Desktop session is currently supported on macOS only.");
        if (!GetCapabilities(address.Vendor).CanLaunchDesktop)
            return new SessionLaunchResult(
                SessionLaunchStatus.ApplicationMissing,
                $"{address.RequiredApplication} is not installed.");
        if (!address.CanLaunch)
            return new SessionLaunchResult(
                SessionLaunchStatus.Unsupported,
                address.Reason ?? "This Desktop session address is not enabled.");

        return await platform.RunHandoffAsync(
            "/usr/bin/open",
            [address.Uri!.OriginalString],
            SessionLaunchStatus.ApplicationMissing,
            "DESKTOP_APP_UNAVAILABLE",
            cancellationToken);
    }

    private Process StartInvocation(LocalAgentInvocation invocation)
    {
        try
        {
            var start = new ProcessStartInfo
            {
                FileName = executables.Resolve(invocation.Executable),
                WorkingDirectory = invocation.WorkingDirectory,
                UseShellExecute = false
            };
            foreach (var argument in invocation.Arguments)
                start.ArgumentList.Add(argument);
            foreach (var pair in invocation.Environment)
                start.Environment[pair.Key] = pair.Value;
            return Process.Start(start)
                ?? throw new TrackerException(
                    "RESUME_EXEC_FAILED", "Could not launch the recorded session.", 7);
        }
        catch (Exception exception) when (exception is not TrackerException)
        {
            throw new TrackerException(
                "RESUME_EXEC_FAILED",
                "Could not launch the recorded session.",
                7,
                innerException: exception);
        }
    }

    private static async Task<SessionLaunchResult> RunHandoffAsync(
        string executable,
        IReadOnlyList<string> arguments,
        SessionLaunchStatus failureStatus,
        string failureCode,
        CancellationToken cancellationToken)
    {
        try
        {
            var start = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            };
            foreach (var argument in arguments)
                start.ArgumentList.Add(argument);
            using var process = Process.Start(start);
            if (process is null)
                return new SessionLaunchResult(failureStatus, failureCode);
            await process.WaitForExitAsync(cancellationToken);
            return process.ExitCode == 0
                ? new SessionLaunchResult(SessionLaunchStatus.Launched)
                : new SessionLaunchResult(failureStatus, failureCode);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return new SessionLaunchResult(failureStatus, failureCode);
        }
    }

    private static void ValidateInvocation(LocalAgentInvocation invocation)
    {
        if (!AllowedExecutables.Contains(invocation.Executable) ||
            string.IsNullOrWhiteSpace(invocation.WorkingDirectory) ||
            !Path.IsPathFullyQualified(invocation.WorkingDirectory))
        {
            throw new TrackerException(
                "SESSION_LAUNCH_NOT_ALLOWED",
                "The requested local agent invocation is not allowlisted.",
                3);
        }
    }

    private static void ValidateDesktopAddress(DesktopLaunchAddress address)
    {
        if (!AllowedDesktopSchemes.TryGetValue(address.Vendor, out var expectedScheme) ||
            address.Uri is not { IsAbsoluteUri: true } uri ||
            !string.Equals(uri.Scheme, expectedScheme, StringComparison.OrdinalIgnoreCase))
        {
            throw new TrackerException(
                "SESSION_LAUNCH_NOT_ALLOWED",
                "The requested Desktop session address is not allowlisted.",
                3);
        }
    }

    private static string AppleScriptString(string value) =>
        $"\"{value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)}\"";

    private static bool IsApplicationAvailable(string application)
    {
        try
        {
            var start = new ProcessStartInfo("/usr/bin/open")
            {
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            };
            start.ArgumentList.Add("-Ra");
            start.ArgumentList.Add(application);
            using var process = Process.Start(start);
            if (process is null)
                return false;
            if (!process.WaitForExit(TimeSpan.FromSeconds(2)))
            {
                process.Kill();
                return false;
            }
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}
