using Highbyte.Wrighty.Processes;
using Highbyte.Wrighty.Configuration;
using Highbyte.Wrighty.Errors;

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
    AgentReadinessState Readiness = AgentReadinessState.Unknown,
    bool InstallationSimulated = false)
{
    public bool Installed => InstallationState == AgentInstallationState.Installed;
}

/// <summary>
/// Applies repository testing overrides to the physical runtime catalogue. The underlying
/// executable lookup remains authoritative when no override is configured, and the repository
/// file is re-read after an atomic save so a web-console change takes effect without a restart.
/// </summary>
public sealed class TestingAgentRuntimeCatalog(
    IAgentRuntimeCatalog physical,
    ITrackerConfigStore configurations,
    string startDirectory) : IAgentRuntimeCatalog
{
    public AgentRuntimeSnapshot Snapshot()
    {
        var testing = ReadTesting();
        return new AgentRuntimeSnapshot(physical.Snapshot().Agents.Select(runtime =>
            testing.PretendsAgentIsNotInstalled(runtime.Agent)
                ? runtime with
                {
                    InstallationState = AgentInstallationState.Missing,
                    ExecutablePath = null,
                    Readiness = AgentReadinessState.Unknown,
                    InstallationSimulated = true
                }
                : runtime));
    }

    private TestingConfig ReadTesting()
    {
        var path = configurations.ResolvePath(startDirectory, explicitPath: null);
        if (!File.Exists(path))
            return new TestingConfig();
        try
        {
            return TrackerConfigLoader.DeserializeExact(
                File.ReadAllBytes(path), path).EffectiveTesting;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or System.Text.Json.JsonException
                or TrackerException)
        {
            // Configuration loading reports the real error on its normal path. Runtime discovery
            // must not turn a malformed test override into a false missing agent. Reading this
            // small file every time also avoids timestamp-resolution races after a same-size save.
            return new TestingConfig();
        }
    }
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
