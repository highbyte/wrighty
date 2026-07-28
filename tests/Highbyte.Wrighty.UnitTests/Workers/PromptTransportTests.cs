using Highbyte.Wrighty.Workers;

namespace Highbyte.Wrighty.UnitTests.Workers;

/// <summary>
/// How a supplied prompt reaches a vendor. The approved context is work-item content, and the
/// command line is the one place it must never be: argument lists are readable by every process on
/// the machine and Wrighty prints them in its own worker events.
/// </summary>
public class PromptTransportTests
{
    private const string Prompt = "APPROVED-CONTEXT-SENTINEL\nsecond line";

    private static readonly Workspace Workspace = new("/tmp/ws", IsWorktree: true);
    private static readonly SessionHandle Handle = new("session-1");

    public static TheoryData<IAgentAdapter> Adapters => new()
    {
        new ClaudeAgentAdapter(),
        new CodexAgentAdapter(),
        new CopilotAgentAdapter()
    };

    [Theory]
    [MemberData(nameof(Adapters))]
    public void ASuppliedPromptTravelsOnStandardInputAndNeverInTheArguments(IAgentAdapter adapter)
    {
        var invocation = adapter.BuildStartWithPrompt(
            Handle, Workspace, AgentPermissionProfile.Workspace, Prompt);

        Assert.Equal(Prompt, invocation.StandardInput);
        Assert.DoesNotContain(
            invocation.Arguments,
            argument => argument.Contains("APPROVED-CONTEXT-SENTINEL", StringComparison.Ordinal));
    }

    [Theory]
    [MemberData(nameof(Adapters))]
    public void TheBootstrapLaunchStillCarriesItsPromptOnTheCommandLine(IAgentAdapter adapter)
    {
        // Unchanged on purpose: that prompt is a short instruction to go and read the item, it
        // carries no approved content, and moving it would alter a path this work does not touch.
        var invocation = adapter.BuildStart(
            new Highbyte.Wrighty.Models.WorkItemDetail(
                new Highbyte.Wrighty.Models.WorkItemId("local:1"), "t", "b", null, "Todo", "P1"),
            Handle, Workspace, AgentPermissionProfile.Workspace);

        Assert.Null(invocation.StandardInput);
        Assert.Contains(invocation.Arguments, a => a.Contains("local:1", StringComparison.Ordinal));
    }

    [Fact]
    public void ClaudeUsesPrintModeWithNoValueSoTheFlagDoesNotSwallowTheNextArgument()
    {
        var invocation = new ClaudeAgentAdapter().BuildStartWithPrompt(
            Handle, Workspace, AgentPermissionProfile.Workspace, Prompt);

        var p = invocation.Arguments.ToList().IndexOf("-p");
        Assert.True(p >= 0);
        // `-p` immediately followed by another flag: were a value ever placed here, the prompt would
        // be back on the command line and this is where that would first show.
        Assert.StartsWith("--", invocation.Arguments[p + 1], StringComparison.Ordinal);
        Assert.Contains("--session-id", invocation.Arguments);
    }

    [Fact]
    public void CodexUsesItsReadFromStdinPromptPlaceholder()
    {
        var invocation = new CodexAgentAdapter().BuildStartWithPrompt(
            Handle, Workspace, AgentPermissionProfile.Workspace, Prompt);

        Assert.Equal("-", invocation.Arguments[^1]);
    }

    [Fact]
    public void CopilotOmitsThePromptFlagEntirely()
    {
        // Phase 0 recorded copilot as having no stdin path, having probed `copilot -p` with no
        // value — which the CLI rejects as a missing argument, not as a refusal to read stdin.
        // Passing the flag here would reintroduce that failure.
        var invocation = new CopilotAgentAdapter().BuildStartWithPrompt(
            Handle, Workspace, AgentPermissionProfile.Workspace, Prompt);

        Assert.DoesNotContain("-p", invocation.Arguments);
        Assert.DoesNotContain("--prompt", invocation.Arguments);
        Assert.Contains("--allow-all-tools", invocation.Arguments);
    }

    [Theory]
    [MemberData(nameof(Adapters))]
    public void TheSuppliedLaunchKeepsTheSameSessionAndPermissionArgumentsAsABootstrapLaunch(
        IAgentAdapter adapter)
    {
        // Only the prompt's route changes. A permission or output-format argument quietly lost here
        // would change how much privilege an unattended agent receives, or make its output
        // unparseable, without any test of this feature noticing.
        var bootstrap = adapter.BuildStart(
            new Highbyte.Wrighty.Models.WorkItemDetail(
                new Highbyte.Wrighty.Models.WorkItemId("local:1"), "t", "b", null, "Todo", "P1"),
            Handle, Workspace, AgentPermissionProfile.Workspace);
        var supplied = adapter.BuildStartWithPrompt(
            Handle, Workspace, AgentPermissionProfile.Workspace, Prompt);

        foreach (var argument in bootstrap.Arguments.Where(a =>
                     a.StartsWith("--allow", StringComparison.Ordinal) ||
                     a is "--output-format" or "json" or "--sandbox" or "--skip-git-repo-check"))
            Assert.Contains(argument, supplied.Arguments);

        Assert.Equal(bootstrap.Executable, supplied.Executable);
        Assert.Equal(bootstrap.WorkingDirectory, supplied.WorkingDirectory);
    }
}
