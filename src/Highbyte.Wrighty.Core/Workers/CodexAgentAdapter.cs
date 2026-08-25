using System.Text.Json;
using Highbyte.Wrighty.Models;
using static Highbyte.Wrighty.Workers.AgentFlags;

namespace Highbyte.Wrighty.Workers;

public sealed class CodexAgentAdapter(Func<DateTimeOffset>? clock = null) :
    IAgentResumeAdapter,
    IAgentInteractiveAdapter,
    IAgentDesktopAdapter
{
    private const string SandboxFlag = "--sandbox";
    private readonly Func<DateTimeOffset> now = clock ?? (() => DateTimeOffset.UtcNow);

    public string Agent => "codex";
    public string ExecutableName => "codex";
    public bool SupportsPreassignedHandle => false;

    public string DecorateResumePrompt(string prompt) =>
        prompt.TrimStart().StartsWith("$wrighty", StringComparison.Ordinal)
            ? prompt
            : $"$wrighty {prompt}";

    // Codex is the one vendor that expresses the `workspace` profile exactly: writes confined to
    // the workspace while the network stays reachable for the agent's own GitHub-backend commands
    // (the prompt has it run `wrighty get`, and the skill runs `wrighty init --check`). This
    // replaces the interim `danger-full-access` parity fix, which was only ever a stopgap for the
    // plain `workspace-write` sandbox disabling network by default. Verified on 2026-07-25 with
    // codex-cli 0.145.0: network reached, a workspace write succeeded, a parent-directory write was
    // denied.
    private static readonly IReadOnlyList<string> WorkspaceSandbox =
        [SandboxFlag, "workspace-write", "-c", "sandbox_workspace_write.network_access=true"];

    public AgentPermissions DescribePermissions(AgentPermissionProfile profile) => profile switch
    {
        AgentPermissionProfile.Full =>
            new AgentPermissions(Agent, profile, AgentPermissionEnforcement.Unrestricted,
                ConfinesFileWrites: false, AllowsNetwork: true, PermissionArguments(profile),
                "codex runs unrestricted: the sandbox is disabled."),
        AgentPermissionProfile.ReadOnly =>
            new AgentPermissions(Agent, profile, AgentPermissionEnforcement.Enforced,
                ConfinesFileWrites: true, AllowsNetwork: false, PermissionArguments(profile),
                "codex runs in its read-only sandbox with network access disabled."),
        _ => new AgentPermissions(Agent, profile, AgentPermissionEnforcement.Enforced,
                ConfinesFileWrites: true, AllowsNetwork: true, PermissionArguments(profile),
                "codex confines file writes to the workspace and enables network access.")
    };

    /// <summary>
    /// Verified against codex-cli 0.145.0. <c>-m/--model</c> is a first-class flag; effort has no
    /// flag and rides the general <c>-c key=value</c> config channel as
    /// <c>model_reasoning_effort</c>.
    ///
    /// Codex performs no local validation of that value — a launch with a nonsense level starts
    /// normally, reports it, and only fails at the API, having already spent a request. Even
    /// <c>--strict-config</c> does not help: it rejects unrecognized config *fields*, not bad
    /// values. Checking here is the only way an operator's typo costs nothing.
    ///
    /// This is the union across the models a <c>model/list</c> capability query returned on
    /// 2026-08-08, and it is only a gate: the per-model sets differ, with <c>ultra</c> and
    /// <c>max</c> offered by the GPT-5.6 family but not by <c>gpt-5.4</c> or
    /// <c>gpt-5.3-codex-spark</c>. Notably no model advertises <c>none</c> or <c>minimal</c>, even
    /// though an API rejection message once listed them.
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
            ExecutionEffort.Max,
            ExecutionEffort.Ultra
        });

    // The selection precedes "-C" so the prompt stays the final positional argument.
    public AgentInvocation BuildStart(WorkItemDetail item, SessionHandle handle, Workspace workspace,
        AgentPermissionProfile permissions, string? promptAddendum = null,
        bool requiresUserConfirmation = false, ExecutionSelection? selection = null) =>
        new("codex", ["exec", "--json", "--skip-git-repo-check", .. PermissionArguments(permissions),
            .. ExecutionSelectionArguments.ForCodex(selection),
            "-C", workspace.Path,
            WorkerPrompt.Append(WorkerPrompt.For(item.Id, requiresUserConfirmation), promptAddendum)], workspace.Path,
            new Dictionary<string, string>(), true);

    // A literal "-" in the prompt position is codex exec's read-from-stdin form.
    public AgentInvocation BuildStartWithPrompt(SessionHandle handle, Workspace workspace,
        AgentPermissionProfile permissions, string prompt, ExecutionSelection? selection = null) =>
        new("codex", ["exec", "--json", "--skip-git-repo-check", .. PermissionArguments(permissions),
            .. ExecutionSelectionArguments.ForCodex(selection),
            "-C", workspace.Path, "-"], workspace.Path,
            new Dictionary<string, string>(), true, prompt);

    public AgentInvocation BuildResumeWithPrompt(SessionHandle handle, Workspace workspace,
        AgentPermissionProfile permissions, string prompt) =>
        new("codex", ["exec", "--json", "--skip-git-repo-check", .. PermissionArguments(permissions),
            "-C", workspace.Path, "resume", handle.Value, "-"], workspace.Path,
            new Dictionary<string, string>(), true, prompt);

    public AgentInvocation BuildResume(SessionHandle handle, Workspace workspace, string prompt,
        AgentPermissionProfile permissions) =>
        new("codex", ["exec", "--json", "--skip-git-repo-check", .. PermissionArguments(permissions),
            "-C", workspace.Path, "resume", handle.Value, DecorateResumePrompt(prompt)], workspace.Path,
            new Dictionary<string, string>(), true);

    public AgentInvocation BuildCheck(SessionHandle handle, Workspace workspace) =>
        new("codex", ["exec", "--json", "--skip-git-repo-check", SandboxFlag, "read-only",
            "-C", workspace.Path, "Reply exactly OK."], workspace.Path,
            new Dictionary<string, string>(), true);

    public string BuildInteractiveCommand(
        SessionHandle handle,
        Workspace workspace,
        IReadOnlyDictionary<string, string>? environment = null) =>
        InteractiveAgentCommand.Build(
            workspace,
            $"codex resume {InteractiveAgentCommand.Quote(handle.Value)}",
            environment);

    public LocalAgentInvocation BuildInteractiveInvocation(
        SessionHandle handle,
        Workspace workspace,
        IReadOnlyDictionary<string, string>? environment = null) =>
        new("codex", ["resume", handle.Value], workspace.Path,
            environment ?? new Dictionary<string, string>());

    public DesktopLaunchAddress BuildDesktopLaunch(SessionHandle handle) =>
        DesktopLaunchAddresses.Build(
            Agent, "codex", "threads", handle.Value, "ChatGPT");

    public string? TryExtractSessionId(string outputLine)
    {
        try
        {
            using var document = JsonDocument.Parse(outputLine);
            var root = document.RootElement;
            return root.TryGetProperty("type", out var type) && type.GetString() == "thread.started" &&
                   root.TryGetProperty("thread_id", out var thread)
                ? thread.GetString()
                : null;
        }
        catch (JsonException) { return null; }
    }

    private static IReadOnlyList<string> PermissionArguments(AgentPermissionProfile profile) =>
        profile switch
        {
            AgentPermissionProfile.Full => [SandboxFlag, "danger-full-access"],
            AgentPermissionProfile.ReadOnly => [SandboxFlag, "read-only"],
            _ => WorkspaceSandbox
        };

    public async Task<AgentRunResult> InterpretAsync(Stream stdout, int exitCode, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(stdout, leaveOpen: true);
        string? sessionId = null;
        string? final = null;
        var completed = false;
        var failed = false;
        JsonElement? terminalError = null;
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
            InterpretLine(
                line,
                ref sessionId,
                ref final,
                ref completed,
                ref failed,
                ref terminalError);
        if (sessionId is null)
            return new AgentRunResult(AgentOutcome.Rejected, null,
                "Codex output ended before thread.started.", exitCode,
                AgentFailureClassifier.Unknown(
                    Agent, "Codex output ended before thread.started.", exitCode));
        var succeeded = completed && !failed && exitCode == 0;
        var failure = FailureFor(succeeded, terminalError, final, exitCode);
        var outcome = succeeded ? AgentOutcome.Succeeded : AgentOutcome.Failed;
        return new AgentRunResult(outcome, sessionId, final, exitCode, failure);
    }

    private static void InterpretLine(
        string line,
        ref string? sessionId,
        ref string? final,
        ref bool completed,
        ref bool failed,
        ref JsonElement? terminalError)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            var type = root.TryGetProperty("type", out var typeValue) ? typeValue.GetString() : null;
            if (type == "thread.started" && root.TryGetProperty("thread_id", out var thread))
                sessionId ??= thread.GetString();
            completed |= type == "turn.completed";
            if (type == "turn.failed")
            {
                failed = true;
                terminalError = root.Clone();
            }
            // Capture the agent's actual assistant text — Codex emits it as an "agent_message"
            // item — rather than the trailing "turn.completed" usage-stats line, which is always
            // the last line of the stream and carries no human-useful content.
            if (type == "item.completed" &&
                root.TryGetProperty("item", out var item) &&
                item.TryGetProperty("type", out var itemType) &&
                itemType.GetString() == "agent_message" &&
                item.TryGetProperty("text", out var text))
            {
                final = text.GetString();
            }
        }
        catch (JsonException)
        {
            // Codex may print a non-JSON diagnostic before terminating. The missing terminal event
            // or nonzero exit code still makes the run fail safely.
        }
    }

    private AgentFailure? FailureFor(
        bool succeeded,
        JsonElement? terminalError,
        string? final,
        int exitCode)
    {
        if (succeeded)
            return null;
        return terminalError is { } error
            ? AgentFailureClassifier.FromEvent(Agent, error, exitCode, now())
            : AgentFailureClassifier.Unknown(Agent, final, exitCode);
    }

}
