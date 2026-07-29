using Highbyte.Wrighty.Processes;
using Highbyte.Wrighty.Workers;

namespace Highbyte.Wrighty.UnitTests.Workers;

public sealed class AgentRuntimeCatalogTests
{
    [Fact]
    public void Registered_adapters_define_the_supported_set_and_canonical_order()
    {
        var resolver = new RecordingResolver(["codex", "claude"]);
        var catalog = new AgentRuntimeCatalog(
            [new CopilotAgentAdapter(), new ClaudeAgentAdapter(), new CodexAgentAdapter()],
            resolver);

        var snapshot = catalog.Snapshot();

        Assert.Equal(["claude", "codex", "copilot"], snapshot.Agents.Select(value => value.Agent));
        Assert.Equal(["claude", "codex"], snapshot.InstalledAgents.Select(value => value.Agent));
        Assert.Equal(AgentInstallationState.Missing, snapshot.Find("copilot")!.InstallationState);
        Assert.All(snapshot.Agents, value => Assert.True(value.Supported));
        Assert.All(snapshot.Agents, value => Assert.Equal(AgentReadinessState.Unknown, value.Readiness));
        Assert.Equal(["claude", "codex", "copilot"], resolver.Lookups.Order());
    }

    [Fact]
    public void Every_snapshot_refreshes_previously_missing_executables()
    {
        var resolver = new RecordingResolver([]);
        var catalog = new AgentRuntimeCatalog([new CodexAgentAdapter()], resolver);

        Assert.False(catalog.Snapshot().AnyInstalled);
        resolver.Installed.Add("codex");

        Assert.True(catalog.Snapshot().IsInstalled("codex"));
        Assert.Equal(2, resolver.Lookups.Count);
    }

    private sealed class RecordingResolver(IEnumerable<string> installed) : IExecutableResolver
    {
        public HashSet<string> Installed { get; } =
            new(installed, StringComparer.OrdinalIgnoreCase);

        public List<string> Lookups { get; } = [];

        public string Resolve(string executableName) =>
            TryResolve(executableName, out var path)
                ? path!
                : throw new FileNotFoundException("missing", executableName);

        public bool TryResolve(string executableName, out string? executablePath)
        {
            Lookups.Add(executableName);
            executablePath = Installed.Contains(executableName)
                ? $"/tools/{executableName}"
                : null;
            return executablePath is not null;
        }
    }
}
