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
    public void Non_macOS_capabilities_are_copy_only()
    {
        var launcher = Launcher(isMacOS: false);

        var capabilities = launcher.GetCapabilities("codex");

        Assert.False(capabilities.CanLaunchCli);
        Assert.False(capabilities.CanLaunchDesktop);
        Assert.Contains("macOS only", capabilities.CliUnavailableReason);
        Assert.Contains("macOS only", capabilities.DesktopUnavailableReason);
    }

    [Fact]
    public void MacOS_capabilities_probe_and_cache_the_required_application()
    {
        var probes = 0;
        var launcher = Launcher(
            isMacOS: true,
            applicationAvailable: application =>
            {
                probes++;
                return application == "ChatGPT";
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
    [InlineData("claude", "Claude")]
    [InlineData("copilot", "GitHub Copilot")]
    [InlineData("unknown", "The required Desktop application")]
    public void Missing_desktop_application_has_a_bounded_reason(
        string agent,
        string expectedApplication)
    {
        var launcher = Launcher(isMacOS: true, applicationAvailable: _ => false);

        var capabilities = launcher.GetCapabilities(agent);

        Assert.True(capabilities.CanLaunchCli);
        Assert.False(capabilities.CanLaunchDesktop);
        Assert.Contains(expectedApplication, capabilities.DesktopUnavailableReason);
        Assert.EndsWith("is not installed.", capabilities.DesktopUnavailableReason);
    }

    [Fact]
    public async Task Execute_uses_the_structured_executable_and_returns_its_exit_code()
    {
        var launcher = Launcher(
            isMacOS: false,
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
            isMacOS: false,
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
        var launcher = Launcher(isMacOS: true);
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
    public async Task CLI_launch_is_copy_only_off_macOS()
    {
        var launcher = Launcher(isMacOS: false);

        var result = await launcher.LaunchCliAsync(Invocation, CancellationToken.None);

        Assert.Equal(SessionLaunchStatus.Unsupported, result.Status);
        Assert.Contains("macOS only", result.Message);
    }

    [Fact]
    public async Task CLI_launch_reports_a_missing_agent_executable()
    {
        var launcher = Launcher(
            isMacOS: true,
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
            isMacOS: true,
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

        Task<SessionLaunchResult> Capture(
            string executable,
            IReadOnlyList<string> arguments,
            SessionLaunchStatus failureStatus,
            string failureCode,
            CancellationToken _)
        {
            call = new HandoffCall(executable, arguments, failureStatus, failureCode);
            return Task.FromResult(new SessionLaunchResult(SessionLaunchStatus.Launched));
        }
    }

    [Fact]
    public async Task Desktop_launch_is_copy_only_off_macOS()
    {
        var launcher = Launcher(isMacOS: false);

        var result = await launcher.LaunchDesktopAsync(
            CodexAddress(),
            CancellationToken.None);

        Assert.Equal(SessionLaunchStatus.Unsupported, result.Status);
        Assert.Contains("macOS only", result.Message);
    }

    [Fact]
    public async Task Desktop_launch_reports_a_missing_application()
    {
        var launcher = Launcher(
            isMacOS: true,
            applicationAvailable: _ => false);

        var result = await launcher.LaunchDesktopAsync(
            CodexAddress(),
            CancellationToken.None);

        Assert.Equal(SessionLaunchStatus.ApplicationMissing, result.Status);
        Assert.Equal("ChatGPT is not installed.", result.Message);
    }

    [Fact]
    public async Task Disabled_desktop_address_is_not_launched()
    {
        var launcher = Launcher(isMacOS: true);
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

    [Fact]
    public async Task Desktop_launch_hands_the_allowlisted_URI_to_open()
    {
        HandoffCall? call = null;
        var launcher = Launcher(isMacOS: true, handoff: Capture);

        var result = await launcher.LaunchDesktopAsync(
            CodexAddress(),
            CancellationToken.None);

        Assert.True(result.Launched);
        Assert.NotNull(call);
        Assert.Equal("/usr/bin/open", call.Executable);
        Assert.Equal(["codex://threads/019f-thread"], call.Arguments);
        Assert.Equal(SessionLaunchStatus.ApplicationMissing, call.FailureStatus);
        Assert.Equal("DESKTOP_APP_UNAVAILABLE", call.FailureCode);

        Task<SessionLaunchResult> Capture(
            string executable,
            IReadOnlyList<string> arguments,
            SessionLaunchStatus failureStatus,
            string failureCode,
            CancellationToken _)
        {
            call = new HandoffCall(executable, arguments, failureStatus, failureCode);
            return Task.FromResult(new SessionLaunchResult(SessionLaunchStatus.Launched));
        }
    }

    [Theory]
    [InlineData("unknown", "codex://threads/019f-thread")]
    [InlineData("codex", "https://example.invalid/thread")]
    public async Task Desktop_launch_rejects_non_adapter_addresses(
        string vendor,
        string uri)
    {
        var launcher = Launcher(isMacOS: true);
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
        var launcher = Launcher(isMacOS: true);
        var address = CodexAddress() with { Uri = null };

        var error = await Assert.ThrowsAsync<TrackerException>(
            () => launcher.LaunchDesktopAsync(address, CancellationToken.None));

        Assert.Equal("SESSION_LAUNCH_NOT_ALLOWED", error.Code);
    }

    private static LocalAgentSessionLauncher Launcher(
        bool isMacOS,
        IExecutableResolver? resolver = null,
        Func<string, bool>? applicationAvailable = null,
        Func<
            string,
            IReadOnlyList<string>,
            SessionLaunchStatus,
            string,
            CancellationToken,
            Task<SessionLaunchResult>>? handoff = null) =>
        new(
            resolver ?? new FixedResolver("/usr/local/bin/codex"),
            new LocalAgentLaunchPlatform(
                isMacOS,
                applicationAvailable ?? (_ => true),
                handoff ?? ((_, _, _, _, _) =>
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
}
