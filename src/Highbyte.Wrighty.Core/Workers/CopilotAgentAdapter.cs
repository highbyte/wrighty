using System.Text.Json;
using Highbyte.Wrighty.Models;
using static Highbyte.Wrighty.Workers.AgentFlags;

namespace Highbyte.Wrighty.Workers;

public sealed class CopilotAgentAdapter(
    Func<DateTimeOffset>? clock = null,
    string? shareDirectory = null) :
    IAgentResumeAdapter,
    IAgentInteractiveAdapter,
    IAgentDesktopAdapter
{
    private readonly Func<DateTimeOffset> now = clock ?? (() => DateTimeOffset.UtcNow);

    // Copilot separates tool approval from path and URL access: `--allow-all-tools` (which its own
    // help documents as required for non-interactive mode) auto-approves tools while leaving file
    // path verification and URL confirmation in place, and `--allow-all` is the flag that drops all
    // three (`--allow-all-tools --allow-all-paths --allow-all-urls`). The previous unconditional
    // `--allow-all-tools` was therefore already the narrower posture, and becomes the `workspace`
    // profile unchanged. Verified on 2026-07-25 with GitHub Copilot CLI 1.0.75: under
    // `--allow-all-tools` the agent attempted a parent-directory write through both its file tool
    // and the shell, and the CLI denied both ("Permission denied and could not request permission
    // from user"); adding --allow-all-paths let the identical prompt succeed. Path verification
    // also covers shell commands, not just the file tools.
    public string Agent => "copilot";
    public string ExecutableName => "copilot";
    public bool SupportsPreassignedHandle => true;

    public string DecorateResumePrompt(string prompt) =>
        prompt.TrimStart().StartsWith("/wrighty", StringComparison.Ordinal)
            ? prompt
            : $"/wrighty {prompt}";

    public AgentPermissions DescribePermissions(AgentPermissionProfile profile) => profile switch
    {
        AgentPermissionProfile.Full =>
            new AgentPermissions(Agent, profile, AgentPermissionEnforcement.Unrestricted,
                ConfinesFileWrites: false, AllowsNetwork: true, PermissionArguments(profile),
                "copilot runs unrestricted: all tools, all paths, and all URLs are allowed."),
        AgentPermissionProfile.ReadOnly =>
            new AgentPermissions(Agent, profile, AgentPermissionEnforcement.Enforced,
                ConfinesFileWrites: true, AllowsNetwork: false, PermissionArguments(profile),
                "copilot denies shell commands, file writes, and URLs, disables built-in MCP " +
                "servers, and disallows the system temporary directory."),
        _ => new AgentPermissions(Agent, profile, AgentPermissionEnforcement.Enforced,
                ConfinesFileWrites: true, AllowsNetwork: true, PermissionArguments(profile),
                "copilot auto-approves tools while keeping its own path verification and URL " +
                "confirmation, so file access stays within the workspace and the system temporary " +
                "directory.")
    };

    /// <summary>
    /// Verified against GitHub Copilot CLI 1.0.78, whose <c>--effort</c> help enumerates its own
    /// choices, so the CLI rejects an unsupported level before a request is made. <c>--model</c>
    /// accepts <c>auto</c> to leave the choice to Copilot; concrete model availability is
    /// account-dependent and cannot be known here.
    ///
    /// The levels are copilot's own documented choices, which include <c>none</c> and
    /// <c>minimal</c> but not <c>ultra</c>. As elsewhere this is a gate, not a guarantee: whether a
    /// level works also depends on the model the account resolves to.
    /// </summary>
    public AgentExecutionCapability DescribeExecutionCapability() => new(
        Agent,
        SupportsModel: true,
        SupportedEfforts: new HashSet<ExecutionEffort>
        {
            ExecutionEffort.None,
            ExecutionEffort.Minimal,
            ExecutionEffort.Low,
            ExecutionEffort.Medium,
            ExecutionEffort.High,
            ExecutionEffort.XHigh,
            ExecutionEffort.Max
        });

    public AgentInvocation BuildStart(WorkItemDetail item, SessionHandle handle, Workspace workspace,
        AgentPermissionProfile permissions, string? promptAddendum = null,
        bool requiresUserConfirmation = false, ExecutionSelection? selection = null) =>
        Invocation(workspace, ["-p", WorkerPrompt.Append(WorkerPrompt.For(item.Id, requiresUserConfirmation), promptAddendum),
            "-n", handle.Value, .. PermissionArguments(permissions), .. ShareArguments(handle),
            .. ExecutionSelectionArguments.ForFlags(selection),
            OutputFormatFlag, "json", "--no-remote", "-C", workspace.Path]);

    // No `-p` at all: copilot reads a piped prompt when the flag is absent. Phase 0 recorded a
    // stdin failure for this vendor having probed `copilot -p` with no value, which the CLI rejects
    // as a missing argument rather than as a refusal to read standard input. Re-measured on
    // 2026-07-27 with GitHub Copilot CLI 1.0.75: a piped prompt is read in full, and a ~100,000
    // character context with an identifier in its closing lines came back answered from the tail.
    public AgentInvocation BuildStartWithPrompt(SessionHandle handle, Workspace workspace,
        AgentPermissionProfile permissions, string prompt, ExecutionSelection? selection = null) =>
        Invocation(workspace, ["-n", handle.Value, .. PermissionArguments(permissions),
            .. ShareArguments(handle),
            .. ExecutionSelectionArguments.ForFlags(selection),
            OutputFormatFlag, "json", "--no-remote", "-C", workspace.Path]) with
        {
            StandardInput = prompt
        };

    public AgentInvocation BuildResumeWithPrompt(SessionHandle handle, Workspace workspace,
        AgentPermissionProfile permissions, string prompt) =>
        Invocation(workspace, [$"--resume={handle.Value}", .. PermissionArguments(permissions),
            .. ShareArguments(handle),
            OutputFormatFlag, "json", "--no-remote", "-C", workspace.Path]) with
        {
            StandardInput = prompt
        };

    public AgentInvocation BuildResume(SessionHandle handle, Workspace workspace, string prompt,
        AgentPermissionProfile permissions) =>
        Invocation(workspace, ["-p", DecorateResumePrompt(prompt), $"--resume={handle.Value}",
            .. PermissionArguments(permissions), .. ShareArguments(handle),
            OutputFormatFlag, "json", "--no-remote", "-C", workspace.Path]);

    /// <summary>
    /// Requests copilot's own Markdown session export into the machine-local cache (plan 026
    /// part e): unlike claude and codex, copilot keeps its transcript in a private database, and
    /// `--share` is its supported export surface — so the export is requested from the beginning
    /// of every worker-owned run, and a later cross-agent handoff reads what the vendor wrote.
    /// Not applied to the read-only check probe or to interactive launches, which are not
    /// handoff sources.
    /// </summary>
    private string[] ShareArguments(SessionHandle handle)
    {
        if (shareDirectory is null)
            return [];
        Directory.CreateDirectory(shareDirectory);
        return [$"--share={Path.Combine(shareDirectory, handle.Value + ".md")}"];
    }

    public AgentInvocation BuildCheck(SessionHandle handle, Workspace workspace) =>
        Invocation(workspace, ["-p", "Reply exactly OK.", "-n", handle.Value,
            OutputFormatFlag, "json", "--no-remote", "-C", workspace.Path]);

    public string BuildInteractiveCommand(
        SessionHandle handle,
        Workspace workspace,
        IReadOnlyDictionary<string, string>? environment = null) =>
        InteractiveAgentCommand.Build(
            workspace,
            $"copilot --resume={InteractiveAgentCommand.Quote(handle.Value)}",
            environment);

    public LocalAgentInvocation BuildInteractiveInvocation(
        SessionHandle handle,
        Workspace workspace,
        IReadOnlyDictionary<string, string>? environment = null) =>
        new("copilot", [$"--resume={handle.Value}"], workspace.Path,
            environment ?? new Dictionary<string, string>());

    public DesktopLaunchAddress BuildDesktopLaunch(SessionHandle handle) =>
        DesktopLaunchAddresses.Build(
            Agent, "ghapp", "sessions", handle.Value, "GitHub Copilot") with
        {
            Prerequisite =
                "In GitHub Copilot Desktop, open Settings → Sessions → " +
                "Show Copilot CLI Session and change Off to a retention period that includes " +
                "this session. Wrighty cannot detect this setting.",
            CompatibilityWarning =
                "Some GitHub Copilot Desktop versions may open Home instead of the recorded CLI " +
                "session. No session data is lost; use Open Copilot CLI if this occurs."
        };

    public async Task<AgentRunResult> InterpretAsync(Stream stdout, int exitCode, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(stdout, leaveOpen: true);
        JsonElement? terminal = null;
        JsonElement? terminalError = null;
        string? assistantMessage = null;
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
            InterpretLine(line, ref terminal, ref terminalError, ref assistantMessage);
        if (terminal is not { } result)
        {
            var missingFailure = MissingResultFailure(terminalError, exitCode);
            return new AgentRunResult(
                AgentOutcome.Rejected,
                null,
                "Copilot returned no result event.",
                exitCode,
                missingFailure);
        }
        var resultExit = result.TryGetProperty("exitCode", out var resultExitValue)
            ? resultExitValue.GetInt32() : exitCode;
        var succeeded = resultExit == 0 && exitCode == 0;
        var resultMessage = ReadCopilotResultMessage(result);
        var failure = succeeded
            ? null
            : AgentFailureClassifier.FromEvent(
                Agent, terminalError ?? result, resultExit, now());
        var finalMessage = FinalMessage(resultMessage, assistantMessage, failure, succeeded);
        return new AgentRunResult(succeeded ? AgentOutcome.Succeeded : AgentOutcome.Failed,
            result.TryGetProperty("sessionId", out var session) ? session.GetString() : null,
            finalMessage, resultExit, failure);
    }

    private static void InterpretLine(
        string line,
        ref JsonElement? terminal,
        ref JsonElement? terminalError,
        ref string? assistantMessage)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            if (!document.RootElement.TryGetProperty("type", out var type))
                return;
            if (type.GetString() == "result")
                terminal = document.RootElement.Clone();
            else if (type.GetString() is "error" or "session.error")
                terminalError = document.RootElement.Clone();
            else if (type.GetString() == "assistant.message" &&
                     TryReadCopilotAssistantMessage(document.RootElement) is { } content)
                assistantMessage = content;
        }
        catch (JsonException)
        {
            // Copilot may print a non-JSON diagnostic before terminating. The missing result event
            // or nonzero exit code still makes the run fail safely.
        }
    }

    private AgentFailure MissingResultFailure(JsonElement? terminalError, int exitCode) =>
        terminalError is { } error
            ? AgentFailureClassifier.FromEvent(Agent, error, exitCode, now())
            : AgentFailureClassifier.Unknown(
                Agent,
                "Copilot returned no result event.",
                exitCode);

    private static string FinalMessage(
        string? resultMessage,
        string? assistantMessage,
        AgentFailure? failure,
        bool succeeded)
    {
        if (resultMessage is not null)
            return resultMessage;
        if (!succeeded && failure?.SanitizedMessage is { } failureMessage)
            return failureMessage;
        if (assistantMessage is not null)
            return assistantMessage;
        return succeeded
            ? "Copilot completed without a final text response."
            : "Copilot failed without a final text response.";
    }

    // Narrative rather than failure sanitizing: this is what the agent said about its run, and it
    // was asked to end with a fenced report block. Collapsing its newlines destroys that block.
    private static string? ReadCopilotResultMessage(JsonElement result)
    {
        if (result.TryGetProperty("message", out var message) &&
            message.ValueKind == JsonValueKind.String)
            return AgentFailureClassifier.SanitizeNarrative(message.GetString());
        return result.TryGetProperty("result", out var resultMessage) &&
               resultMessage.ValueKind == JsonValueKind.String
            ? AgentFailureClassifier.SanitizeNarrative(resultMessage.GetString())
            : null;
    }

    private static string? TryReadCopilotAssistantMessage(JsonElement root)
    {
        if (!root.TryGetProperty("data", out var data) ||
            data.ValueKind != JsonValueKind.Object ||
            !data.TryGetProperty("content", out var content) ||
            content.ValueKind != JsonValueKind.String)
            return null;
        return AgentFailureClassifier.SanitizeNarrative(content.GetString());
    }

    private static IReadOnlyList<string> PermissionArguments(AgentPermissionProfile profile) =>
        profile switch
        {
            AgentPermissionProfile.Full => ["--allow-all"],
            AgentPermissionProfile.ReadOnly =>
                ["--allow-all-tools", "--deny-tool=write", "--deny-tool=shell",
                    "--deny-tool=url", "--disable-builtin-mcps", "--disallow-temp-dir"],
            _ => ["--allow-all-tools"]
        };

    private static AgentInvocation Invocation(Workspace workspace, IReadOnlyList<string> arguments) =>
        new("copilot", arguments, workspace.Path, new Dictionary<string, string>());
}
