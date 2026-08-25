using Highbyte.Wrighty.Configuration;
using Highbyte.Wrighty.Workers;

namespace Highbyte.Wrighty.UnitTests.Workers;

public sealed class ProviderCapacitySimulationTests : IDisposable
{
    private readonly string directory = Path.Combine(
        Path.GetTempPath(), $"wrighty-capacity-simulation-{Guid.NewGuid():N}");

    [Fact]
    public async Task Repository_simulation_is_reloaded_and_identified_without_persistence()
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, TrackerConfigLoader.FileName);
        var store = new TrackerConfigLoader();
        var config = Configuration(path, "available", 0);
        await store.SaveAsync(path, config, CancellationToken.None);
        var simulator = new RepositoryConfigurationProviderCapacitySimulator(store);
        var observedAt = new DateTimeOffset(2026, 8, 25, 12, 0, 0, TimeSpan.Zero);

        var available = await simulator.TryCreateAsync(
            config, "codex", observedAt, CancellationToken.None);

        Assert.NotNull(available);
        Assert.True(available.Simulated);
        Assert.Equal(ProviderCapacityState.Available, available.State);

        await store.SaveAsync(
            path,
            Configuration(path, "rate-limited", 45),
            CancellationToken.None);
        var unavailable = await simulator.TryCreateAsync(
            config, "codex", observedAt, CancellationToken.None);

        Assert.NotNull(unavailable);
        Assert.True(unavailable.Simulated);
        Assert.Equal(ProviderCapacityState.UnavailableUntil, unavailable.State);
        Assert.Equal(observedAt.AddSeconds(45), unavailable.UnavailableUntil);
        Assert.Contains("simulated rate limited", unavailable.Reason);
    }

    private static TrackerConfig Configuration(
        string path,
        string result,
        double retryAfterSeconds) =>
        new()
        {
            Backend = "local-markdown",
            SourcePath = path,
            LocalMarkdown = new LocalMarkdownBackendConfig(),
            Testing = new TestingConfig
            {
                CapacityProbes = new Dictionary<string, ProviderCapacitySimulation>
                {
                    ["codex"] = new(result, retryAfterSeconds)
                }
            }
        };

    public void Dispose()
    {
        if (Directory.Exists(directory))
            Directory.Delete(directory, true);
    }
}
