using System.Text;
using System.Text.Json;
using Highbyte.Wrighty.Models;

namespace Highbyte.Wrighty.Workers;

/// <summary>OpenCode's headless, resume, and interactive CLI protocol.</summary>
public sealed class OpenCodeAgentAdapter(Func<DateTimeOffset>? clock = null) :
    IAgentResumeAdapter,
    IAgentInteractiveAdapter
{
    private const string AutoFlag = "--auto";
    private const string ConfigEnvironmentVariable = "OPENCODE_CONFIG_CONTENT";
    private const string ReadOnlyConfig =
        "{\"permission\":{\"*\":\"deny\",\"read\":\"allow\",\"glob\":\"allow\"," +
        "\"grep\":\"allow\",\"list\":\"allow\",\"skill\":\"allow\"}}";
    private const string WorkspaceConfig =
        "{\"permission\":{\"*\":\"allow\",\"external_directory\":\"deny\"," +
        "\"question\":\"deny\"}}";
    private const string FullConfig =
        "{\"permission\":{\"*\":\"allow\",\"question\":\"deny\"}}";

    private readonly Func<DateTimeOffset> now = clock ?? (() => DateTimeOffset.UtcNow);

    public string Agent => "opencode";
    public string ExecutableName => "opencode";
    public bool SupportsPreassignedHandle => false;

    public string DecorateResumePrompt(string prompt) =>
        prompt.Contains("wrighty skill", StringComparison.OrdinalIgnoreCase)
            ? prompt
            : $"Use the wrighty skill to {LowercaseFirst(prompt)}";

    public AgentPermissions DescribePermissions(AgentPermissionProfile profile) => profile switch
    {
        AgentPermissionProfile.Full =>
            new AgentPermissions(
                Agent,
                profile,
                AgentPermissionEnforcement.Unrestricted,
                ConfinesFileWrites: false,
                AllowsNetwork: true,
                [AutoFlag],
                "opencode auto-approves every non-interactive action except questions."),
        AgentPermissionProfile.ReadOnly =>
            new AgentPermissions(
                Agent,
                profile,
                AgentPermissionEnforcement.Enforced,
                ConfinesFileWrites: true,
                AllowsNetwork: false,
                [AutoFlag],
                "opencode permits repository read, search, listing, and skill tools while denying " +
                "commands, mutation, network tools, and every other action."),
        _ => new AgentPermissions(
                Agent,
                profile,
                AgentPermissionEnforcement.Partial,
                ConfinesFileWrites: false,
                AllowsNetwork: true,
                [AutoFlag],
                "opencode denies native tool access outside the workspace, but shell commands run " +
                "with host filesystem authority and can write beyond it.")
    };

    /// <summary>
    /// OpenCode accepts a provider-qualified model through <c>--model</c> and a model-specific
    /// variant through <c>--variant</c>. Discovery supplies the precise variants for each model;
    /// this union is only the early gate used when discovery is unavailable.
    /// </summary>
    public AgentExecutionCapability DescribeExecutionCapability() => new(
        Agent,
        SupportsModel: true,
        SupportedEfforts: Enum.GetValues<ExecutionEffort>().ToHashSet());

    public AgentInvocation BuildStart(
        WorkItemDetail item,
        SessionHandle handle,
        Workspace workspace,
        AgentPermissionProfile permissions,
        string? promptAddendum = null,
        bool requiresUserConfirmation = false,
        ExecutionSelection? selection = null) =>
        Invocation(
            workspace,
            permissions,
            WorkerPrompt.Append(
                WorkerPrompt.For(item.Id, requiresUserConfirmation),
                promptAddendum),
            selection);

    public AgentInvocation BuildStartWithPrompt(
        SessionHandle handle,
        Workspace workspace,
        AgentPermissionProfile permissions,
        string prompt,
        ExecutionSelection? selection = null) =>
        Invocation(workspace, permissions, prompt, selection);

    public AgentInvocation BuildResumeWithPrompt(
        SessionHandle handle,
        Workspace workspace,
        AgentPermissionProfile permissions,
        string prompt) =>
        Invocation(workspace, permissions, prompt, sessionId: handle.Value);

    public AgentInvocation BuildResume(
        SessionHandle handle,
        Workspace workspace,
        string prompt,
        AgentPermissionProfile permissions) =>
        Invocation(
            workspace,
            permissions,
            DecorateResumePrompt(prompt),
            sessionId: handle.Value);

    public AgentInvocation BuildCheck(SessionHandle handle, Workspace workspace) =>
        Invocation(
            workspace,
            AgentPermissionProfile.ReadOnly,
            "Reply exactly OK.");

    public string BuildInteractiveCommand(
        SessionHandle handle,
        Workspace workspace,
        IReadOnlyDictionary<string, string>? environment = null) =>
        InteractiveAgentCommand.Build(
            workspace,
            $"opencode --session {InteractiveAgentCommand.Quote(handle.Value)}",
            environment);

    public LocalAgentInvocation BuildInteractiveInvocation(
        SessionHandle handle,
        Workspace workspace,
        IReadOnlyDictionary<string, string>? environment = null) =>
        new(
            ExecutableName,
            ["--session", handle.Value],
            workspace.Path,
            environment ?? new Dictionary<string, string>());

    public string? TryExtractSessionId(string outputLine)
    {
        try
        {
            using var document = JsonDocument.Parse(outputLine);
            return Text(document.RootElement, "sessionID");
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public async Task<AgentRunResult> InterpretAsync(
        Stream stdout,
        int exitCode,
        CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(stdout, leaveOpen: true);
        var final = new StringBuilder();
        string? sessionId = null;
        string? finalReason = null;
        JsonElement? terminalError = null;

        while (await reader.ReadLineAsync(cancellationToken) is { } line)
            InterpretLine(line, final, ref sessionId, ref finalReason, ref terminalError);

        var message = final.Length == 0 ? null : final.ToString();
        if (sessionId is null)
        {
            const string missing = "OpenCode output ended before a session ID was emitted.";
            return new AgentRunResult(
                AgentOutcome.Rejected,
                null,
                message ?? missing,
                exitCode,
                AgentFailureClassifier.Unknown(Agent, message ?? missing, exitCode));
        }

        var succeeded = exitCode == 0 && terminalError is null && finalReason == "stop";
        var failure = FailureFor(succeeded, terminalError, message ?? finalReason, exitCode);
        return new AgentRunResult(
            succeeded ? AgentOutcome.Succeeded : AgentOutcome.Failed,
            sessionId,
            message,
            exitCode,
            failure);
    }

    private static void InterpretLine(
        string line,
        StringBuilder final,
        ref string? sessionId,
        ref string? finalReason,
        ref JsonElement? terminalError)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            sessionId ??= Text(root, "sessionID");
            var type = Text(root, "type");
            if (type == "text" &&
                root.TryGetProperty("part", out var textPart) &&
                Text(textPart, "text") is { } text)
            {
                if (final.Length > 0)
                    final.AppendLine();
                final.Append(text);
            }
            else if (type == "step_finish" &&
                     root.TryGetProperty("part", out var finishPart))
            {
                finalReason = Text(finishPart, "reason");
            }
            else if (type == "error")
            {
                terminalError = root.Clone();
            }
        }
        catch (JsonException)
        {
            // OpenCode may print a non-JSON diagnostic before terminating. The exit code and
            // absence of a terminal step still make the run fail safely.
        }
    }

    private AgentFailure? FailureFor(
        bool succeeded,
        JsonElement? terminalError,
        string? message,
        int exitCode)
    {
        if (succeeded)
            return null;
        return terminalError is { } error
            ? AgentFailureClassifier.FromEvent(Agent, error, exitCode, now())
            : AgentFailureClassifier.Unknown(Agent, message, exitCode);
    }

    private static AgentInvocation Invocation(
        Workspace workspace,
        AgentPermissionProfile permissions,
        string prompt,
        ExecutionSelection? selection = null,
        string? sessionId = null) =>
        new(
            "opencode",
            [
                "run",
                "--pure",
                "--format", "json",
                AutoFlag,
                "--agent", "build",
                "--dir", workspace.Path,
                .. ExecutionSelectionArguments.ForOpenCode(selection),
                .. sessionId is null ? [] : new[] { "--session", sessionId }
            ],
            workspace.Path,
            new Dictionary<string, string>
            {
                [ConfigEnvironmentVariable] = PermissionConfig(permissions)
            },
            CloseStandardInput: true,
            StandardInput: prompt);

    private static string PermissionConfig(AgentPermissionProfile profile) => profile switch
    {
        AgentPermissionProfile.Full => FullConfig,
        AgentPermissionProfile.ReadOnly => ReadOnlyConfig,
        _ => WorkspaceConfig
    };

    private static string LowercaseFirst(string value) =>
        value.Length == 0 ? value : char.ToLowerInvariant(value[0]) + value[1..];

    private static string? Text(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.String &&
        value.GetString() is { Length: > 0 } text
            ? text
            : null;
}
