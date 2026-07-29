using Highbyte.Wrighty.Processes;

namespace Highbyte.Wrighty.Workers;

public enum AgentInstallationState
{
    Installed,
    Missing
}

public enum AgentReadinessState
{
    Unknown,
    Ready,
    Failed
}

public sealed record AgentRuntime(
    string Agent,
    string ExecutableName,
    bool Supported,
    AgentInstallationState InstallationState,
    string? ExecutablePath,
    AgentReadinessState Readiness = AgentReadinessState.Unknown)
{
    public bool Installed => InstallationState == AgentInstallationState.Installed;
}

public sealed class AgentRuntimeSnapshot
{
    private readonly Dictionary<string, AgentRuntime> agentsByName;

    public AgentRuntimeSnapshot(IEnumerable<AgentRuntime> agents)
    {
        Agents = agents
            .OrderBy(runtime => runtime.Agent, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        InstalledAgents = Agents
            .Where(runtime => runtime.Installed)
            .ToArray();
        agentsByName = Agents.ToDictionary(
            runtime => runtime.Agent,
            StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<AgentRuntime> Agents { get; }

    public IReadOnlyList<AgentRuntime> InstalledAgents { get; }

    public bool AnyInstalled => Agents.Any(runtime => runtime.Installed);

    public bool IsInstalled(string agent) =>
        agentsByName.TryGetValue(agent, out var runtime) && runtime.Installed;

    public AgentRuntime? Find(string agent) =>
        agentsByName.GetValueOrDefault(agent);
}

public interface IAgentRuntimeCatalog
{
    AgentRuntimeSnapshot Snapshot();
}

public sealed class AgentRuntimeCatalog(
    IEnumerable<IAgentAdapter> adapters,
    IExecutableResolver executables) : IAgentRuntimeCatalog
{
    private readonly IAgentAdapter[] registeredAdapters = adapters
        .OrderBy(adapter => adapter.Agent, StringComparer.OrdinalIgnoreCase)
        .ToArray();

    public AgentRuntimeSnapshot Snapshot() => new(registeredAdapters.Select(adapter =>
    {
        var installed = executables.TryResolve(adapter.ExecutableName, out var path);
        return new AgentRuntime(
            adapter.Agent.ToLowerInvariant(),
            adapter.ExecutableName,
            Supported: true,
            installed ? AgentInstallationState.Installed : AgentInstallationState.Missing,
            path);
    }));
}

internal sealed class AssumeInstalledAgentRuntimeCatalog(
    IEnumerable<IAgentAdapter> adapters) : IAgentRuntimeCatalog
{
    private readonly AgentRuntimeSnapshot snapshot = new(adapters.Select(adapter =>
        new AgentRuntime(
            adapter.Agent.ToLowerInvariant(),
            adapter.ExecutableName,
            Supported: true,
            AgentInstallationState.Installed,
            ExecutablePath: null)));

    public AgentRuntimeSnapshot Snapshot() => snapshot;
}
