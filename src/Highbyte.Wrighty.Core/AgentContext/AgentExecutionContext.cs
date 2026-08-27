namespace Highbyte.Wrighty.AgentContext;

public sealed record AgentExecutionContext(
    string? Agent,
    string? SessionId,
    AgentContextSource Source,
    string? Warning = null,
    ClaimantKind ClaimantKind = ClaimantKind.Unknown,
    string? ClaimantId = null,
    string? ClaimToken = null,
    string? ExecutionPhase = null)
{
    public ClaimantKind EffectiveClaimantKind => ClaimantKind;

    public static AgentExecutionContext None { get; } =
        new(null, null, AgentContextSource.None);

    public static AgentExecutionContext Human { get; } =
        new(null, null, AgentContextSource.None, ClaimantKind: ClaimantKind.Human, ClaimantId: "human-cli");
}

public sealed record AgentContextInput(
    string? Agent = null,
    string? SessionId = null,
    bool Disabled = false,
    string? ClaimantKind = null,
    string? ClaimantId = null,
    string? ClaimToken = null);

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

    public static ClaimantKind FromStorageValue(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            "agent" => ClaimantKind.Agent,
            "human" => ClaimantKind.Human,
            "automation" => ClaimantKind.Automation,
            "unknown" => ClaimantKind.Unknown,
            _ => ClaimantKind.Unknown
        };
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
