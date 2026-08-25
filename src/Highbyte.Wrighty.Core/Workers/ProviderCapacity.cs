using System.Text.Json.Serialization;
using Highbyte.Wrighty.Configuration;
using Highbyte.Wrighty.Errors;

namespace Highbyte.Wrighty.Workers;

[JsonConverter(typeof(JsonStringEnumConverter<ProviderCapacityState>))]
public enum ProviderCapacityState
{
    [JsonStringEnumMemberName("available")]
    Available,
    [JsonStringEnumMemberName("unavailable-until")]
    UnavailableUntil,
    [JsonStringEnumMemberName("probe-in-progress")]
    ProbeInProgress
}

/// <summary>
/// A sanitized, installation-local view of one agent provider's capacity. Account identifiers and
/// raw provider responses are deliberately excluded.
/// </summary>
public sealed record ProviderCapacity(
    string Agent,
    ProviderCapacityState State,
    string? Reason,
    DateTimeOffset? UnavailableUntil,
    AgentFailureConfidence Confidence,
    int ConsecutiveFailures,
    DateTimeOffset UpdatedAt,
    bool Simulated = false);

public sealed record ProviderProbeLease(
    string Agent,
    string LeaseId,
    DateTimeOffset ExpiresAt);

public interface IProviderCapacityStore
{
    Task<IReadOnlyList<ProviderCapacity>> ListAsync(
        CancellationToken cancellationToken);

    Task<ProviderCapacity?> GetAsync(
        string agentType,
        CancellationToken cancellationToken);

    Task<ProviderCapacity> RecordUnavailableAsync(
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
        CancellationToken cancellationToken,
        bool allowBeforeUnavailableUntil = false,
        bool allowWhenAvailable = false);

    Task RecordAvailableAsync(
        string agentType,
        DateTimeOffset observedAt,
        CancellationToken cancellationToken);

    Task ReleaseProbeAsync(
        ProviderProbeLease lease,
        DateTimeOffset observedAt,
        CancellationToken cancellationToken);
}

public interface IProviderCapacityProbeService
{
    IReadOnlyList<string> SupportedAgents { get; }

    Task<ProviderCapacity> ProbeProviderAsync(
        TrackerConfig config,
        string agentType,
        string repositoryPath,
        Func<WorkerEvent, Task> emit,
        CancellationToken cancellationToken);

    Task<ProviderCapacity?> GetSimulatedCapacityAsync(
        TrackerConfig config,
        string agentType,
        CancellationToken cancellationToken) =>
        Task.FromResult<ProviderCapacity?>(null);
}

public sealed class UnavailableProviderCapacityProbeService : IProviderCapacityProbeService
{
    public static UnavailableProviderCapacityProbeService Instance { get; } = new();

    public IReadOnlyList<string> SupportedAgents => [];

    public Task<ProviderCapacity> ProbeProviderAsync(
        TrackerConfig config,
        string agentType,
        string repositoryPath,
        Func<WorkerEvent, Task> emit,
        CancellationToken cancellationToken) =>
        Task.FromException<ProviderCapacity>(new TrackerException(
            "PROVIDER_PROBE_UNAVAILABLE",
            "Provider capacity probing is not configured in this Wrighty process.",
            7));
}

public sealed class NoOpProviderCapacityStore : IProviderCapacityStore
{
    public static NoOpProviderCapacityStore Instance { get; } = new();

    public Task<IReadOnlyList<ProviderCapacity>> ListAsync(
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<ProviderCapacity>>([]);

    public Task<ProviderCapacity?> GetAsync(
        string agentType,
        CancellationToken cancellationToken) =>
        Task.FromResult<ProviderCapacity?>(null);

    public Task<ProviderCapacity> RecordUnavailableAsync(
        string agentType,
        string? reason,
        DateTimeOffset unavailableUntil,
        AgentFailureConfidence confidence,
        DateTimeOffset observedAt,
        CancellationToken cancellationToken) =>
        Task.FromResult(new ProviderCapacity(
            agentType,
            ProviderCapacityState.UnavailableUntil,
            reason,
            unavailableUntil,
            confidence,
            1,
            observedAt));

    public Task<ProviderProbeLease?> TryAcquireProbeAsync(
        string agentType,
        DateTimeOffset observedAt,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken,
        bool allowBeforeUnavailableUntil = false,
        bool allowWhenAvailable = false) =>
        Task.FromResult<ProviderProbeLease?>(
            new ProviderProbeLease(agentType, Guid.NewGuid().ToString("N"), observedAt + leaseDuration));

    public Task RecordAvailableAsync(
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
