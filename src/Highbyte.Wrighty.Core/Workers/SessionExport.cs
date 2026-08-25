using System.Text;
using System.Text.Json;

namespace Highbyte.Wrighty.Workers;

/// <summary>One conversation message extracted from a source agent session, before selection,
/// redaction, or bounding — those belong to the handoff packet builder, so every exporter is
/// bounded the same way.</summary>
public sealed record ExportedSessionMessage(string Role, string Text);

/// <summary>
/// What an exporter could recover from the source vendor's session surface. Supplementary by
/// design: the work item and workspace stay authoritative, so an unavailable export leaves the
/// handoff packet valid (the workspace-only fallback) rather than failing the handoff.
/// </summary>
public sealed record SessionExportResult(
    IReadOnlyList<ExportedSessionMessage>? Messages,
    string? Source,
    string? Unavailable)
{
    public bool IsAvailable => Messages is not null;

    public static SessionExportResult From(
        string source, IReadOnlyList<ExportedSessionMessage> messages) =>
        new(messages, source, null);

    public static SessionExportResult NotAvailable(string reason) => new(null, null, reason);
}

/// <summary>
/// Reads a finished session's conversation from one vendor's local session surface. Exporters
/// never throw for missing or unreadable session data: transcript context is a best-effort
/// supplement, and any failure must degrade to the workspace-only packet, not block the handoff.
/// </summary>
public interface IAgentSessionExporter
{
    string Agent { get; }

    Task<SessionExportResult> ExportAsync(string sessionId, CancellationToken cancellationToken);
}

/// <summary>A vendor whose supported export surface is not integrated yet. Exists so the seam
/// covers every agent from day one and the reason reaches diagnostics instead of a null check.</summary>
public sealed class UnsupportedSessionExporter(string agent, string reason) : IAgentSessionExporter
{
    public string Agent => agent;

    public Task<SessionExportResult> ExportAsync(
        string sessionId, CancellationToken cancellationToken) =>
        Task.FromResult(SessionExportResult.NotAvailable(reason));
}

/// <summary>
/// Reads Claude Code's local transcript store: one JSONL file per session under a per-project
/// directory. The session file is found by scanning project directories for
/// <c>&lt;sessionId&gt;.jsonl</c> rather than reconstructing the store's path-slug convention,
/// which is undocumented and could change; session IDs are UUIDs, so a scan cannot pick the wrong
/// session.
/// </summary>
public sealed class ClaudeSessionExporter(string? transcriptRoot = null) : IAgentSessionExporter
{
    // A transcript is local trusted-machine data but still bounded on read, so a corrupt or
    // pathological file cannot balloon the worker's memory.
    private const long MaxTranscriptBytes = 64 * 1024 * 1024;

    public string Agent => "claude";

    public async Task<SessionExportResult> ExportAsync(
        string sessionId, CancellationToken cancellationToken)
    {
        if (!IsSafeSessionId(sessionId))
            return SessionExportResult.NotAvailable(
                "The recorded Claude session ID is not a valid session file name.");

        var root = transcriptRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".claude", "projects");
        if (!Directory.Exists(root))
            return SessionExportResult.NotAvailable(
                "No local Claude transcript store was found on this host.");

        string? transcript;
        try
        {
            transcript = Directory.EnumerateDirectories(root)
                .Select(directory => Path.Combine(directory, sessionId + ".jsonl"))
                .FirstOrDefault(File.Exists);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return SessionExportResult.NotAvailable(
                "The local Claude transcript store could not be read.");
        }

        if (transcript is null)
            return SessionExportResult.NotAvailable(
                $"No transcript for session '{sessionId}' exists in the local Claude " +
                "transcript store.");

        try
        {
            if (new FileInfo(transcript).Length > MaxTranscriptBytes)
                return SessionExportResult.NotAvailable(
                    "The Claude transcript is larger than the export limit.");
            var messages = new List<ExportedSessionMessage>();
            await foreach (var line in File.ReadLinesAsync(transcript, cancellationToken))
            {
                if (TryReadMessage(line) is { } message)
                    messages.Add(message);
            }

            return SessionExportResult.From("claude-local-transcript", messages);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return SessionExportResult.NotAvailable(
                $"The transcript for session '{sessionId}' could not be read.");
        }
    }

    private static bool IsSafeSessionId(string sessionId) =>
        SessionTranscripts.IsSafeSessionId(sessionId);

    /// <summary>
    /// One transcript line → one conversation message, or null for everything else. Only top-level
    /// user and assistant turns carry conversation text; summaries, meta entries, sidechain
    /// (subagent) traffic, and tool payloads are transcript bookkeeping, not the dialogue a target
    /// agent needs to understand prior decisions.
    /// </summary>
    private static ExportedSessionMessage? TryReadMessage(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return null;
        // A single malformed line skips that line, not the whole export — the rest of the
        // transcript is still worth carrying.
        using var document = TryParse(line);
        if (document is null)
            return null;
        var entry = document.RootElement;
        if (entry.ValueKind is not JsonValueKind.Object)
            return null;
        if (entry.TryGetProperty("type", out var type) is false ||
            type.GetString() is not ("user" or "assistant") ||
            (entry.TryGetProperty("isSidechain", out var sidechain) &&
                sidechain.ValueKind is JsonValueKind.True) ||
            (entry.TryGetProperty("isMeta", out var meta) &&
                meta.ValueKind is JsonValueKind.True) ||
            !entry.TryGetProperty("message", out var message) ||
            message.ValueKind is not JsonValueKind.Object ||
            !message.TryGetProperty("content", out var content))
            return null;

        var text = content.ValueKind switch
        {
            JsonValueKind.String => content.GetString(),
            JsonValueKind.Array => string.Join("\n\n", content.EnumerateArray()
                .Where(block => block.ValueKind is JsonValueKind.Object &&
                    block.TryGetProperty("type", out var blockType) &&
                    blockType.GetString() == "text" &&
                    block.TryGetProperty("text", out _))
                .Select(block => block.GetProperty("text").GetString())
                .Where(value => !string.IsNullOrWhiteSpace(value))),
            _ => null
        };
        return string.IsNullOrWhiteSpace(text)
            ? null
            : new ExportedSessionMessage(type.GetString()!, text);
    }

    private static JsonDocument? TryParse(string line) => SessionTranscripts.TryParse(line);
}

/// <summary>Shared guards for exporters that read vendor session files.</summary>
internal static class SessionTranscripts
{
    /// <summary>Session files are named by the vendor's session/thread UUID; anything else is
    /// rejected before it can reach the filesystem as a path segment.</summary>
    public static bool IsSafeSessionId(string sessionId) =>
        !string.IsNullOrWhiteSpace(sessionId) &&
        sessionId.All(character => char.IsAsciiLetterOrDigit(character) || character == '-');

    public static JsonDocument? TryParse(string line)
    {
        try
        {
            return JsonDocument.Parse(line);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>
/// Reads codex's local session store: one rollout JSONL file per thread under date-nested
/// directories, named <c>rollout-&lt;timestamp&gt;-&lt;threadId&gt;.jsonl</c>. Found by a
/// recursive name scan on the recorded thread id rather than reconstructing the date path, which
/// depends on when the vendor created the file. Each line wraps a payload in an envelope; the
/// conversation lives in <c>response_item</c> message payloads, with <c>event_msg</c>
/// user/agent messages as an older mirror of the same content — so the mirror is used only when
/// no response items were found, never alongside them.
/// </summary>
public sealed class CodexSessionExporter(string? sessionsRoot = null) : IAgentSessionExporter
{
    private const long MaxTranscriptBytes = 64 * 1024 * 1024;

    public string Agent => "codex";

    public async Task<SessionExportResult> ExportAsync(
        string sessionId, CancellationToken cancellationToken)
    {
        if (!SessionTranscripts.IsSafeSessionId(sessionId))
            return SessionExportResult.NotAvailable(
                "The recorded codex thread ID is not a valid session file name.");

        var root = sessionsRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".codex", "sessions");
        if (!Directory.Exists(root))
            return SessionExportResult.NotAvailable(
                "No local codex session store was found on this host.");

        string? rollout;
        try
        {
            // The newest matching file wins: a resumed thread can leave more than one rollout,
            // and the timestamp-prefixed names sort chronologically.
            rollout = Directory.EnumerateFiles(
                    root, $"rollout-*-{sessionId}.jsonl", SearchOption.AllDirectories)
                .OrderBy(Path.GetFileName, StringComparer.Ordinal)
                .LastOrDefault();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return SessionExportResult.NotAvailable(
                "The local codex session store could not be read.");
        }

        if (rollout is null)
            return SessionExportResult.NotAvailable(
                $"No rollout for thread '{sessionId}' exists in the local codex session store.");

        try
        {
            if (new FileInfo(rollout).Length > MaxTranscriptBytes)
                return SessionExportResult.NotAvailable(
                    "The codex rollout is larger than the export limit.");
            var messages = new List<ExportedSessionMessage>();
            var mirrored = new List<ExportedSessionMessage>();
            await foreach (var line in File.ReadLinesAsync(rollout, cancellationToken))
                ReadLine(line, messages, mirrored);
            return SessionExportResult.From(
                "codex-local-rollout", messages.Count > 0 ? messages : mirrored);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return SessionExportResult.NotAvailable(
                $"The rollout for thread '{sessionId}' could not be read.");
        }
    }

    private static void ReadLine(
        string line,
        List<ExportedSessionMessage> messages,
        List<ExportedSessionMessage> mirrored)
    {
        if (string.IsNullOrWhiteSpace(line))
            return;
        using var document = SessionTranscripts.TryParse(line);
        if (document is null ||
            document.RootElement.ValueKind is not JsonValueKind.Object ||
            !document.RootElement.TryGetProperty("type", out var envelope) ||
            !document.RootElement.TryGetProperty("payload", out var payload) ||
            payload.ValueKind is not JsonValueKind.Object)
            return;

        switch (envelope.GetString())
        {
            case "response_item" when TryReadResponseMessage(payload) is { } message:
                messages.Add(message);
                break;
            case "event_msg" when TryReadEventMessage(payload) is { } message:
                mirrored.Add(message);
                break;
            default:
                break;
        }
    }

    /// <summary>
    /// A conversation message, or null for everything else. Reasoning, tool calls and outputs,
    /// and developer-role instruction payloads are session mechanics, not the dialogue a target
    /// agent needs; injected environment/instruction wrappers on the user role are launch
    /// scaffolding rather than something a person or caller said.
    /// </summary>
    private static ExportedSessionMessage? TryReadResponseMessage(JsonElement payload)
    {
        if (!payload.TryGetProperty("type", out var type) ||
            type.GetString() != "message" ||
            !payload.TryGetProperty("role", out var roleValue) ||
            roleValue.GetString() is not ("user" or "assistant") ||
            !payload.TryGetProperty("content", out var content) ||
            content.ValueKind is not JsonValueKind.Array)
            return null;

        var role = roleValue.GetString()!;
        var text = string.Join("\n\n", content.EnumerateArray()
            .Where(block => block.ValueKind is JsonValueKind.Object &&
                block.TryGetProperty("type", out var blockType) &&
                blockType.GetString() is "input_text" or "output_text" or "text" &&
                block.TryGetProperty("text", out _))
            .Select(block => block.GetProperty("text").GetString())
            .Where(value => !string.IsNullOrWhiteSpace(value)));
        if (string.IsNullOrWhiteSpace(text) ||
            (role == "user" && IsLaunchScaffolding(text)))
            return null;
        return new ExportedSessionMessage(role, text);
    }

    private static ExportedSessionMessage? TryReadEventMessage(JsonElement payload)
    {
        if (!payload.TryGetProperty("type", out var type))
            return null;
        var role = type.GetString() switch
        {
            "user_message" => "user",
            "agent_message" => "assistant",
            _ => null
        };
        if (role is null ||
            !payload.TryGetProperty("message", out var message) ||
            message.GetString() is not { } text ||
            string.IsNullOrWhiteSpace(text) ||
            (role == "user" && IsLaunchScaffolding(text)))
            return null;
        return new ExportedSessionMessage(role, text);
    }

    /// <summary>
    /// Codex injects launch context — environment, instruction files, plugin listings — as
    /// ordinary user-role messages, and the wrapper tags vary by vendor version. What they share
    /// is shape: the message opens with a bare <c>&lt;tag&gt;</c> line. A person's or caller's
    /// prompt starting that way is unlikely enough that the shape, not a tag whitelist, is the
    /// filter — a whitelist would silently readmit the next version's new wrapper.
    /// </summary>
    private static bool IsLaunchScaffolding(string text)
    {
        if (text.Length == 0 || text[0] != '<')
            return false;
        var firstLineEnd = text.IndexOf('\n');
        var firstLine = (firstLineEnd < 0 ? text : text[..firstLineEnd]).TrimEnd('\r').Trim();
        return firstLine.Length > 2 &&
            firstLine[0] == '<' &&
            firstLine[^1] == '>' &&
            firstLine[1..^1].All(character =>
                char.IsAsciiLetterOrDigit(character) || character is '_' or '-' or ' ');
    }
}

/// <summary>
/// Reads the Markdown session export a worker-owned copilot session was asked to write with
/// <c>--share</c> — copilot's supported export surface; its own transcript store is a private
/// database. One file per session handle in the machine-local cache, written by the vendor when
/// the session ends. Sessions that predate share-request support (or that died before writing the
/// export) have no file, which degrades to the workspace-only packet.
/// </summary>
public sealed class CopilotSessionExporter(string? sharesRoot = null) : IAgentSessionExporter
{
    private const long MaxShareBytes = 64 * 1024 * 1024;

    public string Agent => "copilot";

    public async Task<SessionExportResult> ExportAsync(
        string sessionId, CancellationToken cancellationToken)
    {
        if (!SessionTranscripts.IsSafeSessionId(sessionId))
            return SessionExportResult.NotAvailable(
                "The recorded copilot session ID is not a valid session file name.");

        var root = sharesRoot ?? new Caching.CachePaths().CopilotSharesRoot;
        // The export is requested under Wrighty's session handle, but copilot then reports its
        // own session UUID and that UUID is what the resume address records — so the file name
        // rarely matches the recorded id directly. The export's own metadata note carries the
        // real session id, so the fallback is to find the file that says it is this session.
        var share = Path.Combine(root, sessionId + ".md");
        if (!File.Exists(share))
            share = await FindShareBySessionIdAsync(root, sessionId, cancellationToken);
        if (share is null)
            return SessionExportResult.NotAvailable(
                $"No session export for '{sessionId}' exists in the local share cache; copilot " +
                "writes one only for sessions started after export requests were enabled, and " +
                "only when the session ended normally.");

        try
        {
            if (new FileInfo(share).Length > MaxShareBytes)
                return SessionExportResult.NotAvailable(
                    "The copilot session export is larger than the export limit.");
            var messages = ParseShare(
                await File.ReadAllLinesAsync(share, cancellationToken));
            return SessionExportResult.From("copilot-share-export", messages);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return SessionExportResult.NotAvailable(
                $"The session export for '{sessionId}' could not be read.");
        }
    }

    /// <summary>The newest export whose metadata note names this session. Only the head of each
    /// file is read — the note is the first thing copilot writes.</summary>
    private static async Task<string?> FindShareBySessionIdAsync(
        string root, string sessionId, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(root))
            return null;
        var marker = $"`{sessionId}`";
        try
        {
            foreach (var candidate in Directory.EnumerateFiles(root, "*.md")
                         .OrderByDescending(File.GetLastWriteTimeUtc))
            {
                var head = 0;
                await foreach (var line in File.ReadLinesAsync(candidate, cancellationToken))
                {
                    if (line.Contains(marker, StringComparison.Ordinal))
                        return candidate;
                    if (++head >= 10)
                        break;
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        return null;
    }

    /// <summary>
    /// The share format alternates <c>### User</c> and <c>### Copilot</c> headings separated by
    /// <c>---</c> rules, with <c>&lt;sub&gt;</c> timing lines and a quoted metadata note around
    /// them. Only the headed sections are conversation; everything else is presentation.
    /// </summary>
    private static List<ExportedSessionMessage> ParseShare(IReadOnlyList<string> lines)
    {
        var messages = new List<ExportedSessionMessage>();
        string? role = null;
        var text = new StringBuilder();
        void Flush()
        {
            var value = text.ToString().Trim();
            if (role is not null && value.Length > 0)
                messages.Add(new ExportedSessionMessage(role, value));
            text.Clear();
        }

        foreach (var raw in lines)
        {
            var line = raw.TrimEnd();
            if (line.StartsWith("### ", StringComparison.Ordinal))
            {
                Flush();
                role = line[4..].Trim() switch
                {
                    "User" => "user",
                    "Copilot" => "assistant",
                    _ => null
                };
                continue;
            }

            if (line == "---")
            {
                Flush();
                role = null;
                continue;
            }

            if (role is null ||
                line.StartsWith("<sub>", StringComparison.Ordinal) ||
                line.StartsWith(">", StringComparison.Ordinal))
                continue;
            text.AppendLine(raw);
        }

        Flush();
        return messages;
    }
}
