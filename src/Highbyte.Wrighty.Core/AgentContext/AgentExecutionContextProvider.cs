using Highbyte.Wrighty.Errors;

namespace Highbyte.Wrighty.AgentContext;

public sealed class AgentExecutionContextProvider(
    IReadOnlyDictionary<string, string?> environment) : IAgentExecutionContextProvider
{
    private static readonly HashSet<string> SupportedAgentTypes =
        new(StringComparer.Ordinal) { "codex", "claude", "copilot", "other" };

    public AgentExecutionContext Resolve(AgentContextInput input)
    {
        if (input.Disabled || IsTrue(Get("WRIGHTY_NO_AGENT_CONTEXT")))
        {
            return AgentExecutionContext.None;
        }

        var explicitAgentType = NormalizeAgentType(input.AgentType, "--agent-type");
        var explicitSessionId = ValidateSessionId(input.SessionId, "--session-id");
        var trackerAgentType = explicitAgentType is null
            ? NormalizeAgentType(Get("WRIGHTY_AGENT_TYPE"), "WRIGHTY_AGENT_TYPE")
            : null;
        var trackerSessionId = explicitSessionId is null
            ? ValidateSessionId(Get("WRIGHTY_SESSION_ID"), "WRIGHTY_SESSION_ID")
            : null;

        var agentType = explicitAgentType ?? trackerAgentType;
        var sessionId = explicitSessionId ?? trackerSessionId;
        var source = explicitAgentType is not null || explicitSessionId is not null
            ? AgentContextSource.ExplicitOption
            : trackerAgentType is not null || trackerSessionId is not null
                ? AgentContextSource.TrackerEnvironment
                : AgentContextSource.None;

        var detection = agentType is null || sessionId is null
            ? DetectVendorContext()
            : AgentExecutionContext.None;
        if (agentType is null && detection.Warning is null)
        {
            agentType = detection.AgentType;
        }

        if (sessionId is null && detection.Warning is null)
        {
            sessionId = detection.SessionId;
        }

        if (source == AgentContextSource.None && (agentType is not null || sessionId is not null))
        {
            source = AgentContextSource.VendorEnvironment;
        }

        return new AgentExecutionContext(agentType, sessionId, source, detection.Warning);
    }

    private AgentExecutionContext DetectVendorContext()
    {
        var codex = Vendor("codex", Get("CODEX_THREAD_ID"));
        var claude = Vendor(
            "claude",
            FirstNonEmpty(Get("CLAUDE_CODE_REMOTE_SESSION_ID"), Get("CLAUDE_CODE_SESSION_ID")));
        var copilot = Vendor("copilot", Get("COPILOT_AGENT_SESSION_ID"));
        var strong = new[] { codex, claude, copilot }
            .Where(context => context is not null)
            .Cast<AgentExecutionContext>()
            .ToArray();

        if (strong.Length > 1)
        {
            return new AgentExecutionContext(
                null,
                null,
                AgentContextSource.None,
                "Conflicting agent runtime signals were found; use --agent-type and --session-id to identify the active session.");
        }

        if (strong.Length == 1)
        {
            return strong[0];
        }

        if (string.Equals(Get("CLAUDECODE"), "1", StringComparison.Ordinal) ||
            IsTrue(Get("CLAUDE_CODE_REMOTE")))
        {
            return new AgentExecutionContext(
                "claude",
                null,
                AgentContextSource.VendorEnvironment);
        }

        return AgentExecutionContext.None;
    }

    private AgentExecutionContext? Vendor(string agentType, string? sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return null;
        }

        try
        {
            return new AgentExecutionContext(
                agentType,
                ValidateSessionId(sessionId, agentType),
                AgentContextSource.VendorEnvironment);
        }
        catch (TrackerException)
        {
            return new AgentExecutionContext(
                null,
                null,
                AgentContextSource.None,
                $"The {agentType} session identifier is invalid and will not be published.");
        }
    }

    private string? Get(string name) =>
        environment.TryGetValue(name, out var value) ? value : null;

    private static string? NormalizeAgentType(string? value, string source)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.Trim().ToLowerInvariant();
        if (!SupportedAgentTypes.Contains(normalized))
        {
            throw new TrackerException(
                "ARGUMENT_INVALID",
                $"{source} must be one of: codex, claude, copilot, other.",
                2);
        }

        return normalized;
    }

    private static string? ValidateSessionId(string? value, string source)
    {
        if (value is null)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(value) ||
            value.Length > 200 ||
            value.Any(char.IsControl) ||
            value.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            throw new TrackerException(
                "ARGUMENT_INVALID",
                $"{source} must be an opaque 1-200 character identifier without control characters or a URL.",
                2);
        }

        return value;
    }

    private static string? FirstNonEmpty(string? preferred, string? fallback) =>
        !string.IsNullOrWhiteSpace(preferred) ? preferred : fallback;

    private static bool IsTrue(string? value) =>
        string.Equals(value, "1", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(value, "on", StringComparison.OrdinalIgnoreCase);
}
