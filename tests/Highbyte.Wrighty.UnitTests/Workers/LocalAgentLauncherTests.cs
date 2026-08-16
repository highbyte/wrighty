using System.Diagnostics;
using Highbyte.Wrighty.Errors;
using Highbyte.Wrighty.Processes;
using Highbyte.Wrighty.Workers;

namespace Highbyte.Wrighty.UnitTests.Workers;

public sealed class LocalAgentLauncherTests
{
    private static readonly LocalAgentInvocation Invocation = new(
        "codex",
        ["resume", "019f-thread"],
        Path.GetTempPath(),
        new Dictionary<string, string> { ["WRIGHTY_TEST"] = "value" });

    [Fact]
    public void Unsupported_platform_capabilities_are_copy_only()
    {
        var launcher = Launcher(LocalAgentOperatingSystem.Unsupported);

        var capabilities = launcher.GetCapabilities("codex");

        Assert.False(capabilities.CanLaunchCli);
        Assert.False(capabilities.CanLaunchDesktop);
        Assert.Contains("native Windows", capabilities.CliUnavailableReason);
        Assert.Contains("not supported", capabilities.DesktopUnavailableReason);
    }

    [Fact]
    public void MacOS_capabilities_probe_and_cache_the_required_application()
    {
        var probes = 0;
        var launcher = Launcher(
            LocalAgentOperatingSystem.MacOS,
            applicationAvailable: (application, scheme) =>
            {
                probes++;
                return application == "ChatGPT" && scheme == "codex";
            });

        var first = launcher.GetCapabilities("codex");
        var second = launcher.GetCapabilities("codex");

        Assert.True(first.CanLaunchCli);
        Assert.True(first.CanLaunchDesktop);
        Assert.Null(first.DesktopUnavailableReason);
        Assert.Equal(first, second);
        Assert.Equal(1, probes);
    }

    [Theory]
    [InlineData((int)LocalAgentOperatingSystem.Windows, "claude", "Claude", "claude")]
    [InlineData((int)LocalAgentOperatingSystem.Windows, "codex", "ChatGPT", "codex")]
    [InlineData((int)LocalAgentOperatingSystem.Windows, "copilot", "GitHub Copilot", "ghapp")]
    [InlineData((int)LocalAgentOperatingSystem.Linux, "copilot", "GitHub Copilot", "ghapp")]
    public void Supported_non_macOS_desktop_capabilities_probe_the_URI_handler(
        int operatingSystemValue,
        string agent,
        string expectedApplication,
        string expectedScheme)
    {
        var operatingSystem = (LocalAgentOperatingSystem)operatingSystemValue;
        string? application = null;
        string? scheme = null;
        var launcher = Launcher(
            operatingSystem,
            applicationAvailable: (candidateApplication, candidateScheme) =>
            {
                application = candidateApplication;
                scheme = candidateScheme;
                return true;
            });

        var capabilities = launcher.GetCapabilities(agent);

        if (operatingSystem == LocalAgentOperatingSystem.Windows)
        {
            Assert.True(capabilities.CanLaunchCli);
            Assert.Null(capabilities.CliUnavailableReason);
        }
        else
        {
            Assert.False(capabilities.CanLaunchCli);
            Assert.Contains("native Windows", capabilities.CliUnavailableReason);
        }
        Assert.True(capabilities.CanLaunchDesktop);
        Assert.Null(capabilities.DesktopUnavailableReason);
        Assert.Equal(expectedApplication, application);
        Assert.Equal(expectedScheme, scheme);
    }

    [Theory]
    [InlineData("claude", "Claude")]
    [InlineData("codex", "ChatGPT")]
    public void Linux_rejects_desktop_applications_that_are_not_available_on_Linux(
        string agent,
        string expectedApplication)
    {
        var launcher = Launcher(LocalAgentOperatingSystem.Linux);

        var capabilities = launcher.GetCapabilities(agent);

        Assert.False(capabilities.CanLaunchDesktop);
        Assert.Contains(expectedApplication, capabilities.DesktopUnavailableReason);
        Assert.Contains("not supported", capabilities.DesktopUnavailableReason);
    }

    [Theory]
    [InlineData("claude", "Claude")]
    [InlineData("copilot", "GitHub Copilot")]
    public void Missing_desktop_application_has_a_bounded_reason(
        string agent,
        string expectedApplication)
    {
        var launcher = Launcher(
            LocalAgentOperatingSystem.MacOS,
            applicationAvailable: (_, _) => false);

        var capabilities = launcher.GetCapabilities(agent);

        Assert.True(capabilities.CanLaunchCli);
        Assert.False(capabilities.CanLaunchDesktop);
        Assert.Contains(expectedApplication, capabilities.DesktopUnavailableReason);
        Assert.Contains("is not installed", capabilities.DesktopUnavailableReason);
        Assert.Contains("handler is not registered", capabilities.DesktopUnavailableReason);
    }

    [Fact]
    public void Unknown_agent_has_a_bounded_desktop_reason()
    {
        var launcher = Launcher(LocalAgentOperatingSystem.Windows);

        var capabilities = launcher.GetCapabilities("unknown");

        Assert.False(capabilities.CanLaunchDesktop);
        Assert.Contains("The required Desktop application", capabilities.DesktopUnavailableReason);
        Assert.Contains("not supported", capabilities.DesktopUnavailableReason);
    }

    [Fact]
    public void Windows_capabilities_report_a_missing_Windows_Terminal()
    {
        var launcher = Launcher(
            LocalAgentOperatingSystem.Windows,
            resolver: new ThrowingResolver());

        var capabilities = launcher.GetCapabilities("codex");

        Assert.False(capabilities.CanLaunchCli);
        Assert.Contains("Windows Terminal", capabilities.CliUnavailableReason);
        Assert.Contains("app execution alias", capabilities.CliUnavailableReason);
    }

    [Fact]
    public void Default_platform_detects_the_current_operating_system()
    {
        var launcher = new LocalAgentSessionLauncher(new ThrowingResolver());

        var capabilities = launcher.GetCapabilities("unknown");

        Assert.False(capabilities.CanLaunchDesktop);
        Assert.Contains("not supported", capabilities.DesktopUnavailableReason);
        Assert.NotEqual(LocalAgentOperatingSystem.Unsupported,
            LocalAgentSessionLauncher.CurrentOperatingSystem());
    }

    [Theory]
    [InlineData((int)LocalAgentOperatingSystem.MacOS, "wrighty-tests-missing-app", "unused")]
    [InlineData((int)LocalAgentOperatingSystem.Windows, "unused", "wrighty-tests-no-handler")]
    [InlineData((int)LocalAgentOperatingSystem.Linux, "unused", "wrighty-tests-no-handler")]
    [InlineData((int)LocalAgentOperatingSystem.Unsupported, "unused", "unused")]
    public void Platform_application_probes_reject_missing_applications_and_handlers(
        int operatingSystemValue,
        string application,
        string scheme)
    {
        var operatingSystem = (LocalAgentOperatingSystem)operatingSystemValue;
        IExecutableResolver? executables = operatingSystem == LocalAgentOperatingSystem.Linux
            ? new FixedResolver(new PathExecutableResolver().Resolve("dotnet"))
            : null;
        var available = LocalAgentSessionLauncher.IsApplicationAvailable(
            operatingSystem,
            application,
            scheme,
            executables);

        Assert.False(available);
    }

    [Fact]
    public async Task Handoff_process_receives_arguments_and_environment()
    {
        var executable = new PathExecutableResolver().Resolve("dotnet");

        var result = await LocalAgentSessionLauncher.RunHandoffAsync(
            executable,
            ["--version"],
            new Dictionary<string, string> { ["WRIGHTY_HANDOFF_TEST"] = "value" },
            SessionLaunchStatus.Failed,
            "HANDOFF_FAILED",
            CancellationToken.None);

        Assert.True(result.Launched);
    }

    [Fact]
    public async Task Handoff_process_maps_a_nonzero_exit_to_the_requested_failure()
    {
        var executable = new PathExecutableResolver().Resolve("dotnet");

        var result = await LocalAgentSessionLauncher.RunHandoffAsync(
            executable,
            ["--wrighty-invalid-option"],
            null,
            SessionLaunchStatus.ApplicationMissing,
            "EXPECTED_FAILURE",
            CancellationToken.None);

        Assert.Equal(SessionLaunchStatus.ApplicationMissing, result.Status);
        Assert.Equal("EXPECTED_FAILURE", result.Message);
    }

    [Fact]
    public async Task Handoff_process_maps_start_errors_to_the_requested_failure()
    {
        var missingExecutable = Path.Combine(
            Path.GetTempPath(),
            $"wrighty-missing-{Guid.NewGuid():N}");

        var result = await LocalAgentSessionLauncher.RunHandoffAsync(
            missingExecutable,
            [],
            null,
            SessionLaunchStatus.Failed,
            "EXPECTED_FAILURE",
            CancellationToken.None);

        Assert.Equal(SessionLaunchStatus.Failed, result.Status);
        Assert.Equal("EXPECTED_FAILURE", result.Message);
    }

    [Fact]
    public async Task URI_opener_uses_shell_execution_for_the_allowlisted_address()
    {
        ProcessStartInfo? captured = null;

        var result = await LocalAgentSessionLauncher.OpenUriAsync(
            new Uri("codex://threads/thread-id"),
            startInfo => captured = startInfo,
            CancellationToken.None);

        Assert.True(result.Launched);
        Assert.NotNull(captured);
        Assert.Equal("codex://threads/thread-id", captured.FileName);
        Assert.True(captured.UseShellExecute);
    }

    [Fact]
    public async Task URI_opener_maps_shell_errors_to_a_missing_application()
    {
        var result = await LocalAgentSessionLauncher.OpenUriAsync(
            new Uri("codex://threads/thread-id"),
            _ => throw new InvalidOperationException("No handler"),
            CancellationToken.None);

        Assert.Equal(SessionLaunchStatus.ApplicationMissing, result.Status);
        Assert.Equal("DESKTOP_APP_UNAVAILABLE", result.Message);
    }

    [Fact]
    public async Task URI_opener_honors_cancellation_before_shell_execution()
    {
        var opened = false;
        var token = new CancellationToken(canceled: true);

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            LocalAgentSessionLauncher.OpenUriAsync(
                new Uri("codex://threads/thread-id"),
                _ => opened = true,
                token));

        Assert.False(opened);
    }

    [Fact]
    public async Task Execute_uses_the_structured_executable_and_returns_its_exit_code()
    {
        var launcher = Launcher(
            LocalAgentOperatingSystem.Linux,
            resolver: new FixedResolver("/bin/sh"));
        var invocation = Invocation with
        {
            Arguments = ["-c", "test \"$WRIGHTY_TEST\" = value; exit 7"]
        };

        var exitCode = await launcher.ExecuteAsync(invocation, CancellationToken.None);

        Assert.Equal(7, exitCode);
    }

    [Fact]
    public async Task Execute_wraps_process_start_failures()
    {
        var launcher = Launcher(
            LocalAgentOperatingSystem.Linux,
            resolver: new ThrowingResolver());

        var error = await Assert.ThrowsAsync<TrackerException>(
            () => launcher.ExecuteAsync(Invocation, CancellationToken.None));

        Assert.Equal("RESUME_EXEC_FAILED", error.Code);
        Assert.IsType<FileNotFoundException>(error.InnerException);
    }

    [Theory]
    [InlineData("sh", "/tmp")]
    [InlineData("codex", "relative")]
    [InlineData("codex", " ")]
    public async Task Invocation_must_use_an_allowlisted_executable_and_absolute_workspace(
        string executable,
        string workspace)
    {
        var launcher = Launcher(LocalAgentOperatingSystem.MacOS);
        var invocation = Invocation with
        {
            Executable = executable,
            WorkingDirectory = workspace
        };

        var error = await Assert.ThrowsAsync<TrackerException>(
            () => launcher.LaunchCliAsync(invocation, CancellationToken.None));

        Assert.Equal("SESSION_LAUNCH_NOT_ALLOWED", error.Code);
    }

    [Fact]
    public async Task CLI_launch_is_copy_only_on_an_unsupported_platform()
    {
        var launcher = Launcher(LocalAgentOperatingSystem.Linux);

        var result = await launcher.LaunchCliAsync(Invocation, CancellationToken.None);

        Assert.Equal(SessionLaunchStatus.Unsupported, result.Status);
        Assert.Contains("native Windows", result.Message);
    }

    [Fact]
    public async Task CLI_launch_reports_a_missing_agent_executable()
    {
        var launcher = Launcher(
            LocalAgentOperatingSystem.MacOS,
            resolver: new ThrowingResolver());

        var result = await launcher.LaunchCliAsync(Invocation, CancellationToken.None);

        Assert.Equal(SessionLaunchStatus.Unsupported, result.Status);
        Assert.Contains("not installed", result.Message);
    }

    [Fact]
    public async Task CLI_launch_builds_one_fixed_Terminal_handoff()
    {
        HandoffCall? call = null;
        var launcher = Launcher(
            LocalAgentOperatingSystem.MacOS,
            resolver: new FixedResolver("/opt/Codex Agent/codex"),
            handoff: Capture);
        var invocation = Invocation with
        {
            Arguments = ["resume", "thread with spaces"],
            WorkingDirectory = "/tmp/work space",
            Environment = new Dictionary<string, string> { ["TOKEN"] = "a'b" }
        };

        var result = await launcher.LaunchCliAsync(invocation, CancellationToken.None);

        Assert.True(result.Launched);
        Assert.NotNull(call);
        Assert.Equal("/usr/bin/osascript", call.Executable);
        Assert.Equal("-e", call.Arguments[0]);
        Assert.Contains("tell application \"Terminal\" to do script", call.Arguments[1]);
        Assert.Contains("/opt/Codex Agent/codex", call.Arguments[1]);
        Assert.Contains("thread with spaces", call.Arguments[1]);
        Assert.Equal(SessionLaunchStatus.Failed, call.FailureStatus);
        Assert.Equal("TERMINAL_LAUNCH_UNSUPPORTED", call.FailureCode);
        Assert.Null(call.Environment);

        Task<SessionLaunchResult> Capture(
            string executable,
            IReadOnlyList<string> arguments,
            IReadOnlyDictionary<string, string>? environment,
            SessionLaunchStatus failureStatus,
            string failureCode,
            CancellationToken _)
        {
            call = new HandoffCall(
                executable,
                arguments,
                environment,
                failureStatus,
                failureCode);
            return Task.FromResult(new SessionLaunchResult(SessionLaunchStatus.Launched));
        }
    }

    [Fact]
    public async Task CLI_launch_uses_a_new_Windows_Terminal_with_the_structured_invocation()
    {
        HandoffCall? call = null;
        var launcher = Launcher(
            LocalAgentOperatingSystem.Windows,
            resolver: new MappingResolver(new Dictionary<string, string>
            {
                ["wt"] = "/windows/wt.exe",
                ["codex"] = "/tools/codex.exe"
            }),
            handoff: Capture);
        var invocation = Invocation with
        {
            Arguments = ["resume", "thread with spaces"],
            WorkingDirectory = "/work/project with spaces",
            Environment = new Dictionary<string, string>
            {
                ["WRIGHTY_CLAIMANT_ID"] = "agent-id",
                ["WRIGHTY_FENCING_TOKEN"] = "token-value"
            }
        };

        var result = await launcher.LaunchCliAsync(invocation, CancellationToken.None);

        Assert.True(result.Launched);
        Assert.NotNull(call);
        Assert.Equal("/windows/wt.exe", call.Executable);
        Assert.Equal(
            [
                "-w",
                "new",
                "new-tab",
                "--startingDirectory",
                "/work/project with spaces",
                "--inheritEnvironment",
                "--",
                "/tools/codex.exe",
                "resume",
                "thread with spaces"
            ],
            call.Arguments);
        Assert.Equal("agent-id", call.Environment?["WRIGHTY_CLAIMANT_ID"]);
        Assert.Equal("token-value", call.Environment?["WRIGHTY_FENCING_TOKEN"]);
        Assert.Equal(SessionLaunchStatus.Failed, call.FailureStatus);
        Assert.Equal("TERMINAL_LAUNCH_UNSUPPORTED", call.FailureCode);

        Task<SessionLaunchResult> Capture(
            string executable,
            IReadOnlyList<string> arguments,
            IReadOnlyDictionary<string, string>? environment,
            SessionLaunchStatus failureStatus,
            string failureCode,
            CancellationToken _)
        {
            call = new HandoffCall(
                executable,
                arguments,
                environment,
                failureStatus,
                failureCode);
            return Task.FromResult(new SessionLaunchResult(SessionLaunchStatus.Launched));
        }
    }

    [Fact]
    public async Task CLI_launch_reports_when_Windows_Terminal_disappears_after_the_probe()
    {
        var launcher = Launcher(
            LocalAgentOperatingSystem.Windows,
            resolver: new DisappearingWindowsTerminalResolver());

        var result = await launcher.LaunchCliAsync(Invocation, CancellationToken.None);

        Assert.Equal(SessionLaunchStatus.Unsupported, result.Status);
        Assert.Contains("Windows Terminal", result.Message);
    }

    [Fact]
    public async Task Desktop_launch_is_unsupported_when_the_vendor_has_no_app_on_the_platform()
    {
        var launcher = Launcher(LocalAgentOperatingSystem.Linux);

        var result = await launcher.LaunchDesktopAsync(
            CodexAddress(),
            CancellationToken.None);

        Assert.Equal(SessionLaunchStatus.Unsupported, result.Status);
        Assert.Contains("not supported", result.Message);
    }

    [Fact]
    public async Task Desktop_launch_reports_a_missing_application()
    {
        var launcher = Launcher(
            LocalAgentOperatingSystem.Windows,
            applicationAvailable: (_, _) => false);

        var result = await launcher.LaunchDesktopAsync(
            CodexAddress(),
            CancellationToken.None);

        Assert.Equal(SessionLaunchStatus.ApplicationMissing, result.Status);
        Assert.Contains("ChatGPT is not installed", result.Message);
        Assert.Contains("codex:// handler is not registered", result.Message);
    }

    [Fact]
    public async Task Disabled_desktop_address_is_not_launched()
    {
        var launcher = Launcher(LocalAgentOperatingSystem.MacOS);
        var address = CodexAddress() with
        {
            Support = DesktopSessionSupport.Unavailable,
            Reason = "Compatibility has not been established."
        };

        var result = await launcher.LaunchDesktopAsync(
            address,
            CancellationToken.None);

        Assert.Equal(SessionLaunchStatus.Unsupported, result.Status);
        Assert.Equal(address.Reason, result.Message);
    }

    [Theory]
    [InlineData((int)LocalAgentOperatingSystem.MacOS)]
    [InlineData((int)LocalAgentOperatingSystem.Windows)]
    public async Task Desktop_launch_hands_the_allowlisted_URI_to_the_platform_opener(
        int operatingSystemValue)
    {
        var operatingSystem = (LocalAgentOperatingSystem)operatingSystemValue;
        Uri? openedUri = null;
        var launcher = Launcher(operatingSystem, openUri: Capture);

        var result = await launcher.LaunchDesktopAsync(
            CodexAddress(),
            CancellationToken.None);

        Assert.True(result.Launched);
        Assert.Equal("codex://threads/019f-thread", openedUri?.OriginalString);

        Task<SessionLaunchResult> Capture(Uri uri, CancellationToken _)
        {
            openedUri = uri;
            return Task.FromResult(new SessionLaunchResult(SessionLaunchStatus.Launched));
        }
    }

    [Fact]
    public async Task Copilot_desktop_launch_is_supported_on_Linux()
    {
        Uri? openedUri = null;
        var launcher = Launcher(
            LocalAgentOperatingSystem.Linux,
            openUri: (uri, _) =>
            {
                openedUri = uri;
                return Task.FromResult(new SessionLaunchResult(SessionLaunchStatus.Launched));
            });
        var address = new DesktopLaunchAddress(
            "copilot",
            new Uri("ghapp://sessions/session-id"),
            DesktopSessionSupport.Supported,
            null,
            "GitHub Copilot");

        var result = await launcher.LaunchDesktopAsync(address, CancellationToken.None);

        Assert.True(result.Launched);
        Assert.Equal("ghapp://sessions/session-id", openedUri?.OriginalString);
    }

    [Theory]
    [InlineData("unknown", "codex://threads/019f-thread")]
    [InlineData("codex", "https://example.invalid/thread")]
    public async Task Desktop_launch_rejects_non_adapter_addresses(
        string vendor,
        string uri)
    {
        var launcher = Launcher(LocalAgentOperatingSystem.MacOS);
        var address = CodexAddress() with
        {
            Vendor = vendor,
            Uri = new Uri(uri)
        };

        var error = await Assert.ThrowsAsync<TrackerException>(
            () => launcher.LaunchDesktopAsync(address, CancellationToken.None));

        Assert.Equal("SESSION_LAUNCH_NOT_ALLOWED", error.Code);
    }

    [Fact]
    public async Task Desktop_launch_rejects_a_missing_URI()
    {
        var launcher = Launcher(LocalAgentOperatingSystem.MacOS);
        var address = CodexAddress() with { Uri = null };

        var error = await Assert.ThrowsAsync<TrackerException>(
            () => launcher.LaunchDesktopAsync(address, CancellationToken.None));

        Assert.Equal("SESSION_LAUNCH_NOT_ALLOWED", error.Code);
    }

    private static LocalAgentSessionLauncher Launcher(
        LocalAgentOperatingSystem operatingSystem,
        IExecutableResolver? resolver = null,
        Func<string, string, bool>? applicationAvailable = null,
        Func<
            string,
            IReadOnlyList<string>,
            IReadOnlyDictionary<string, string>?,
            SessionLaunchStatus,
            string,
            CancellationToken,
            Task<SessionLaunchResult>>? handoff = null,
        Func<Uri, CancellationToken, Task<SessionLaunchResult>>? openUri = null) =>
        new(
            resolver ?? new FixedResolver("/usr/local/bin/codex"),
            new LocalAgentLaunchPlatform(
                operatingSystem,
                applicationAvailable ?? ((_, _) => true),
                handoff ?? ((_, _, _, _, _, _) =>
                    Task.FromResult(new SessionLaunchResult(SessionLaunchStatus.Launched))),
                openUri ?? ((_, _) =>
                    Task.FromResult(new SessionLaunchResult(SessionLaunchStatus.Launched)))));

    private static DesktopLaunchAddress CodexAddress() =>
        new(
            "codex",
            new Uri("codex://threads/019f-thread"),
            DesktopSessionSupport.Supported,
            null,
            "ChatGPT");

    private sealed record HandoffCall(
        string Executable,
        IReadOnlyList<string> Arguments,
        IReadOnlyDictionary<string, string>? Environment,
        SessionLaunchStatus FailureStatus,
        string FailureCode);

    private sealed class FixedResolver(string path) : IExecutableResolver
    {
        public string Resolve(string executableName) => path;
    }

    private sealed class ThrowingResolver : IExecutableResolver
    {
        public string Resolve(string executableName) =>
            throw new FileNotFoundException(executableName);
    }

    private sealed class MappingResolver(IReadOnlyDictionary<string, string> paths)
        : IExecutableResolver
    {
        public string Resolve(string executableName) =>
            paths.TryGetValue(executableName, out var path)
                ? path
                : throw new FileNotFoundException(executableName);
    }

    private sealed class DisappearingWindowsTerminalResolver : IExecutableResolver
    {
        private int terminalResolutions;

        public string Resolve(string executableName)
        {
            if (executableName == "codex")
                return "/tools/codex.exe";
            if (executableName == "wt" && terminalResolutions++ == 0)
                return "/windows/wt.exe";
            throw new FileNotFoundException(executableName);
        }
    }
}
