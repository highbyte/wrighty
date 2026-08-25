namespace Highbyte.Wrighty.AgentContext;

/// <summary>A registered agent-runtime signal before Wrighty validates its session identifier.</summary>
public sealed record AgentContextDetection(
    string Agent,
    string? SessionId,
    bool IsPresent);

/// <summary>Detects one agent's runtime context without deciding conflicts between agents.</summary>
public interface IAgentContextDetector
{
    string Agent { get; }

    AgentContextDetection Detect(IReadOnlyDictionary<string, string?> environment);
}

/// <summary>A presence-only environment signal and the values that enable it.</summary>
public sealed record AgentPresenceSignal(
    string Variable,
    IReadOnlyList<string> Values);

/// <summary>
/// Describes an agent runtime using ordered session variables and optional presence-only signals.
/// The first non-empty session variable wins.
/// </summary>
public sealed class EnvironmentAgentContextDetector : IAgentContextDetector
{
    private readonly IReadOnlyList<string> sessionVariables;
    private readonly IReadOnlyList<AgentPresenceSignal> presenceSignals;

    public EnvironmentAgentContextDetector(
        string agent,
        IEnumerable<string> sessionVariables,
        IEnumerable<AgentPresenceSignal>? presenceSignals = null)
    {
        if (string.IsNullOrWhiteSpace(agent))
            throw new ArgumentException("Agent id must be non-empty.", nameof(agent));
        ArgumentNullException.ThrowIfNull(sessionVariables);

        Agent = agent;
        this.sessionVariables = Array.AsReadOnly(sessionVariables.ToArray());
        this.presenceSignals = Array.AsReadOnly((presenceSignals ?? []).ToArray());
        if (this.sessionVariables.Any(string.IsNullOrWhiteSpace) ||
            this.presenceSignals.Any(signal =>
                string.IsNullOrWhiteSpace(signal.Variable) ||
                signal.Values.Count == 0))
        {
            throw new ArgumentException("Environment signal names and values must be non-empty.");
        }
    }

    public string Agent { get; }

    public AgentContextDetection Detect(IReadOnlyDictionary<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        var sessionId = sessionVariables
            .Select(variable => Get(environment, variable))
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
        var isPresent = sessionId is not null || presenceSignals.Any(signal =>
            signal.Values.Contains(
                Get(environment, signal.Variable),
                StringComparer.OrdinalIgnoreCase));
        return new AgentContextDetection(Agent, sessionId, isPresent);
    }

    private static string? Get(
        IReadOnlyDictionary<string, string?> environment,
        string name) =>
        environment.TryGetValue(name, out var value) ? value : null;
}
