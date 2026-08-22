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
}
