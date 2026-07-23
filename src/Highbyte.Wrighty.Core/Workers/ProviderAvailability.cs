using System.Text.Json.Serialization;

namespace Highbyte.Wrighty.Workers;

[JsonConverter(typeof(JsonStringEnumConverter<ProviderAvailabilityState>))]
public enum ProviderAvailabilityState
{
    [JsonStringEnumMemberName("available")]
    Available,
    [JsonStringEnumMemberName("unavailable-until")]
    UnavailableUntil,
    [JsonStringEnumMemberName("probe-due")]
    ProbeDue
}

/// <summary>
/// A sanitized, installation-local view of one agent provider's capacity. Account identifiers and
/// raw provider responses are deliberately excluded.
/// </summary>
public sealed record ProviderAvailability(
    string AgentType,
    ProviderAvailabilityState State,
    string? Reason,
    DateTimeOffset? UnavailableUntil,
    AgentFailureConfidence Confidence,
    int ConsecutiveFailures,
    DateTimeOffset UpdatedAt);

public sealed record ProviderProbeLease(
    string AgentType,
    string LeaseId,
    DateTimeOffset ExpiresAt);

public interface IProviderAvailabilityStore
{
    Task<ProviderAvailability?> GetAsync(
        string agentType,
        CancellationToken cancellationToken);

    Task<ProviderAvailability> OpenAsync(
        string agentType,
        string? reason,
        DateTimeOffset unavailableUntil,
        AgentFailureConfidence confidence,
        DateTimeOffset observedAt,
        CancellationToken cancellationToken);

    Task<ProviderProbeLease?> TryAcquireProbeAsync(
        string agentType,
        DateTimeOffset observedAt,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken);

    Task CloseAsync(
        string agentType,
        DateTimeOffset observedAt,
        CancellationToken cancellationToken);

    Task ReleaseProbeAsync(
        ProviderProbeLease lease,
        DateTimeOffset observedAt,
        CancellationToken cancellationToken);
}

internal sealed class NoOpProviderAvailabilityStore : IProviderAvailabilityStore
{
    public static NoOpProviderAvailabilityStore Instance { get; } = new();

    public Task<ProviderAvailability?> GetAsync(
        string agentType,
        CancellationToken cancellationToken) =>
        Task.FromResult<ProviderAvailability?>(null);

    public Task<ProviderAvailability> OpenAsync(
        string agentType,
        string? reason,
        DateTimeOffset unavailableUntil,
        AgentFailureConfidence confidence,
        DateTimeOffset observedAt,
        CancellationToken cancellationToken) =>
        Task.FromResult(new ProviderAvailability(
            agentType,
            ProviderAvailabilityState.UnavailableUntil,
            reason,
            unavailableUntil,
            confidence,
            1,
            observedAt));

    public Task<ProviderProbeLease?> TryAcquireProbeAsync(
        string agentType,
        DateTimeOffset observedAt,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken) =>
        Task.FromResult<ProviderProbeLease?>(
            new ProviderProbeLease(agentType, Guid.NewGuid().ToString("N"), observedAt + leaseDuration));

    public Task CloseAsync(
        string agentType,
        DateTimeOffset observedAt,
        CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public Task ReleaseProbeAsync(
        ProviderProbeLease lease,
        DateTimeOffset observedAt,
        CancellationToken cancellationToken) =>
        Task.CompletedTask;
}
