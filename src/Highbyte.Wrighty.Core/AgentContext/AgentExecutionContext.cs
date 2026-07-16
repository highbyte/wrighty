namespace Highbyte.Wrighty.AgentContext;

public sealed record AgentExecutionContext(
    string? AgentType,
    string? SessionId,
    AgentContextSource Source,
    string? Warning = null,
    ClaimantKind ClaimantKind = ClaimantKind.Unknown)
{
    public ClaimantKind EffectiveClaimantKind =>
        ClaimantKind == ClaimantKind.Unknown && ClaimantKinds.IsLegacyAgentType(AgentType)
            ? ClaimantKind.Agent
            : ClaimantKind;

    public static AgentExecutionContext None { get; } =
        new(null, null, AgentContextSource.None);

    public static AgentExecutionContext Human { get; } =
        new(null, null, AgentContextSource.None, ClaimantKind: ClaimantKind.Human);
}

public sealed record AgentContextInput(
    string? AgentType = null,
    string? SessionId = null,
    bool Disabled = false,
    string? ClaimantKind = null);

public enum ClaimantKind
{
    Unknown,
    Agent,
    Human,
    Automation
}

public static class ClaimantKinds
{
    public static string ToStorageValue(ClaimantKind kind) => kind switch
    {
        ClaimantKind.Agent => "agent",
        ClaimantKind.Human => "human",
        ClaimantKind.Automation => "automation",
        _ => "unknown"
    };

    public static ClaimantKind FromStorageValue(string? value, string? legacyAgentType = null) =>
        value?.Trim().ToLowerInvariant() switch
        {
            "agent" => ClaimantKind.Agent,
            "human" => ClaimantKind.Human,
            "automation" => ClaimantKind.Automation,
            "unknown" => ClaimantKind.Unknown,
            null or "" when IsLegacyAgentType(legacyAgentType) => ClaimantKind.Agent,
            _ => ClaimantKind.Unknown
        };

    public static bool IsLegacyAgentType(string? value) =>
        value?.Trim().ToLowerInvariant() is "codex" or "claude" or "copilot" or "other";
}

public enum AgentContextSource
{
    None,
    ExplicitOption,
    TrackerEnvironment,
    VendorEnvironment
}

public interface IAgentExecutionContextProvider
{
    AgentExecutionContext Resolve(AgentContextInput input);
}
