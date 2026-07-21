using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Highbyte.Wrighty.Models;

namespace Highbyte.Wrighty.Workers;

public enum AgentOutcome { Succeeded, Failed, TimedOut, Rejected }

public sealed record SessionHandle(string Value);

public sealed record Workspace(string Path, bool IsWorktree = false, string? Branch = null);

public sealed record AgentInvocation(
    string Executable,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    IReadOnlyDictionary<string, string> Environment,
    bool CloseStandardInput = true);

public sealed record AgentRunResult(
    AgentOutcome Outcome,
    string? SessionId,
    string? FinalMessage,
    int ExitCode = 0);

public interface IAgentAdapter
{
    string AgentType { get; }
    bool SupportsPreassignedHandle { get; }
    AgentInvocation BuildStart(WorkItemDetail item, SessionHandle handle, Workspace workspace);
    AgentInvocation BuildResume(SessionHandle handle, Workspace workspace, string prompt);
    AgentInvocation BuildCheck(SessionHandle handle, Workspace workspace);
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
    public static string For(WorkItemId id) => For(id, mentionSkill: true);

    public static string ForClaude(WorkItemId id) =>
        $"/wrighty {For(id, mentionSkill: false)}";

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

    private static string For(WorkItemId id, bool mentionSkill) =>
        $"Work Wrighty item {id.Value}. It is already claimed for you by a worker, and your " +
        "claim handle is in WRIGHTY_CLAIMANT_ID / WRIGHTY_CLAIM_TOKEN — do not claim it again. " +
        $"{(mentionSkill ? "Use the wrighty skill. " : string.Empty)}" +
        $"Run `wrighty get {id.Value} --json` for details. " +
        $"Call `wrighty finish {id.Value}` only when the tracked work is genuinely complete. " +
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

public sealed class ClaudeAgentAdapter : IAgentAdapter
{
    public string AgentType => "claude";
    public bool SupportsPreassignedHandle => true;

    public AgentInvocation BuildStart(WorkItemDetail item, SessionHandle handle, Workspace workspace) =>
        Invocation(workspace, ["-p", WorkerPrompt.ForClaude(item.Id), "--session-id", handle.Value,
            "--output-format", "json", "--dangerously-skip-permissions"]);

    public AgentInvocation BuildResume(SessionHandle handle, Workspace workspace, string prompt) =>
        Invocation(workspace,
            ["-p", WorkerPrompt.ForClaudeResume(prompt), "--resume", handle.Value,
                "--output-format", "json", "--dangerously-skip-permissions"]);

    public AgentInvocation BuildCheck(SessionHandle handle, Workspace workspace) =>
        Invocation(workspace, ["-p", "Reply exactly OK.", "--session-id", handle.Value,
            "--output-format", "json", "--dangerously-skip-permissions"]);

    public string BuildInteractiveCommand(
        SessionHandle handle,
        Workspace workspace,
        IReadOnlyDictionary<string, string>? environment = null) =>
        InteractiveAgentCommand.Build(
            workspace,
            $"claude --resume {InteractiveAgentCommand.Quote(handle.Value)}",
            environment);

    public async Task<AgentRunResult> InterpretAsync(Stream stdout, int exitCode, CancellationToken cancellationToken)
    {
        try
        {
            using var document = await JsonDocument.ParseAsync(stdout, cancellationToken: cancellationToken);
            var root = document.RootElement;
            var failed = root.TryGetProperty("is_error", out var error) && error.GetBoolean();
            var subtype = root.TryGetProperty("subtype", out var subtypeValue) ? subtypeValue.GetString() : null;
            var success = !failed && string.Equals(subtype, "success", StringComparison.OrdinalIgnoreCase);
            return new AgentRunResult(success && exitCode == 0 ? AgentOutcome.Succeeded : AgentOutcome.Failed,
                root.TryGetProperty("session_id", out var session) ? session.GetString() : null,
                root.TryGetProperty("result", out var result) ? result.GetString() : null, exitCode);
        }
        catch (JsonException)
        {
            return new AgentRunResult(AgentOutcome.Rejected, null, "Claude returned invalid JSON.", exitCode);
        }
    }

    private static AgentInvocation Invocation(Workspace workspace, IReadOnlyList<string> arguments) =>
        new("claude", arguments, workspace.Path, new Dictionary<string, string>());
}

public sealed class CodexAgentAdapter : IAgentAdapter
{
    public string AgentType => "codex";
    public bool SupportsPreassignedHandle => false;

    public AgentInvocation BuildStart(WorkItemDetail item, SessionHandle handle, Workspace workspace) =>
        new("codex", ["exec", "--json", "--skip-git-repo-check", "--sandbox", "workspace-write", "-C", workspace.Path,
            WorkerPrompt.For(item.Id)], workspace.Path, new Dictionary<string, string>(), true);

    public AgentInvocation BuildResume(SessionHandle handle, Workspace workspace, string prompt) =>
        new("codex", ["exec", "--json", "--skip-git-repo-check", "--sandbox", "workspace-write",
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

    public async Task<AgentRunResult> InterpretAsync(Stream stdout, int exitCode, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(stdout, leaveOpen: true);
        string? sessionId = null;
        string? final = null;
        var completed = false;
        var failed = false;
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
                failed |= type == "turn.failed";
                final = line;
            }
            catch (JsonException) { }
        }
        if (sessionId is null)
            return new AgentRunResult(AgentOutcome.Rejected, null,
                "Codex output ended before thread.started.", exitCode);
        return new AgentRunResult(completed && !failed && exitCode == 0
            ? AgentOutcome.Succeeded : AgentOutcome.Failed, sessionId, final, exitCode);
    }

}

public sealed class CopilotAgentAdapter : IAgentAdapter
{
    public string AgentType => "copilot";
    public bool SupportsPreassignedHandle => true;

    public AgentInvocation BuildStart(WorkItemDetail item, SessionHandle handle, Workspace workspace) =>
        Invocation(workspace, ["-p", WorkerPrompt.For(item.Id), "-n", handle.Value, "--allow-all-tools",
            "--output-format", "json", "--no-remote", "-C", workspace.Path]);

    public AgentInvocation BuildResume(SessionHandle handle, Workspace workspace, string prompt) =>
        Invocation(workspace, ["-p", prompt, $"--resume={handle.Value}", "--allow-all-tools",
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

    public async Task<AgentRunResult> InterpretAsync(Stream stdout, int exitCode, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(stdout, leaveOpen: true);
        JsonElement? terminal = null;
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            try
            {
                using var document = JsonDocument.Parse(line);
                if (document.RootElement.TryGetProperty("type", out var type) && type.GetString() == "result")
                    terminal = document.RootElement.Clone();
            }
            catch (JsonException) { }
        }
        if (terminal is not { } result)
            return new AgentRunResult(AgentOutcome.Rejected, null, "Copilot returned no result event.", exitCode);
        var resultExit = result.TryGetProperty("exitCode", out var resultExitValue)
            ? resultExitValue.GetInt32() : exitCode;
        return new AgentRunResult(resultExit == 0 && exitCode == 0 ? AgentOutcome.Succeeded : AgentOutcome.Failed,
            result.TryGetProperty("sessionId", out var session) ? session.GetString() : null,
            result.GetRawText(), resultExit);
    }

    private static AgentInvocation Invocation(Workspace workspace, IReadOnlyList<string> arguments) =>
        new("copilot", arguments, workspace.Path, new Dictionary<string, string>());
}
