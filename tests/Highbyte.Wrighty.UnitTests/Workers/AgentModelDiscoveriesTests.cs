using Highbyte.Wrighty.Workers;

namespace Highbyte.Wrighty.UnitTests.Workers;

public sealed class AgentModelDiscoveriesTests
{
    [Fact]
    public async Task Canceled_discovery_is_not_cached()
    {
        var adapter = new CancelOnceDiscovery();
        var discoveries = new AgentModelDiscoveries([adapter]);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => discoveries.DiscoverAsync("codex", CancellationToken.None));

        var catalog = await discoveries.DiscoverAsync("codex", CancellationToken.None);

        Assert.Equal(ModelDiscoveryFailure.None, catalog.Failure);
        Assert.Equal(2, adapter.Attempts);
    }

    [Fact]
    public async Task Simulated_missing_agent_suppresses_vendor_model_discovery()
    {
        var adapter = new RecordingDiscovery();
        var discoveries = new AgentModelDiscoveries(
            _ => adapter,
            new FixedRuntimeCatalog(installed: false));

        var catalog = await discoveries.DiscoverAsync("codex", CancellationToken.None);

        Assert.Equal(ModelDiscoveryFailure.NotInstalled, catalog.Failure);
        Assert.Equal(0, adapter.Attempts);
    }

    private sealed class CancelOnceDiscovery : IAgentModelDiscovery
    {
        public string Agent => "codex";

        public int Attempts { get; private set; }

        public Task<AgentModelCatalog> DiscoverAsync(CancellationToken cancellationToken)
        {
            Attempts++;
            if (Attempts == 1)
            {
                return Task.FromCanceled<AgentModelCatalog>(new CancellationToken(canceled: true));
            }

            return Task.FromResult(new AgentModelCatalog(
                Agent,
                [new AgentModel("gpt-test")],
                CurrentModelId: "gpt-test"));
        }
    }

    private sealed class RecordingDiscovery : IAgentModelDiscovery
    {
        public string Agent => "codex";
        public int Attempts { get; private set; }

        public Task<AgentModelCatalog> DiscoverAsync(CancellationToken cancellationToken)
        {
            Attempts++;
            return Task.FromResult(new AgentModelCatalog(Agent, [new AgentModel("test")]));
        }
    }

    private sealed class FixedRuntimeCatalog(bool installed) : IAgentRuntimeCatalog
    {
        public AgentRuntimeSnapshot Snapshot() => new(
        [
            new AgentRuntime(
                "codex",
                "codex",
                Supported: true,
                installed ? AgentInstallationState.Installed : AgentInstallationState.Missing,
                installed ? "/tools/codex" : null,
                InstallationSimulated: !installed)
        ]);
    }
}
