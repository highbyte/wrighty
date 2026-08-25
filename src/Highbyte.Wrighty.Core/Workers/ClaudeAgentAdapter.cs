using System.Text.Json;
using Highbyte.Wrighty.Models;
using static Highbyte.Wrighty.Workers.AgentFlags;

namespace Highbyte.Wrighty.Workers;

public sealed class ClaudeAgentAdapter(Func<DateTimeOffset>? clock = null) :
    IAgentAdapter,
    IAgentResumeAdapter,
    IAgentInteractiveAdapter,
    IAgentDesktopAdapter
{
    private readonly Func<DateTimeOffset> now = clock ?? (() => DateTimeOffset.UtcNow);

    // Claude has no verified headless mode that confines file writes to the workspace. Verified on
    // 2026-07-25 with Claude Code 2.1.219: under `--permission-mode acceptEdits` with a tool
    // allow-list, a `-p` run completes without a permission stall, reaches the network, and writes
    // inside the workspace — but a write to the parent directory (through Bash and through the
    // Write tool) also succeeds, and enabling the built-in sandbox through `--settings`
    // (`sandbox.enabled`, accepted with `enforceStartup`) did not confine those writes either.
    // The `workspace` profile therefore delivers tool-level narrowing only — tools outside the
    // allow-list are denied instead of auto-approved — and reports itself as partial.
    private static readonly IReadOnlyList<string> WorkspaceTools =
        ["Bash", "Edit", "Write", "Read", "Glob", "Grep", "NotebookEdit", "TodoWrite", "Task"];
    private static readonly IReadOnlyList<string> ReadOnlyTools = ["Read", "Glob", "Grep"];

    public string Agent => "claude";
    public string ExecutableName => "claude";
    public bool SupportsPreassignedHandle => true;

    public SessionHandle CreateSessionHandle(WorkItemId id, string claimGeneration) =>
        SessionHandles.ForClaude(id, claimGeneration);

    public bool MatchesEmittedSessionId(SessionHandle handle, string sessionId) =>
        string.Equals(sessionId, handle.Value, StringComparison.OrdinalIgnoreCase);

    public string DecorateResumePrompt(string prompt) => WorkerPrompt.ForClaudeResume(prompt);

    public AgentPermissions DescribePermissions(AgentPermissionProfile profile) => profile switch
    {
        AgentPermissionProfile.Full =>
            new AgentPermissions(Agent, profile, AgentPermissionEnforcement.Unrestricted,
                ConfinesFileWrites: false, AllowsNetwork: true, PermissionArguments(profile),
                "claude runs unrestricted: all permission checks are bypassed."),
        AgentPermissionProfile.ReadOnly =>
            new AgentPermissions(Agent, profile, AgentPermissionEnforcement.Enforced,
                ConfinesFileWrites: true, AllowsNetwork: false, PermissionArguments(profile),
                "claude exposes only Read, Glob, and Grep; command execution and mutating tools " +
                "are unavailable."),
        _ => new AgentPermissions(Agent, profile, AgentPermissionEnforcement.Partial,
                ConfinesFileWrites: false, AllowsNetwork: true, PermissionArguments(profile),
                "claude auto-approves only allow-listed tools " +
                $"({string.Join(", ", WorkspaceTools)}) and denies the rest, but has no verified " +
                "headless mode that confines file writes to the workspace.")
    };

    /// <summary>
    /// Verified against Claude Code 2.1.222: <c>--model</c> takes a rolling alias or a full model
    /// name, and <c>--effort</c> documents five levels. Claude is the outlier that does not accept
    /// <c>none</c> or <c>minimal</c>, so a mapping carrying either is rejected here rather than
    /// quietly rounded up to <c>low</c>.
    /// </summary>
    public AgentExecutionCapability DescribeExecutionCapability() => new(
        Agent,
        SupportsModel: true,
        SupportedEfforts: new HashSet<ExecutionEffort>
        {
            ExecutionEffort.Low,
            ExecutionEffort.Medium,
            ExecutionEffort.High,
            ExecutionEffort.XHigh,
            ExecutionEffort.Max
        });

    public AgentInvocation BuildStart(WorkItemDetail item, SessionHandle handle, Workspace workspace,
        AgentPermissionProfile permissions, string? promptAddendum = null,
        bool requiresUserConfirmation = false, ExecutionSelection? selection = null) =>
        Invocation(workspace, ["-p", WorkerPrompt.Append(WorkerPrompt.ForClaude(item.Id, requiresUserConfirmation), promptAddendum),
            "--session-id", handle.Value,
            OutputFormatFlag, "json", .. PermissionArguments(permissions),
            .. ExecutionSelectionArguments.ForFlags(selection)]);

    // `-p` with no value is print mode reading standard input. Measured working in phase 0.
    public AgentInvocation BuildStartWithPrompt(SessionHandle handle, Workspace workspace,
        AgentPermissionProfile permissions, string prompt, ExecutionSelection? selection = null) =>
        Invocation(workspace, ["-p", "--session-id", handle.Value,
            OutputFormatFlag, "json", .. PermissionArguments(permissions),
            .. ExecutionSelectionArguments.ForFlags(selection)]) with
        {
            StandardInput = prompt
        };

    public AgentInvocation BuildResumeWithPrompt(SessionHandle handle, Workspace workspace,
        AgentPermissionProfile permissions, string prompt) =>
        Invocation(workspace, ["-p", "--resume", handle.Value,
            OutputFormatFlag, "json", .. PermissionArguments(permissions)]) with
        {
            StandardInput = prompt
        };

    public AgentInvocation BuildResume(SessionHandle handle, Workspace workspace, string prompt,
        AgentPermissionProfile permissions) =>
        Invocation(workspace,
            ["-p", DecorateResumePrompt(prompt), "--resume", handle.Value,
                OutputFormatFlag, "json", .. PermissionArguments(permissions)]);

    // The probe only has to prove the vendor answers and honors the preassigned handle, so it runs
    // with every tool disabled instead of bypassing permissions. Verified on 2026-07-25 with Claude
    // Code 2.1.219: `--tools ""` still returns "OK" and echoes the requested session id.
    public AgentInvocation BuildCheck(SessionHandle handle, Workspace workspace) =>
        Invocation(workspace, ["-p", "Reply exactly OK.", "--session-id", handle.Value,
            OutputFormatFlag, "json", "--tools", ""]);

    public string BuildInteractiveCommand(
        SessionHandle handle,
        Workspace workspace,
        IReadOnlyDictionary<string, string>? environment = null) =>
        InteractiveAgentCommand.Build(
            workspace,
            $"claude --resume {InteractiveAgentCommand.Quote(handle.Value)}",
            environment);

    public LocalAgentInvocation BuildInteractiveInvocation(
        SessionHandle handle,
        Workspace workspace,
        IReadOnlyDictionary<string, string>? environment = null) =>
        new("claude", ["--resume", handle.Value], workspace.Path,
            environment ?? new Dictionary<string, string>());

    public DesktopLaunchAddress BuildDesktopLaunch(SessionHandle handle)
    {
        if (!Guid.TryParse(handle.Value, out var sessionId))
        {
            return new DesktopLaunchAddress(
                Agent,
                null,
                DesktopSessionSupport.Unavailable,
                "Claude Desktop resume requires a UUID session ID.",
                "Claude");
        }
        return new DesktopLaunchAddress(
            Agent,
            new Uri($"claude://resume?session={Uri.EscapeDataString(sessionId.ToString())}"),
            DesktopSessionSupport.Experimental,
            "Opening this recorded session in Claude Desktop is experimental and is not enabled.",
            "Claude",
            // On by default, so this warning is what tells the operator the route is unproven —
            // the opt-in used to. It rides the launch surfaces, which show it where the choice is
            // made rather than in a config file nobody reads at that moment.
            CompatibilityWarning:
                "This route is experimental: it uses an undocumented Claude resume link that has " +
                "passed qualification on one release. If Claude Desktop opens without the " +
                "session, use the terminal instead.");
    }

    public async Task<AgentRunResult> InterpretAsync(Stream stdout, int exitCode, CancellationToken cancellationToken)
    {
        try
        {
            using var document = await JsonDocument.ParseAsync(stdout, cancellationToken: cancellationToken);
            var root = document.RootElement;
            var failed = root.TryGetProperty("is_error", out var error) && error.GetBoolean();
            var subtype = root.TryGetProperty("subtype", out var subtypeValue) ? subtypeValue.GetString() : null;
            var success = !failed && string.Equals(subtype, "success", StringComparison.OrdinalIgnoreCase);
            var failure = success && exitCode == 0
                ? null
                : AgentFailureClassifier.FromEvent(Agent, root, exitCode, now());
            return new AgentRunResult(success && exitCode == 0 ? AgentOutcome.Succeeded : AgentOutcome.Failed,
                root.TryGetProperty("session_id", out var session) ? session.GetString() : null,
                root.TryGetProperty("result", out var result) ? result.GetString() : null, exitCode,
                failure);
        }
        catch (JsonException)
        {
            const string message = "Claude returned invalid JSON.";
            return new AgentRunResult(
                AgentOutcome.Rejected,
                null,
                message,
                exitCode,
                AgentFailureClassifier.Unknown(Agent, message, exitCode));
        }
    }

    private static IReadOnlyList<string> PermissionArguments(AgentPermissionProfile profile) =>
        profile switch
        {
            AgentPermissionProfile.Full => ["--dangerously-skip-permissions"],
            AgentPermissionProfile.ReadOnly =>
                ["--permission-mode", "dontAsk", "--tools", string.Join(" ", ReadOnlyTools)],
            _ => ["--permission-mode", "acceptEdits", "--allowedTools",
                string.Join(" ", WorkspaceTools)]
        };

    private static AgentInvocation Invocation(Workspace workspace, IReadOnlyList<string> arguments) =>
        new("claude", arguments, workspace.Path, new Dictionary<string, string>());
}
