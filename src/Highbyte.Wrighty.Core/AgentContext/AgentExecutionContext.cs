namespace Highbyte.Wrighty.AgentContext;

public sealed record AgentExecutionContext(
    string? AgentType,
    string? SessionId,
    AgentContextSource Source,
    string? Warning = null)
{
    public static AgentExecutionContext None { get; } =
        new(null, null, AgentContextSource.None);
}

public sealed record AgentContextInput(
    string? AgentType = null,
    string? SessionId = null,
    bool Disabled = false);

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
