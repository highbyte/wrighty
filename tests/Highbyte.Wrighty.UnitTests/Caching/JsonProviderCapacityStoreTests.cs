using Highbyte.Wrighty.Caching;
using Highbyte.Wrighty.Errors;
using Highbyte.Wrighty.Workers;

namespace Highbyte.Wrighty.UnitTests.Caching;

public sealed class JsonProviderCapacityStoreTests : IDisposable
{
    private readonly string directory = Path.Combine(
        Path.GetTempPath(), $"wrighty-provider-cache-{Guid.NewGuid():N}");
    private static readonly DateTimeOffset ObservedAt =
        new(2026, 7, 23, 18, 0, 0, TimeSpan.Zero);

    private JsonProviderCapacityStore Store() =>
        new(new CachePaths(directory));

    [Fact]
    public async Task Unavailable_capacity_persists_sanitized_state_across_instances()
    {
        await Store().RecordUnavailableAsync(
            "Claude",
            "  Usage   limit reached for user@example.com api_key=secret-value.  ",
            ObservedAt.AddHours(2),
            AgentFailureConfidence.Authoritative,
            ObservedAt,
            CancellationToken.None);

        var capacity = await Store().GetAsync("claude", CancellationToken.None);
        var json = await File.ReadAllTextAsync(
            new CachePaths(directory).ProviderCapacityPath);

        Assert.Equal(ProviderCapacityState.UnavailableUntil, capacity?.State);
        Assert.Equal(
            "Usage limit reached for [redacted-email] api_key=[redacted]",
            capacity?.Reason);
        Assert.Equal(ObservedAt.AddHours(2), capacity?.UnavailableUntil);
        Assert.Equal(1, capacity?.ConsecutiveFailures);
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
        await first.RecordUnavailableAsync(
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
        var capacity = await Store().GetAsync("claude", CancellationToken.None);
        Assert.Equal(ProviderCapacityState.ProbeInProgress, capacity?.State);
        Assert.Equal(ObservedAt.AddMinutes(2), capacity?.UnavailableUntil);
    }

    [Fact]
    public async Task Explicit_probe_can_lease_provider_without_an_open_circuit()
    {
        var first = Store();
        var second = Store();

        var leases = await Task.WhenAll(
            first.TryAcquireProbeAsync(
                "copilot",
                ObservedAt,
                TimeSpan.FromMinutes(2),
                CancellationToken.None,
                allowWhenAvailable: true),
            second.TryAcquireProbeAsync(
                "copilot",
                ObservedAt,
                TimeSpan.FromMinutes(2),
                CancellationToken.None,
                allowWhenAvailable: true));

        Assert.Single(leases, lease => lease is not null);
        var capacity = await Store().GetAsync("copilot", CancellationToken.None);
        Assert.Equal(ProviderCapacityState.ProbeInProgress, capacity?.State);
        Assert.Equal(0, capacity?.ConsecutiveFailures);
        Assert.Equal(ObservedAt.AddMinutes(2), capacity?.UnavailableUntil);
    }

    [Fact]
    public async Task Releasing_proactive_probe_restores_available_state()
    {
        var store = Store();
        var lease = await store.TryAcquireProbeAsync(
            "codex",
            ObservedAt,
            TimeSpan.FromMinutes(2),
            CancellationToken.None,
            allowWhenAvailable: true);

        await store.ReleaseProbeAsync(
            lease!,
            ObservedAt.AddMinutes(1),
            CancellationToken.None);

        var capacity = await store.GetAsync("codex", CancellationToken.None);
        Assert.Equal(ProviderCapacityState.Available, capacity?.State);
        Assert.Null(capacity?.UnavailableUntil);
        Assert.Equal(0, capacity?.ConsecutiveFailures);
    }

    [Fact]
    public async Task Expired_probe_can_be_reacquired_and_success_closes_the_circuit()
    {
        var store = Store();
        await store.RecordUnavailableAsync(
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
        await store.RecordAvailableAsync(
            "codex", ObservedAt.AddMinutes(3), CancellationToken.None);

        Assert.NotNull(expired);
        Assert.NotNull(replacement);
        Assert.NotEqual(expired!.LeaseId, replacement!.LeaseId);
        var capacity = await store.GetAsync("codex", CancellationToken.None);
        Assert.Equal(ProviderCapacityState.Available, capacity?.State);
        Assert.Equal(0, capacity?.ConsecutiveFailures);
        Assert.Null(capacity?.UnavailableUntil);
    }

    [Fact]
    public async Task List_returns_stable_sanitized_snapshots_for_presentation()
    {
        var store = Store();
        await store.RecordUnavailableAsync(
            "copilot",
            "No quota for user@example.com.",
            ObservedAt.AddDays(1),
            AgentFailureConfidence.Authoritative,
            ObservedAt,
            CancellationToken.None);
        await store.RecordUnavailableAsync(
            "claude",
            "Usage limit reached.",
            ObservedAt.AddHours(2),
            AgentFailureConfidence.Inferred,
            ObservedAt,
            CancellationToken.None);

        var capacity = await store.ListAsync(CancellationToken.None);

        Assert.Collection(
            capacity,
            claude =>
            {
                Assert.Equal("claude", claude.Agent);
                Assert.Equal("Usage limit reached.", claude.Reason);
            },
            copilot =>
            {
                Assert.Equal("copilot", copilot.Agent);
                Assert.Equal("No quota for [redacted-email].", copilot.Reason);
            });
    }

    [Fact]
    public async Task Corrupt_state_fails_closed()
    {
        var paths = new CachePaths(directory);
        Directory.CreateDirectory(paths.Root);
        await File.WriteAllTextAsync(paths.ProviderCapacityPath, "{ not json");

        var error = await Assert.ThrowsAsync<TrackerException>(
            () => Store().ListAsync(CancellationToken.None));

        Assert.Equal("PROVIDER_CAPACITY_CORRUPT", error.Code);
    }

    [Fact]
    public async Task Pre_overhaul_store_is_rejected_without_migration()
    {
        var paths = new CachePaths(directory);
        Directory.CreateDirectory(paths.Root);
        await File.WriteAllTextAsync(
            Path.Combine(paths.Root, "provider-availability-v1.json"),
            """{ "version": 1, "entries": {} }""");

        var error = await Assert.ThrowsAsync<TrackerException>(
            () => Store().ListAsync(CancellationToken.None));

        Assert.Equal("STORE_SCHEMA_UNSUPPORTED", error.Code);
        Assert.Contains("provider-availability-v1.json", error.Message);
        Assert.Contains("Remove or rename", error.Message);
        Assert.False(File.Exists(paths.ProviderCapacityPath));
    }

    public void Dispose()
    {
        if (Directory.Exists(directory))
            Directory.Delete(directory, true);
    }
}
