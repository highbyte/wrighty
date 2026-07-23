using Highbyte.Wrighty.Caching;
using Highbyte.Wrighty.Errors;
using Highbyte.Wrighty.Workers;

namespace Highbyte.Wrighty.UnitTests.Caching;

public sealed class JsonProviderAvailabilityStoreTests : IDisposable
{
    private readonly string directory = Path.Combine(
        Path.GetTempPath(), $"wrighty-provider-cache-{Guid.NewGuid():N}");
    private static readonly DateTimeOffset ObservedAt =
        new(2026, 7, 23, 18, 0, 0, TimeSpan.Zero);

    private JsonProviderAvailabilityStore Store() =>
        new(new CachePaths(directory));

    [Fact]
    public async Task Open_persists_sanitized_capacity_state_across_instances()
    {
        await Store().OpenAsync(
            "Claude",
            "  Usage   limit reached for user@example.com api_key=secret-value.  ",
            ObservedAt.AddHours(2),
            AgentFailureConfidence.Authoritative,
            ObservedAt,
            CancellationToken.None);

        var availability = await Store().GetAsync("claude", CancellationToken.None);
        var json = await File.ReadAllTextAsync(
            new CachePaths(directory).ProviderAvailabilityPath);

        Assert.Equal(ProviderAvailabilityState.UnavailableUntil, availability?.State);
        Assert.Equal(
            "Usage limit reached for [redacted-email] api_key=[redacted]",
            availability?.Reason);
        Assert.Equal(ObservedAt.AddHours(2), availability?.UnavailableUntil);
        Assert.Equal(1, availability?.ConsecutiveFailures);
        Assert.Contains("\"state\": \"unavailable-until\"", json);
        Assert.DoesNotContain("subscription", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("user@example.com", json, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-value", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Only_one_store_instance_acquires_a_due_probe_lease()
    {
        var first = Store();
        var second = Store();
        await first.OpenAsync(
            "claude",
            "Usage limit reached.",
            ObservedAt,
            AgentFailureConfidence.Authoritative,
            ObservedAt.AddMinutes(-30),
            CancellationToken.None);

        var leases = await Task.WhenAll(
            first.TryAcquireProbeAsync(
                "claude", ObservedAt, TimeSpan.FromMinutes(2), CancellationToken.None),
            second.TryAcquireProbeAsync(
                "claude", ObservedAt, TimeSpan.FromMinutes(2), CancellationToken.None));

        Assert.Single(leases, lease => lease is not null);
        var availability = await Store().GetAsync("claude", CancellationToken.None);
        Assert.Equal(ProviderAvailabilityState.ProbeDue, availability?.State);
        Assert.Equal(ObservedAt.AddMinutes(2), availability?.UnavailableUntil);
    }

    [Fact]
    public async Task Expired_probe_can_be_reacquired_and_success_closes_the_circuit()
    {
        var store = Store();
        await store.OpenAsync(
            "codex",
            "Usage limit reached.",
            ObservedAt,
            AgentFailureConfidence.Inferred,
            ObservedAt.AddMinutes(-30),
            CancellationToken.None);
        var expired = await store.TryAcquireProbeAsync(
            "codex", ObservedAt, TimeSpan.FromMinutes(1), CancellationToken.None);

        var replacement = await store.TryAcquireProbeAsync(
            "codex", ObservedAt.AddMinutes(2), TimeSpan.FromMinutes(1), CancellationToken.None);
        await store.CloseAsync(
            "codex", ObservedAt.AddMinutes(3), CancellationToken.None);

        Assert.NotNull(expired);
        Assert.NotNull(replacement);
        Assert.NotEqual(expired!.LeaseId, replacement!.LeaseId);
        var availability = await store.GetAsync("codex", CancellationToken.None);
        Assert.Equal(ProviderAvailabilityState.Available, availability?.State);
        Assert.Equal(0, availability?.ConsecutiveFailures);
        Assert.Null(availability?.UnavailableUntil);
    }

    [Fact]
    public async Task Corrupt_state_fails_closed()
    {
        var paths = new CachePaths(directory);
        Directory.CreateDirectory(paths.Root);
        await File.WriteAllTextAsync(paths.ProviderAvailabilityPath, "{ not json");

        var error = await Assert.ThrowsAsync<TrackerException>(
            () => Store().GetAsync("claude", CancellationToken.None));

        Assert.Equal("PROVIDER_AVAILABILITY_CORRUPT", error.Code);
    }

    public void Dispose()
    {
        if (Directory.Exists(directory))
            Directory.Delete(directory, true);
    }
}
