using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Highbyte.Wrighty.ApprovedContext;
using Highbyte.Wrighty.Models;

namespace Highbyte.Wrighty.Workers;

public enum AgentOutcome { Succeeded, Failed, TimedOut, Rejected }

public sealed record SessionHandle(string Value);

public sealed record Workspace(string Path, bool IsWorktree = false, string? Branch = null);

/// <param name="StandardInput">
/// Text written to the vendor's standard input, or null to close it immediately.
///
/// This is how an approved context reaches an agent. The alternative — putting it in
/// <paramref name="Arguments"/> — publishes the work item's content to every process on the machine
/// through the process table, and it lands in the argument list Wrighty prints in worker events.
/// Standard input is visible to neither.
/// </param>
public sealed record AgentInvocation(
    string Executable,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    IReadOnlyDictionary<string, string> Environment,
    bool CloseStandardInput = true,
    string? StandardInput = null);

/// <summary>
/// A local, interactive agent process without any shell syntax.
///
/// The same structured address is used by the CLI's direct execution path and by local launchers.
/// A printable command remains a presentation format only; it is never parsed back into an
/// executable request.
/// </summary>
public sealed record LocalAgentInvocation(
    string Executable,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    IReadOnlyDictionary<string, string> Environment);

public enum DesktopSessionSupport
{
    Supported,
    Experimental,
    Unavailable
}

public sealed record DesktopLaunchAddress(
    string Vendor,
    Uri? Uri,
    DesktopSessionSupport Support,
    string? Reason,
    string RequiredApplication,
    bool Enabled = false,
    string? Prerequisite = null,
    string? CompatibilityWarning = null)
{
    public bool CanLaunch =>
        Uri is not null &&
        (Support == DesktopSessionSupport.Supported ||
         Support == DesktopSessionSupport.Experimental && Enabled);

    public DesktopLaunchAddress EnableExperimental(bool enabled) =>
        Support == DesktopSessionSupport.Experimental && enabled
            ? this with
            {
                Enabled = true,
                Reason = "Experimental support is enabled for this repository."
            }
            : this;
}

public sealed record AgentRunResult(
    AgentOutcome Outcome,
    string? SessionId,
    string? FinalMessage,
    int ExitCode = 0,
    AgentFailure? Failure = null)
{
    /// <summary>
    /// The report block the agent's final message carried, or null when it carried none usable.
    ///
    /// Parsed on demand rather than stored: every adapter produces this record, and a computed
    /// property means none of them can forget to populate it, or populate it from something other
    /// than the message it actually returned.
    /// </summary>
    public ApprovedContext.AgentReportContent? Report =>
        ApprovedContext.AgentReportParser.TryParse(FinalMessage);

    /// <summary>
    /// What to record when there is no usable report: the response itself, bounded. An agent that
    /// wrote prose instead of a block still said something worth keeping.
    /// </summary>
    public string? ReportFallback =>
        Report is null ? ApprovedContext.AgentReportParser.BoundedFallback(FinalMessage) : null;
}

public interface IAgentAdapter
{
    string Agent { get; }
    string ExecutableName => Agent;
    bool SupportsPreassignedHandle { get; }

    /// <summary>
    /// The effective posture this adapter produces for a requested profile, including what the
    /// vendor cannot enforce. Callers report this rather than assuming the request was honored.
    /// </summary>
    AgentPermissions DescribePermissions(AgentPermissionProfile profile);

    AgentInvocation BuildStart(WorkItemDetail item, SessionHandle handle, Workspace workspace,
        AgentPermissionProfile permissions, string? promptAddendum = null,
        bool requiresUserConfirmation = false);

    /// <summary>
    /// A fresh launch whose prompt Wrighty supplies in full, delivered on standard input.
    ///
    /// Separate from <see cref="BuildStart"/> because the two differ in more than a string. That one
    /// tells the agent to go and read the item, and its short prompt is safe on the command line;
    /// this one carries the approved content, which must not appear in the process table or in the
    /// argument list worker events print.
    ///
    /// Every vendor accepts a piped prompt, but each asks for it differently, so the flag that
    /// normally carries the text is what changes here — not the permission, session or output
    /// arguments, which stay identical to a bootstrap launch.
    /// </summary>
    AgentInvocation BuildStartWithPrompt(SessionHandle handle, Workspace workspace,
        AgentPermissionProfile permissions, string prompt);

    /// <summary>
    /// Re-entering a recorded session with a prompt Wrighty supplies, delivered on standard input
    /// for the same reason as a fresh launch: an approved entry's text must not reach the process
    /// table or the argument list worker events print.
    /// </summary>
    AgentInvocation BuildResumeWithPrompt(SessionHandle handle, Workspace workspace,
        AgentPermissionProfile permissions, string prompt);
    AgentInvocation BuildResume(SessionHandle handle, Workspace workspace, string prompt,
        AgentPermissionProfile permissions);

    /// <summary>
    /// The read-only liveness probe. It issues no <c>wrighty</c> command and needs no workspace
    /// write or network access, so it always runs at the narrowest posture the vendor offers,
    /// independently of the configured profile.
    /// </summary>
    AgentInvocation BuildCheck(SessionHandle handle, Workspace workspace);
    LocalAgentInvocation BuildInteractiveInvocation(
        SessionHandle handle,
        Workspace workspace,
        IReadOnlyDictionary<string, string>? environment = null);
    DesktopLaunchAddress BuildDesktopLaunch(SessionHandle handle);
    string BuildInteractiveCommand(
        SessionHandle handle,
        Workspace workspace,
        IReadOnlyDictionary<string, string>? environment = null);
    string? TryExtractSessionId(string outputLine) => null;
    Task<AgentRunResult> InterpretAsync(Stream stdout, int exitCode, CancellationToken cancellationToken);
}

internal static class InteractiveAgentCommand
{
    public static string Build(
        Workspace workspace,
        string vendorCommand,
        IReadOnlyDictionary<string, string>? environment)
    {
        var environmentPrefix = environment is null || environment.Count == 0
            ? string.Empty
            : string.Join(" ", environment.Select(pair =>
                $"{pair.Key}={Quote(pair.Value)}")) + " ";
        return $"cd {Quote(workspace.Path)} && {environmentPrefix}{vendorCommand}";
    }

    public static string Quote(string value) =>
        $"'{value.Replace("'", "'\\''", StringComparison.Ordinal)}'";
}

public static class WorkerPrompt
{
    public static string For(WorkItemId id) =>
        For(id, mentionSkill: true, requiresUserConfirmation: false);

    public static string For(WorkItemId id, bool requiresUserConfirmation) =>
        For(id, mentionSkill: true, requiresUserConfirmation);

    public static string Append(string prompt, string? addendum) =>
        string.IsNullOrWhiteSpace(addendum) ? prompt : $"{prompt} {addendum}";

    /// <summary>
    /// The unattended execution contract: what kind of session this is and which interactive
    /// habits do not apply (issue #87). Stated explicitly because agent behavior without it is
    /// nondeterministic — user-level conventions written for interactive sessions have made one
    /// live run block forever waiting for a branch-name approval no one could give, while another
    /// under identical configuration silently skipped workspace isolation instead. Capability
    /// configuration cannot settle behavior; only stated expectations can.
    /// </summary>
    public static string UnattendedContract(Workspace workspace) =>
        "This is an unattended automated session; no interactive operator is present. Never pause " +
        "to wait for approval or confirmation, and do not follow conventions that require asking " +
        "a human before acting — for this run, these instructions supersede general " +
        "interactive-session conventions. If you cannot proceed, report the blocker in your final " +
        "response and end the run. " +
        WorkspaceProvenance(workspace) +
        "Do not create branches or worktrees, do not fetch, do not change git configuration or " +
        "remotes, and never push.";

    private static string WorkspaceProvenance(Workspace workspace) => workspace switch
    {
        { IsWorktree: true, Branch: { Length: > 0 } branch } =>
            $"You are already working in a dedicated isolated worktree on branch `{branch}`, " +
            "prepared for this task. ",
        { IsWorktree: true } =>
            "You are already working in a dedicated isolated worktree prepared for this task. ",
        _ => "You are working directly in the repository checkout prepared for this run. "
    };

    /// <summary>
    /// The explicit commit instruction for a run. Instructing in both directions keeps the
    /// completion outcome deterministic: an agent's autonomous commit habit must not decide
    /// whether the operator's inspect-first policy holds.
    ///
    /// <para>Only a worktree run under the `agent` commit policy is told to commit. A non-worktree
    /// run is always told not to, whatever the policy says: the workspace is the shared checkout,
    /// and a commit there lands on whatever branch the operator has checked out. Before issue #87
    /// a non-worktree run received no instruction at all, which left the outcome to the agent's
    /// habits.</para>
    /// </summary>
    public static string? CommitInstruction(Workspace workspace, string? commitPolicy) =>
        workspace.IsWorktree &&
        string.Equals(commitPolicy, "agent", StringComparison.OrdinalIgnoreCase)
            ? "Commit your work with git in logical commits referencing the item before finishing; " +
              "leave nothing uncommitted."
            : "Do not run git commit: leave every file change uncommitted so the operator can " +
              "review the work before it is committed.";

    /// <summary>
    /// Everything a spawned run needs prepended or appended beyond the work itself: the unattended
    /// contract and the commit expectation. One composition point so the launch, resume, queued,
    /// and preview paths cannot drift apart.
    /// </summary>
    public static string RunAddendum(Workspace workspace, string? commitPolicy) =>
        $"{UnattendedContract(workspace)} {CommitInstruction(workspace, commitPolicy)}";

    public static string ForClaude(WorkItemId id) =>
        ForClaude(id, requiresUserConfirmation: false);

    public static string ForClaude(WorkItemId id, bool requiresUserConfirmation) =>
        $"/wrighty {For(id, mentionSkill: false, requiresUserConfirmation)}";

    public static string ForClaudeResume(string prompt) =>
        prompt.TrimStart().StartsWith("/wrighty", StringComparison.Ordinal)
            ? prompt
            : $"/wrighty {prompt}";

    public static string ForResume(WorkItemId id, string agentType)
    {
        var prompt =
            $"Item {id.Value} has been clarified. Re-read it with `wrighty get {id.Value} --json`, " +
            $"implement the updated requirements, and call `wrighty finish {id.Value}` only when " +
            "the tracked work is genuinely complete. If the item is still blocked, report only " +
            "the blocker and the clarification or change needed. Do not suggest Wrighty claim, " +
            "edit, takeover, finish, archive, or worker commands, and do not explain claimant IDs " +
            "or claim tokens; the worker prints the operator's next actions. If a Wrighty mutation " +
            "fails with CLAIM_STALE, stop immediately.";
        return agentType switch
        {
            "claude" or "copilot" => $"/wrighty {prompt}",
            "codex" => $"$wrighty {prompt}",
            _ => prompt
        };
    }

    /// <summary>
    /// A reaction carries no natural-language task content, so Wrighty supplies the fixed meaning
    /// rather than asking the agent to infer intent from an emoji. Completion is still an agent
    /// run: it verifies current state and calls the ordinary fenced finish command.
    /// </summary>
    public static string? ForControlReaction(TrustedContinuationEvent? trigger) => trigger switch
    {
        {
            Source: TrustedContinuationSource.Reaction,
            Kind: TrustedContinuationKind.CompletionRequested
        } =>
            "Operator control: a trusted operator requested completion on the latest Wrighty run " +
            "report. Verify the current work against the approved requirements. If it is genuinely " +
            "complete, call the ordinary `wrighty finish` command for this item; otherwise report " +
            "the remaining work or blocker. The reaction did not itself finish or archive anything.",
        { Source: TrustedContinuationSource.Reaction } =>
            "Operator control: a trusted operator requested continuation on the latest Wrighty " +
            "status comment. Continue the retained session from its current approved context.",
        _ => null
    };

    /// <summary>
    /// How to work a claimed item: finishing, reporting a blocker, and the claim-fencing rules —
    /// everything except how the agent learns what the work *is*.
    ///
    /// Split out because the two prompt paths differ only in that last part. The bootstrap prompt
    /// sends the agent to read the item; a prompt carrying an approved context must not, because
    /// reading the item returns whatever is on the tracker now rather than what was approved. These
    /// rules are identical either way, and stating them twice is how they drift.
    /// </summary>
    public static string OperatingInstructions(WorkItemId id) =>
        OperatingInstructions(id, requiresUserConfirmation: false);

    /// <param name="requiresUserConfirmation">
    /// When true, the agent may not finish on its own judgement. It reports the work it believes
    /// complete and stops; the item waits for a person to accept it in the discussion, and a later
    /// run — carrying that acceptance as ordinary approved context — is the one that finishes.
    ///
    /// The acceptance is deliberately not a command or a marker. A reply saying the work is good
    /// enough is already task direction the agent must read and act on, and inventing a second
    /// control vocabulary for it would give an operator two ways to say the same thing and Wrighty
    /// a new way to misparse one of them.
    /// </param>
    public static string OperatingInstructions(WorkItemId id, bool requiresUserConfirmation) =>
        (requiresUserConfirmation
            ? $"Do not call `wrighty finish {id.Value}` on your own judgement. This item completes " +
              "only when a person accepts the work. When you believe it is done, describe what you " +
              "changed and why you consider it complete in your final response, then stop without " +
              "finishing — that is the expected successful ending, not a failure. If someone in the " +
              "supplied discussion has already accepted the work, finish it: verify the current " +
              "state first, and treat only a clear acceptance as one. Ambiguity, silence, or a " +
              "further question is not acceptance; report and stop again. "
            : $"Call `wrighty finish {id.Value}` only when the tracked work is genuinely complete. ") +
        "If the item is blocked or needs clarification, do not call finish: explain the blocker " +
        "clearly in your final response and exit. Report only the blocker and the clarification or " +
        "change needed. Do not suggest Wrighty claim, edit, takeover, finish, archive, or worker " +
        "commands, and do not explain claimant IDs or claim tokens; the worker prints the operator's " +
        "next actions. The worker will report that operator attention is needed and retain the " +
        "resumable claim until its finite lease expires. " +
        "Wrighty manages lease renewal: do not speculate about `expiresAt`, report possible expiry " +
        "from the timestamp alone, or attempt to reclaim; only CLAIM_EXPIRED or CLAIM_STALE from a " +
        "Wrighty mutation is authoritative. " +
        "If a Wrighty mutation fails with CLAIM_STALE, a human has taken this item over: " +
        "stop immediately, do not attempt to reclaim it, and do not keep editing files.";

    private static string For(WorkItemId id, bool mentionSkill, bool requiresUserConfirmation) =>
        $"Work Wrighty item {id.Value}. It is already claimed for you by a worker, and your " +
        "claim handle is in WRIGHTY_CLAIMANT_ID / WRIGHTY_CLAIM_TOKEN — do not claim it again. " +
        $"{(mentionSkill ? "Use the wrighty skill. " : string.Empty)}" +
        $"Run `wrighty get {id.Value} --json` for details. " +
        OperatingInstructions(id, requiresUserConfirmation);

}

public static class SessionHandles
{
    // Preassigned handles are stable within one fenced claim generation and change on reacquisition,
    // so a retry starts a new vendor session instead of colliding with an existing one.
    private static readonly Guid Namespace = new("8d65e798-70e4-5d91-9d7d-cbb6b16e0429");

    public static SessionHandle ForClaude(WorkItemId id, string claimGeneration) =>
        new(CreateDeterministicUuid(Namespace, $"wrighty-{id.Value}-{claimGeneration}").ToString());

    public static SessionHandle ForNamedVendor(WorkItemId id, string claimGeneration)
    {
        var item = string.Concat(id.Value.Select(ch =>
            char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '-'));
        var generation = Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(claimGeneration)))[..12];
        return new SessionHandle($"wrighty-{item}-{generation}");
    }

    private static Guid CreateDeterministicUuid(Guid namespaceId, string name)
    {
        var namespaceBytes = namespaceId.ToByteArray();
        SwapByteOrder(namespaceBytes);
        var nameBytes = Encoding.UTF8.GetBytes(name);
        var input = new byte[namespaceBytes.Length + nameBytes.Length];
        namespaceBytes.CopyTo(input, 0);
        nameBytes.CopyTo(input, namespaceBytes.Length);
        var hash = SHA256.HashData(input);
        // RFC 9562 UUIDv8 reserves the payload for application-defined data. This preserves a
        // deterministic UUID-shaped Claude handle without relying on UUIDv5's SHA-1 algorithm.
        hash[6] = (byte)((hash[6] & 0x0f) | 0x80);
        hash[8] = (byte)((hash[8] & 0x3f) | 0x80);
        var bytes = hash[..16];
        SwapByteOrder(bytes);
        return new Guid(bytes);
    }

    private static void SwapByteOrder(byte[] bytes)
    {
        (bytes[0], bytes[3]) = (bytes[3], bytes[0]);
        (bytes[1], bytes[2]) = (bytes[2], bytes[1]);
        (bytes[4], bytes[5]) = (bytes[5], bytes[4]);
        (bytes[6], bytes[7]) = (bytes[7], bytes[6]);
    }
}

public sealed class ClaudeAgentAdapter(Func<DateTimeOffset>? clock = null) : IAgentAdapter
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

    public string Agent => "claude";
    public string ExecutableName => "claude";
    public bool SupportsPreassignedHandle => true;

    public AgentPermissions DescribePermissions(AgentPermissionProfile profile) =>
        profile == AgentPermissionProfile.Full
            ? new AgentPermissions(Agent, profile, AgentPermissionEnforcement.Unrestricted,
                ConfinesFileWrites: false, AllowsNetwork: true, PermissionArguments(profile),
                "claude runs unrestricted: all permission checks are bypassed.")
            : new AgentPermissions(Agent, profile, AgentPermissionEnforcement.Partial,
                ConfinesFileWrites: false, AllowsNetwork: true, PermissionArguments(profile),
                "claude auto-approves only allow-listed tools " +
                $"({string.Join(", ", WorkspaceTools)}) and denies the rest, but has no verified " +
                "headless mode that confines file writes to the workspace.");

    public AgentInvocation BuildStart(WorkItemDetail item, SessionHandle handle, Workspace workspace,
        AgentPermissionProfile permissions, string? promptAddendum = null,
        bool requiresUserConfirmation = false) =>
        Invocation(workspace, ["-p", WorkerPrompt.Append(WorkerPrompt.ForClaude(item.Id, requiresUserConfirmation), promptAddendum),
            "--session-id", handle.Value,
            "--output-format", "json", .. PermissionArguments(permissions)]);

    // `-p` with no value is print mode reading standard input. Measured working in phase 0.
    public AgentInvocation BuildStartWithPrompt(SessionHandle handle, Workspace workspace,
        AgentPermissionProfile permissions, string prompt) =>
        Invocation(workspace, ["-p", "--session-id", handle.Value,
            "--output-format", "json", .. PermissionArguments(permissions)]) with
        {
            StandardInput = prompt
        };

    public AgentInvocation BuildResumeWithPrompt(SessionHandle handle, Workspace workspace,
        AgentPermissionProfile permissions, string prompt) =>
        Invocation(workspace, ["-p", "--resume", handle.Value,
            "--output-format", "json", .. PermissionArguments(permissions)]) with
        {
            StandardInput = prompt
        };

    public AgentInvocation BuildResume(SessionHandle handle, Workspace workspace, string prompt,
        AgentPermissionProfile permissions) =>
        Invocation(workspace,
            ["-p", WorkerPrompt.ForClaudeResume(prompt), "--resume", handle.Value,
                "--output-format", "json", .. PermissionArguments(permissions)]);

    // The probe only has to prove the vendor answers and honors the preassigned handle, so it runs
    // with every tool disabled instead of bypassing permissions. Verified on 2026-07-25 with Claude
    // Code 2.1.219: `--tools ""` still returns "OK" and echoes the requested session id.
    public AgentInvocation BuildCheck(SessionHandle handle, Workspace workspace) =>
        Invocation(workspace, ["-p", "Reply exactly OK.", "--session-id", handle.Value,
            "--output-format", "json", "--tools", ""]);

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
            "Claude");
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
        profile == AgentPermissionProfile.Full
            ? ["--dangerously-skip-permissions"]
            : ["--permission-mode", "acceptEdits", "--allowedTools", string.Join(" ", WorkspaceTools)];

    private static AgentInvocation Invocation(Workspace workspace, IReadOnlyList<string> arguments) =>
        new("claude", arguments, workspace.Path, new Dictionary<string, string>());
}

public sealed class CodexAgentAdapter(Func<DateTimeOffset>? clock = null) : IAgentAdapter
{
    private readonly Func<DateTimeOffset> now = clock ?? (() => DateTimeOffset.UtcNow);

    public string Agent => "codex";
    public string ExecutableName => "codex";
    public bool SupportsPreassignedHandle => false;

    // Codex is the one vendor that expresses the `workspace` profile exactly: writes confined to
    // the workspace while the network stays reachable for the agent's own GitHub-backend commands
    // (the prompt has it run `wrighty get`, and the skill runs `wrighty init --check`). This
    // replaces the interim `danger-full-access` parity fix, which was only ever a stopgap for the
    // plain `workspace-write` sandbox disabling network by default. Verified on 2026-07-25 with
    // codex-cli 0.145.0: network reached, a workspace write succeeded, a parent-directory write was
    // denied.
    private static readonly IReadOnlyList<string> WorkspaceSandbox =
        ["--sandbox", "workspace-write", "-c", "sandbox_workspace_write.network_access=true"];

    public AgentPermissions DescribePermissions(AgentPermissionProfile profile) =>
        profile == AgentPermissionProfile.Full
            ? new AgentPermissions(Agent, profile, AgentPermissionEnforcement.Unrestricted,
                ConfinesFileWrites: false, AllowsNetwork: true, PermissionArguments(profile),
                "codex runs unrestricted: the sandbox is disabled.")
            : new AgentPermissions(Agent, profile, AgentPermissionEnforcement.Enforced,
                ConfinesFileWrites: true, AllowsNetwork: true, PermissionArguments(profile),
                "codex confines file writes to the workspace and enables network access.");

    public AgentInvocation BuildStart(WorkItemDetail item, SessionHandle handle, Workspace workspace,
        AgentPermissionProfile permissions, string? promptAddendum = null,
        bool requiresUserConfirmation = false) =>
        new("codex", ["exec", "--json", "--skip-git-repo-check", .. PermissionArguments(permissions),
            "-C", workspace.Path,
            WorkerPrompt.Append(WorkerPrompt.For(item.Id, requiresUserConfirmation), promptAddendum)], workspace.Path,
            new Dictionary<string, string>(), true);

    // A literal "-" in the prompt position is codex exec's read-from-stdin form.
    public AgentInvocation BuildStartWithPrompt(SessionHandle handle, Workspace workspace,
        AgentPermissionProfile permissions, string prompt) =>
        new("codex", ["exec", "--json", "--skip-git-repo-check", .. PermissionArguments(permissions),
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
            "-C", workspace.Path, "resume", handle.Value, prompt], workspace.Path,
            new Dictionary<string, string>(), true);

    public AgentInvocation BuildCheck(SessionHandle handle, Workspace workspace) =>
        new("codex", ["exec", "--json", "--skip-git-repo-check", "--sandbox", "read-only",
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
        profile == AgentPermissionProfile.Full
            ? ["--sandbox", "danger-full-access"]
            : WorkspaceSandbox;

    public async Task<AgentRunResult> InterpretAsync(Stream stdout, int exitCode, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(stdout, leaveOpen: true);
        string? sessionId = null;
        string? final = null;
        var completed = false;
        var failed = false;
        JsonElement? terminalError = null;
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
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
                if (type == "item.completed"
                    && root.TryGetProperty("item", out var item)
                    && item.TryGetProperty("type", out var itemType)
                    && itemType.GetString() == "agent_message"
                    && item.TryGetProperty("text", out var text))
                {
                    final = text.GetString();
                }
            }
            catch (JsonException) { }
        }
        if (sessionId is null)
            return new AgentRunResult(AgentOutcome.Rejected, null,
                "Codex output ended before thread.started.", exitCode,
                AgentFailureClassifier.Unknown(
                    Agent, "Codex output ended before thread.started.", exitCode));
        var succeeded = completed && !failed && exitCode == 0;
        var failure = succeeded
            ? null
            : terminalError is { } error
                ? AgentFailureClassifier.FromEvent(Agent, error, exitCode, now())
                : AgentFailureClassifier.Unknown(Agent, final, exitCode);
        return new AgentRunResult(succeeded
            ? AgentOutcome.Succeeded : AgentOutcome.Failed, sessionId, final, exitCode, failure);
    }

}

public sealed class CopilotAgentAdapter(Func<DateTimeOffset>? clock = null) : IAgentAdapter
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

    public AgentPermissions DescribePermissions(AgentPermissionProfile profile) =>
        profile == AgentPermissionProfile.Full
            ? new AgentPermissions(Agent, profile, AgentPermissionEnforcement.Unrestricted,
                ConfinesFileWrites: false, AllowsNetwork: true, PermissionArguments(profile),
                "copilot runs unrestricted: all tools, all paths, and all URLs are allowed.")
            : new AgentPermissions(Agent, profile, AgentPermissionEnforcement.Enforced,
                ConfinesFileWrites: true, AllowsNetwork: true, PermissionArguments(profile),
                "copilot auto-approves tools while keeping its own path verification and URL " +
                "confirmation, so file access stays within the workspace and the system temporary " +
                "directory.");

    public AgentInvocation BuildStart(WorkItemDetail item, SessionHandle handle, Workspace workspace,
        AgentPermissionProfile permissions, string? promptAddendum = null,
        bool requiresUserConfirmation = false) =>
        Invocation(workspace, ["-p", WorkerPrompt.Append(WorkerPrompt.For(item.Id, requiresUserConfirmation), promptAddendum),
            "-n", handle.Value, .. PermissionArguments(permissions),
            "--output-format", "json", "--no-remote", "-C", workspace.Path]);

    // No `-p` at all: copilot reads a piped prompt when the flag is absent. Phase 0 recorded a
    // stdin failure for this vendor having probed `copilot -p` with no value, which the CLI rejects
    // as a missing argument rather than as a refusal to read standard input. Re-measured on
    // 2026-07-27 with GitHub Copilot CLI 1.0.75: a piped prompt is read in full, and a ~100,000
    // character context with an identifier in its closing lines came back answered from the tail.
    public AgentInvocation BuildStartWithPrompt(SessionHandle handle, Workspace workspace,
        AgentPermissionProfile permissions, string prompt) =>
        Invocation(workspace, ["-n", handle.Value, .. PermissionArguments(permissions),
            "--output-format", "json", "--no-remote", "-C", workspace.Path]) with
        {
            StandardInput = prompt
        };

    public AgentInvocation BuildResumeWithPrompt(SessionHandle handle, Workspace workspace,
        AgentPermissionProfile permissions, string prompt) =>
        Invocation(workspace, [$"--resume={handle.Value}", .. PermissionArguments(permissions),
            "--output-format", "json", "--no-remote", "-C", workspace.Path]) with
        {
            StandardInput = prompt
        };

    public AgentInvocation BuildResume(SessionHandle handle, Workspace workspace, string prompt,
        AgentPermissionProfile permissions) =>
        Invocation(workspace, ["-p", prompt, $"--resume={handle.Value}",
            .. PermissionArguments(permissions),
            "--output-format", "json", "--no-remote", "-C", workspace.Path]);

    public AgentInvocation BuildCheck(SessionHandle handle, Workspace workspace) =>
        Invocation(workspace, ["-p", "Reply exactly OK.", "-n", handle.Value,
            "--output-format", "json", "--no-remote", "-C", workspace.Path]);

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
        {
            try
            {
                using var document = JsonDocument.Parse(line);
                if (!document.RootElement.TryGetProperty("type", out var type))
                    continue;
                if (type.GetString() == "result")
                    terminal = document.RootElement.Clone();
                else if (type.GetString() is "error" or "session.error")
                    terminalError = document.RootElement.Clone();
                else if (type.GetString() == "assistant.message" &&
                         TryReadCopilotAssistantMessage(document.RootElement) is { } content)
                    assistantMessage = content;
            }
            catch (JsonException) { }
        }
        if (terminal is not { } result)
            return new AgentRunResult(
                AgentOutcome.Rejected,
                null,
                "Copilot returned no result event.",
                exitCode,
                terminalError is { } error
                    ? AgentFailureClassifier.FromEvent(Agent, error, exitCode, now())
                    : AgentFailureClassifier.Unknown(
                        Agent, "Copilot returned no result event.", exitCode));
        var resultExit = result.TryGetProperty("exitCode", out var resultExitValue)
            ? resultExitValue.GetInt32() : exitCode;
        var succeeded = resultExit == 0 && exitCode == 0;
        var resultMessage = ReadCopilotResultMessage(result);
        var failure = succeeded
            ? null
            : AgentFailureClassifier.FromEvent(
                Agent, terminalError ?? result, resultExit, now());
        var finalMessage = resultMessage ??
                           (succeeded ? assistantMessage : failure?.SanitizedMessage) ??
                           assistantMessage ??
                           (succeeded
                               ? "Copilot completed without a final text response."
                               : "Copilot failed without a final text response.");
        return new AgentRunResult(succeeded ? AgentOutcome.Succeeded : AgentOutcome.Failed,
            result.TryGetProperty("sessionId", out var session) ? session.GetString() : null,
            finalMessage, resultExit, failure);
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
        profile == AgentPermissionProfile.Full ? ["--allow-all"] : ["--allow-all-tools"];

    private static AgentInvocation Invocation(Workspace workspace, IReadOnlyList<string> arguments) =>
        new("copilot", arguments, workspace.Path, new Dictionary<string, string>());
}

internal static class DesktopLaunchAddresses
{
    public static DesktopLaunchAddress Build(
        string vendor,
        string scheme,
        string route,
        string sessionId,
        string requiredApplication)
    {
        if (!ValidTechnicalId(sessionId))
        {
            return new DesktopLaunchAddress(
                vendor,
                null,
                DesktopSessionSupport.Unavailable,
                "The recorded session ID is not valid for a Desktop deep link.",
                requiredApplication);
        }
        return new DesktopLaunchAddress(
            vendor,
            new Uri($"{scheme}://{route}/{Uri.EscapeDataString(sessionId)}"),
            DesktopSessionSupport.Supported,
            null,
            requiredApplication,
            Enabled: true);
    }

    private static bool ValidTechnicalId(string value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= 256 &&
        value.All(character =>
            !char.IsControl(character) &&
            (char.IsAsciiLetterOrDigit(character) ||
             character is '-' or '_' or '.' or ':'));
}
