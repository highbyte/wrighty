using Highbyte.Wrighty.Models;
using Highbyte.Wrighty.Workers;

namespace Highbyte.Wrighty.UnitTests.Workers;

/// <summary>Proves that fresh worker execution does not require optional session surfaces.</summary>
internal sealed class FreshOnlyAgentAdapter(string agent = "fresh-agent") : IAgentAdapter
{
    private readonly CodexAgentAdapter inner = new();

    public string Agent => agent;

    public string ExecutableName => agent;

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

    public AgentInvocation BuildCheck(SessionHandle handle, Workspace workspace) =>
        inner.BuildCheck(handle, workspace) with { Executable = ExecutableName };

    public string? TryExtractSessionId(string outputLine) =>
        inner.TryExtractSessionId(outputLine);

    public Task<AgentRunResult> InterpretAsync(
        Stream stdout,
        int exitCode,
        CancellationToken cancellationToken) =>
        inner.InterpretAsync(stdout, exitCode, cancellationToken);
}
