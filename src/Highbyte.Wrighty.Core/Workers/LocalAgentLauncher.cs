using System.Diagnostics;
using System.Collections.Concurrent;
using Microsoft.Win32;
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

internal enum LocalAgentOperatingSystem
{
    MacOS,
    Windows,
    Linux,
    Unsupported
}

internal sealed record LocalAgentLaunchPlatform(
    LocalAgentOperatingSystem OperatingSystem,
    Func<string, string, bool> IsApplicationAvailable,
    Func<
        string,
        IReadOnlyList<string>,
        IReadOnlyDictionary<string, string>?,
        SessionLaunchStatus,
        string,
        CancellationToken,
        Task<SessionLaunchResult>> RunHandoffAsync,
    Func<Uri, CancellationToken, Task<SessionLaunchResult>> OpenUriAsync);

public sealed class LocalAgentSessionLauncher : ILocalAgentSessionLauncher
{
    private const string ClaudeAgent = "claude";
    private const string CodexAgent = "codex";
    private const string CopilotAgent = "copilot";
    private const string WindowsTerminalExecutable = "wt";
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
    private readonly ConcurrentDictionary<string, bool> executableAvailability =
        new(StringComparer.OrdinalIgnoreCase);

    public LocalAgentSessionLauncher(IExecutableResolver executables)
        : this(executables, CreatePlatform(executables))
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
        var (canLaunchCli, cliUnavailableReason) = GetCliLaunchCapability();
        if (!DesktopApplications.TryGetValue(agentType, out var application) ||
            !AllowedDesktopSchemes.TryGetValue(agentType, out var scheme) ||
            !SupportsDesktop(agentType, platform.OperatingSystem))
        {
            return new LocalSessionLaunchCapabilities(
                canLaunchCli,
                false,
                cliUnavailableReason,
                $"{(application ?? "The required Desktop application")} is not supported on " +
                "this operating system.");
        }
        var desktopAvailable =
            applicationAvailability.GetOrAdd(
                agentType,
                _ => platform.IsApplicationAvailable(application, scheme));
        return new LocalSessionLaunchCapabilities(
            canLaunchCli,
            desktopAvailable,
            cliUnavailableReason,
            DesktopUnavailableReason: desktopAvailable
                ? null
                : $"{application} is not installed or its {scheme}:// handler is not registered.");
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
        var (canLaunchCli, unavailableReason) = GetCliLaunchCapability();
        if (!canLaunchCli)
            return new SessionLaunchResult(
                SessionLaunchStatus.Unsupported,
                unavailableReason);
        if (!executables.TryResolve(invocation.Executable, out var executablePath))
            return new SessionLaunchResult(
                SessionLaunchStatus.Unsupported,
                "The recorded agent executable is not installed.");

        if (platform.OperatingSystem == LocalAgentOperatingSystem.Windows)
            return await LaunchWindowsTerminalAsync(
                invocation,
                executablePath!,
                cancellationToken);

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
            null,
            SessionLaunchStatus.Failed,
            "TERMINAL_LAUNCH_UNSUPPORTED",
            cancellationToken);
    }

    public async Task<SessionLaunchResult> LaunchDesktopAsync(
        DesktopLaunchAddress address,
        CancellationToken cancellationToken)
    {
        ValidateDesktopAddress(address);
        var capabilities = GetCapabilities(address.Vendor);
        if (!SupportsDesktop(address.Vendor, platform.OperatingSystem))
            return new SessionLaunchResult(
                SessionLaunchStatus.Unsupported,
                capabilities.DesktopUnavailableReason);
        if (!capabilities.CanLaunchDesktop)
            return new SessionLaunchResult(
                SessionLaunchStatus.ApplicationMissing,
                capabilities.DesktopUnavailableReason);
        if (!address.CanLaunch)
            return new SessionLaunchResult(
                SessionLaunchStatus.Unsupported,
                address.Reason ?? "This Desktop session address is not enabled.");

        return await platform.OpenUriAsync(address.Uri!, cancellationToken);
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

    internal static async Task<SessionLaunchResult> RunHandoffAsync(
        string executable,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string>? environment,
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
            if (environment is not null)
            {
                foreach (var pair in environment)
                    start.Environment[pair.Key] = pair.Value;
            }
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

    private (bool CanLaunch, string? UnavailableReason) GetCliLaunchCapability()
    {
        if (platform.OperatingSystem == LocalAgentOperatingSystem.MacOS)
            return (true, null);
        if (platform.OperatingSystem == LocalAgentOperatingSystem.Windows)
        {
            var terminalAvailable = executableAvailability.GetOrAdd(
                WindowsTerminalExecutable,
                executable => executables.TryResolve(executable, out _));
            return terminalAvailable
                ? (true, null)
                : (false, "Windows Terminal (wt.exe) is not installed or its app execution " +
                    "alias is unavailable.");
        }
        return (false, "Opening a new agent terminal is currently supported on macOS and " +
            "native Windows only.");
    }

    private async Task<SessionLaunchResult> LaunchWindowsTerminalAsync(
        LocalAgentInvocation invocation,
        string agentExecutable,
        CancellationToken cancellationToken)
    {
        if (!executables.TryResolve(WindowsTerminalExecutable, out var terminalExecutable))
            return new SessionLaunchResult(
                SessionLaunchStatus.Unsupported,
                "Windows Terminal (wt.exe) is not installed or its app execution alias is " +
                "unavailable.");

        var arguments = new List<string>
        {
            "-w",
            "new",
            "new-tab",
            "--startingDirectory",
            invocation.WorkingDirectory,
            "--inheritEnvironment",
            "--",
            agentExecutable
        };
        arguments.AddRange(invocation.Arguments);
        return await platform.RunHandoffAsync(
            terminalExecutable!,
            arguments,
            invocation.Environment,
            SessionLaunchStatus.Failed,
            "TERMINAL_LAUNCH_UNSUPPORTED",
            cancellationToken);
    }

    private static LocalAgentLaunchPlatform CreatePlatform(IExecutableResolver executables)
    {
        var operatingSystem = CurrentOperatingSystem();
        return new LocalAgentLaunchPlatform(
            operatingSystem,
            (application, scheme) => IsApplicationAvailable(
                operatingSystem,
                application,
                scheme,
                executables),
            RunHandoffAsync,
            OpenUriAsync);
    }

    internal static LocalAgentOperatingSystem CurrentOperatingSystem()
    {
        if (OperatingSystem.IsMacOS())
            return LocalAgentOperatingSystem.MacOS;
        if (OperatingSystem.IsWindows())
            return LocalAgentOperatingSystem.Windows;
        if (OperatingSystem.IsLinux())
            return LocalAgentOperatingSystem.Linux;
        return LocalAgentOperatingSystem.Unsupported;
    }

    private static bool SupportsDesktop(
        string agentType,
        LocalAgentOperatingSystem operatingSystem) =>
        operatingSystem switch
        {
            LocalAgentOperatingSystem.MacOS => AllowedDesktopSchemes.ContainsKey(agentType),
            LocalAgentOperatingSystem.Windows => AllowedDesktopSchemes.ContainsKey(agentType),
            LocalAgentOperatingSystem.Linux =>
                string.Equals(agentType, CopilotAgent, StringComparison.OrdinalIgnoreCase),
            _ => false
        };

    internal static bool IsApplicationAvailable(
        LocalAgentOperatingSystem operatingSystem,
        string application,
        string scheme,
        IExecutableResolver? executables = null) =>
        operatingSystem switch
        {
            LocalAgentOperatingSystem.MacOS => IsMacApplicationAvailable(application),
            LocalAgentOperatingSystem.Windows => IsWindowsUriSchemeRegistered(scheme),
            LocalAgentOperatingSystem.Linux => IsLinuxUriSchemeRegistered(
                scheme,
                executables ?? new PathExecutableResolver()),
            _ => false
        };

    private static bool IsMacApplicationAvailable(string application)
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

    private static bool IsWindowsUriSchemeRegistered(string scheme)
    {
        if (!OperatingSystem.IsWindows())
            return false;
        try
        {
            using var key = Registry.ClassesRoot.OpenSubKey(scheme);
            return key?.GetValue("URL Protocol") is not null;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsLinuxUriSchemeRegistered(
        string scheme,
        IExecutableResolver executables)
    {
        try
        {
            if (!executables.TryResolve("xdg-mime", out var executablePath) ||
                executablePath is null ||
                !Path.IsPathFullyQualified(executablePath))
            {
                return false;
            }
            var start = new ProcessStartInfo(executablePath)
            {
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            };
            start.ArgumentList.Add("query");
            start.ArgumentList.Add("default");
            start.ArgumentList.Add($"x-scheme-handler/{scheme}");
            using var process = Process.Start(start);
            if (process is null)
                return false;
            if (!process.WaitForExit(TimeSpan.FromSeconds(2)))
            {
                process.Kill();
                return false;
            }
            return process.ExitCode == 0 &&
                   !string.IsNullOrWhiteSpace(process.StandardOutput.ReadToEnd());
        }
        catch
        {
            return false;
        }
    }

    private static Task<SessionLaunchResult> OpenUriAsync(
        Uri uri,
        CancellationToken cancellationToken) =>
        OpenUriAsync(
            uri,
            cancellationToken,
            static startInfo =>
            {
                using var process = Process.Start(startInfo);
            });

    internal static Task<SessionLaunchResult> OpenUriAsync(
        Uri uri,
        CancellationToken cancellationToken,
        Action<ProcessStartInfo> open)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            open(new ProcessStartInfo(uri.OriginalString)
            {
                UseShellExecute = true
            });
            return Task.FromResult(new SessionLaunchResult(SessionLaunchStatus.Launched));
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return Task.FromResult(new SessionLaunchResult(
                SessionLaunchStatus.ApplicationMissing,
                "DESKTOP_APP_UNAVAILABLE"));
        }
    }
}
