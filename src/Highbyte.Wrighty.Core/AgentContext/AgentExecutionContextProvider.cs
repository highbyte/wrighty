using Highbyte.Wrighty.Errors;

namespace Highbyte.Wrighty.AgentContext;

public sealed class AgentExecutionContextProvider(
    IReadOnlyDictionary<string, string?> environment) : IAgentExecutionContextProvider
{
    private static readonly HashSet<string> SupportedAgentTypes =
        new(StringComparer.Ordinal) { "codex", "claude", "copilot", "other" };

    public AgentExecutionContext Resolve(AgentContextInput input)
    {
        if (input.Disabled || IsTrue(Get("WRIGHTY_NO_CLAIMANT_CONTEXT")) || IsTrue(Get("WRIGHTY_NO_AGENT_CONTEXT")))
        {
            return AgentExecutionContext.None;
        }

        var explicitKind = NormalizeClaimantKind(input.ClaimantKind, "--claimant-kind");
        var environmentKind = explicitKind is null
            ? NormalizeClaimantKind(Get("WRIGHTY_CLAIMANT_KIND"), "WRIGHTY_CLAIMANT_KIND")
            : null;
        var configuredKind = explicitKind ?? environmentKind;
        var claimantId = ValidateClaimantId(
            input.ClaimantId ?? Get("WRIGHTY_CLAIMANT_ID"),
            input.ClaimantId is null ? "WRIGHTY_CLAIMANT_ID" : "--claimant-id");
        var claimToken = ValidateClaimantId(
            input.ClaimToken ?? Get("WRIGHTY_CLAIM_TOKEN"),
            input.ClaimToken is null ? "WRIGHTY_CLAIM_TOKEN" : "--claim-token");
        var configured = ResolveConfiguredContext(input);
        if (configuredKind is ClaimantKind.Human or ClaimantKind.Automation or ClaimantKind.Unknown)
        {
            var result = ResolveNonAgentContext(configuredKind.Value, configured, explicitKind is not null);
            if (configuredKind == ClaimantKind.Automation && claimantId is null)
            {
                throw new TrackerException("ARGUMENT_INVALID", "Automation requires --claimant-id or WRIGHTY_CLAIMANT_ID.", 2);
            }
            return result with { ClaimantId = claimantId ?? (configuredKind == ClaimantKind.Human ? "human-cli" : null), ClaimToken = claimToken };
        }

        var detection = NeedsVendorDetection(configured)
            ? DetectVendorContext()
            : AgentExecutionContext.None;
        var merged = MergeContext(configured, detection);
        if (configuredKind == ClaimantKind.Agent)
        {
            var agent = ResolveAgentContext(merged, explicitKind is not null);
            return agent with { ClaimantId = claimantId ?? AgentClaimantId(agent), ClaimToken = claimToken };
        }

        if (merged.AgentType is not null || merged.SessionId is not null)
        {
            var agent = merged with { ClaimantKind = ClaimantKind.Agent };
            return agent with { ClaimantId = claimantId ?? AgentClaimantId(agent), ClaimToken = claimToken };
        }

        return merged.Warning is null
            ? AgentExecutionContext.Human with { ClaimantId = claimantId ?? "human-cli", ClaimToken = claimToken }
            : merged with { ClaimantKind = ClaimantKind.Unknown, ClaimantId = claimantId, ClaimToken = claimToken };
    }

    private static string? AgentClaimantId(AgentExecutionContext context) =>
        context.SessionId is null ? null : $"{context.AgentType ?? "agent"}:{context.SessionId}";

    private static AgentExecutionContext ResolveNonAgentContext(
        ClaimantKind configuredKind,
        ConfiguredContext configured,
        bool isExplicit)
    {
        if (configured.AgentType is not null || configured.SessionId is not null)
        {
            throw new TrackerException(
                "ARGUMENT_INVALID",
                "Agent type or session metadata can only be used when claimant kind is agent.",
                2);
        }

        return new AgentExecutionContext(
            null,
            null,
            isExplicit ? AgentContextSource.ExplicitOption : AgentContextSource.TrackerEnvironment,
            ClaimantKind: configuredKind);
    }

    private static AgentExecutionContext ResolveAgentContext(
        AgentExecutionContext merged,
        bool isExplicit)
    {
        var source = merged.Source;
        if (isExplicit)
        {
            source = AgentContextSource.ExplicitOption;
        }
        else if (source == AgentContextSource.None)
        {
            source = AgentContextSource.TrackerEnvironment;
        }

        return merged with
        {
            AgentType = merged.AgentType ?? "other",
            ClaimantKind = ClaimantKind.Agent,
            Source = source
        };
    }

    private ConfiguredContext ResolveConfiguredContext(AgentContextInput input)
    {
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
        return new ConfiguredContext(
            agentType,
            sessionId,
            ResolveConfiguredSource(
                explicitAgentType,
                explicitSessionId,
                trackerAgentType,
                trackerSessionId));
    }

    private static AgentContextSource ResolveConfiguredSource(
        string? explicitAgentType,
        string? explicitSessionId,
        string? trackerAgentType,
        string? trackerSessionId) =>
        explicitAgentType is not null || explicitSessionId is not null
            ? AgentContextSource.ExplicitOption
            : trackerAgentType is not null || trackerSessionId is not null
                ? AgentContextSource.TrackerEnvironment
                : AgentContextSource.None;

    private static bool NeedsVendorDetection(ConfiguredContext configured) =>
        configured.AgentType is null || configured.SessionId is null;

    private static AgentExecutionContext MergeContext(
        ConfiguredContext configured,
        AgentExecutionContext detection)
    {
        var useDetection = detection.Warning is null;
        var agentType = configured.AgentType ?? (useDetection ? detection.AgentType : null);
        var sessionId = configured.SessionId ?? (useDetection ? detection.SessionId : null);
        var source = configured.Source == AgentContextSource.None &&
                     (agentType is not null || sessionId is not null)
            ? AgentContextSource.VendorEnvironment
            : configured.Source;
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

    private static ClaimantKind? NormalizeClaimantKind(string? value, string source)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value.Trim().ToLowerInvariant() switch
        {
            "agent" => ClaimantKind.Agent,
            "human" => ClaimantKind.Human,
            "automation" => ClaimantKind.Automation,
            "unknown" => ClaimantKind.Unknown,
            _ => throw new TrackerException(
                "ARGUMENT_INVALID",
                $"{source} must be one of: agent, human, automation, unknown.",
                2)
        };
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

    private static string? ValidateClaimantId(string? value, string source)
    {
        if (value is null) return null;
        if (string.IsNullOrWhiteSpace(value) || value.Length > 200 || value.Any(char.IsControl))
        {
            throw new TrackerException("ARGUMENT_INVALID", $"{source} must be an opaque 1-200 character identifier without control characters.", 2);
        }
        return value.Trim();
    }

    private static string? FirstNonEmpty(string? preferred, string? fallback) =>
        !string.IsNullOrWhiteSpace(preferred) ? preferred : fallback;

    private static bool IsTrue(string? value) =>
        string.Equals(value, "1", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(value, "on", StringComparison.OrdinalIgnoreCase);

    private sealed record ConfiguredContext(
        string? AgentType,
        string? SessionId,
        AgentContextSource Source);
}
