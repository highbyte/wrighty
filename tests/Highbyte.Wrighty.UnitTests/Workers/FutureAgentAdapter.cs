using Highbyte.Wrighty.Models;
using Highbyte.Wrighty.Workers;

namespace Highbyte.Wrighty.UnitTests.Workers;

/// <summary>A fourth-agent fixture used to prove generic integration paths.</summary>
internal sealed class FutureAgentAdapter : IAgentAdapter
{
    private readonly CodexAgentAdapter inner = new();

    public string Agent => "future-agent";

    public string ExecutableName => "future-agent";

    public bool SupportsPreassignedHandle => inner.SupportsPreassignedHandle;

    public AgentPermissions DescribePermissions(AgentPermissionProfile profile) =>
        inner.DescribePermissions(profile) with { Agent = Agent };

    public AgentExecutionCapability DescribeExecutionCapability() =>
        inner.DescribeExecutionCapability() with { Agent = Agent };

    public AgentInvocation BuildStart(
        WorkItemDetail item,
        SessionHandle handle,
        Workspace workspace,
        AgentPermissionProfile permissions,
        string? promptAddendum = null,
        bool requiresUserConfirmation = false,
        ExecutionSelection? selection = null) =>
        inner.BuildStart(
            item,
            handle,
            workspace,
            permissions,
            promptAddendum,
            requiresUserConfirmation,
            selection) with
        {
            Executable = ExecutableName
        };

    public AgentInvocation BuildStartWithPrompt(
        SessionHandle handle,
        Workspace workspace,
        AgentPermissionProfile permissions,
        string prompt,
        ExecutionSelection? selection = null) =>
        inner.BuildStartWithPrompt(handle, workspace, permissions, prompt, selection) with
        {
            Executable = ExecutableName
        };

    public AgentInvocation BuildResumeWithPrompt(
        SessionHandle handle,
        Workspace workspace,
        AgentPermissionProfile permissions,
        string prompt) =>
        inner.BuildResumeWithPrompt(handle, workspace, permissions, prompt) with
        {
            Executable = ExecutableName
        };

    public AgentInvocation BuildResume(
        SessionHandle handle,
        Workspace workspace,
        string prompt,
        AgentPermissionProfile permissions) =>
        inner.BuildResume(handle, workspace, prompt, permissions) with
        {
            Executable = ExecutableName
        };

    public AgentInvocation BuildCheck(SessionHandle handle, Workspace workspace) =>
        inner.BuildCheck(handle, workspace) with { Executable = ExecutableName };

    public LocalAgentInvocation BuildInteractiveInvocation(
        SessionHandle handle,
        Workspace workspace,
        IReadOnlyDictionary<string, string>? environment = null) =>
        inner.BuildInteractiveInvocation(handle, workspace, environment) with
        {
            Executable = ExecutableName
        };

    public DesktopLaunchAddress BuildDesktopLaunch(SessionHandle handle) =>
        inner.BuildDesktopLaunch(handle) with { Vendor = Agent };

    public string BuildInteractiveCommand(
        SessionHandle handle,
        Workspace workspace,
        IReadOnlyDictionary<string, string>? environment = null) =>
        inner.BuildInteractiveCommand(handle, workspace, environment);

    public string? TryExtractSessionId(string outputLine) =>
        inner.TryExtractSessionId(outputLine);

    public Task<AgentRunResult> InterpretAsync(
        Stream stdout,
        int exitCode,
        CancellationToken cancellationToken) =>
        inner.InterpretAsync(stdout, exitCode, cancellationToken);
}
