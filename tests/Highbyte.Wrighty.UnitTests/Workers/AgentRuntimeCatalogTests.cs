using Highbyte.Wrighty.Processes;
using Highbyte.Wrighty.Configuration;
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

    [Fact]
    public async Task Repository_testing_can_hide_and_restore_an_installed_agent_without_restart()
    {
        var root = Path.Combine(Path.GetTempPath(), $"wrighty-runtime-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var store = new TrackerConfigLoader();
            var path = Path.Combine(root, TrackerConfigLoader.FileName);
            var physical = new AgentRuntimeCatalog(
                [new CodexAgentAdapter()], new RecordingResolver(["codex"]));
            var catalog = new TestingAgentRuntimeCatalog(physical, store, root);
            await store.SaveAsync(path, Configuration(["codex"]), CancellationToken.None);

            var hidden = catalog.Snapshot().Find("codex")!;

            Assert.False(hidden.Installed);
            Assert.True(hidden.InstallationSimulated);
            Assert.Null(hidden.ExecutablePath);

            await store.SaveAsync(path, Configuration([]), CancellationToken.None);

            var restored = catalog.Snapshot().Find("codex")!;
            Assert.True(restored.Installed);
            Assert.False(restored.InstallationSimulated);
            Assert.Equal("/tools/codex", restored.ExecutablePath);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static TrackerConfig Configuration(IReadOnlyList<string> notInstalled) => new()
    {
        Backend = "local-markdown",
        LocalMarkdown = new LocalMarkdownBackendConfig(),
        Testing = notInstalled.Count == 0
            ? null
            : new TestingConfig { NotInstalledAgents = notInstalled }
    };

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
