using Highbyte.Wrighty.Configuration;

namespace Highbyte.Wrighty.Workers;

/// <summary>
/// One persistent, per-agent failure injected at the production result boundary. The retry delay
/// is meaningful only for the usage failures that enter Wrighty's automatic recovery policy.
/// </summary>
public sealed record AgentFailureSimulation(
    AgentFailureKind Kind,
    double RetryAfterSeconds = 0);

/// <summary>
/// One repository-scoped replacement for a provider capacity probe. Unlike a real probe result,
/// this value must never be persisted in the installation-wide provider-capacity store.
/// </summary>
public sealed record ProviderCapacitySimulation(
    string Result,
    double RetryAfterSeconds = 0);

public sealed record ProviderCapacitySimulationOption(
    string Token,
    string Label,
    AgentFailureKind? FailureKind,
    bool UsesRetryDelay);

public static class ProviderCapacitySimulationResults
{
    public static IReadOnlyList<ProviderCapacitySimulationOption> All { get; } =
    [
        new("available", "Available", null, false),
        new("usage-exhausted", "Usage exhausted", AgentFailureKind.UsageExhausted, true),
        new("rate-limited", "Rate limited", AgentFailureKind.RateLimited, true)
    ];

    public static ProviderCapacitySimulationOption? Find(string? token) =>
        All.FirstOrDefault(option => string.Equals(
            option.Token, token?.Trim(), StringComparison.OrdinalIgnoreCase));
}

public sealed record AgentFailureSimulationOption(
    AgentFailureKind Kind,
    string Token,
    string Label,
    string Behaviour,
    bool UsesRetryDelay);

/// <summary>The bounded set exposed by configuration surfaces.</summary>
public static class AgentFailureSimulationKinds
{
    public static IReadOnlyList<AgentFailureSimulationOption> All { get; } =
    [
        new(AgentFailureKind.UsageExhausted, "usage-exhausted", "Usage exhausted",
            "Exercises retry or cross-agent handoff policy.", true),
        new(AgentFailureKind.RateLimited, "rate-limited", "Rate limited",
            "Exercises retry or cross-agent handoff policy.", true),
        new(AgentFailureKind.Authentication, "authentication", "Authentication unavailable",
            "Stops the item for operator attention.", false),
        new(AgentFailureKind.BillingUnavailable, "billing-unavailable", "Billing unavailable",
            "Stops the item for operator attention.", false),
        new(AgentFailureKind.PermissionDenied, "permission-denied", "Permission denied",
            "Stops the item for operator attention.", false),
        new(AgentFailureKind.ProviderUnavailable, "provider-unavailable", "Provider unavailable",
            "Reports a retryable provider failure without usage recovery.", false),
        new(AgentFailureKind.ContextLimit, "context-limit", "Context limit",
            "Reports a terminal agent failure.", false),
        new(AgentFailureKind.AgentFailure, "agent-failure", "Agent failure",
            "Reports a terminal agent failure.", false)
    ];

    public static AgentFailureSimulationOption? Find(AgentFailureKind kind) =>
        All.FirstOrDefault(option => option.Kind == kind);

    public static bool TryParse(string? token, out AgentFailureKind kind)
    {
        var option = All.FirstOrDefault(candidate => string.Equals(
            candidate.Token, token?.Trim(), StringComparison.OrdinalIgnoreCase));
        kind = option?.Kind ?? default;
        return option is not null;
    }
}

public interface IAgentFailureSimulator
{
    Task<AgentRunResult?> TryCreateFailureAsync(
        TrackerConfig config,
        string agent,
        string? sessionId,
        CancellationToken cancellationToken);
}

public interface IProviderCapacitySimulator
{
    Task<ProviderCapacity?> TryCreateAsync(
        TrackerConfig config,
        string agent,
        DateTimeOffset observedAt,
        CancellationToken cancellationToken);
}

public sealed class NoOpProviderCapacitySimulator : IProviderCapacitySimulator
{
    public static NoOpProviderCapacitySimulator Instance { get; } = new();

    public Task<ProviderCapacity?> TryCreateAsync(
        TrackerConfig config,
        string agent,
        DateTimeOffset observedAt,
        CancellationToken cancellationToken) =>
        Task.FromResult<ProviderCapacity?>(null);
}

/// <summary>
/// Reads a probe replacement immediately before it is needed. The generated state is returned to
/// the caller only; it is deliberately never written to the machine-wide capacity store.
/// </summary>
public sealed class RepositoryConfigurationProviderCapacitySimulator(
    ITrackerConfigStore configurations) : IProviderCapacitySimulator
{
    public async Task<ProviderCapacity?> TryCreateAsync(
        TrackerConfig config,
        string agent,
        DateTimeOffset observedAt,
        CancellationToken cancellationToken)
    {
        var current = config.SourcePath is { } path && File.Exists(path)
            ? await configurations.TryLoadPathAsync(path, cancellationToken) ?? config
            : config;
        var configured = current.EffectiveTesting.FindCapacityProbe(agent);
        var option = ProviderCapacitySimulationResults.Find(configured?.Result);
        if (configured is null || option is null)
            return null;

        if (option.FailureKind is null)
        {
            return new ProviderCapacity(
                agent,
                ProviderCapacityState.Available,
                $"Wrighty simulated available capacity for {agent} from the effective repository configuration.",
                null,
                AgentFailureConfidence.Authoritative,
                0,
                observedAt,
                Simulated: true);
        }

        var retryAfter = TimeSpan.FromSeconds(Math.Clamp(
            configured.RetryAfterSeconds, 0, 86_400));
        return new ProviderCapacity(
            agent,
            ProviderCapacityState.UnavailableUntil,
            $"Wrighty simulated {option.Label.ToLowerInvariant()} capacity for {agent} from the effective repository configuration.",
            observedAt + retryAfter,
            AgentFailureConfidence.Authoritative,
            1,
            observedAt,
            Simulated: true);
    }
}

/// <summary>
/// Reads the durable setting immediately before each implementation launch. Nothing is cached, so
/// a web-console save affects an already-running worker's next launch without a restart.
/// </summary>
public sealed class RepositoryConfigurationAgentFailureSimulator(
    ITrackerConfigStore configurations) : IAgentFailureSimulator
{
    public async Task<AgentRunResult?> TryCreateFailureAsync(
        TrackerConfig config,
        string agent,
        string? sessionId,
        CancellationToken cancellationToken)
    {
        var current = config.SourcePath is { } path && File.Exists(path)
            ? await configurations.TryLoadPathAsync(path, cancellationToken) ?? config
            : config;
        var configured = current.EffectiveTesting.FindAgentFailure(agent);
        if (configured is null || AgentFailureSimulationKinds.Find(configured.Kind) is not { } option)
        {
            return null;
        }

        var retryable = configured.Kind is
            AgentFailureKind.UsageExhausted or
            AgentFailureKind.RateLimited or
            AgentFailureKind.ProviderUnavailable;
        var retryAfter = option.UsesRetryDelay
            ? TimeSpan.FromSeconds(Math.Clamp(configured.RetryAfterSeconds, 0, 86_400))
            : (TimeSpan?)null;
        var message = $"Wrighty simulated {option.Label.ToLowerInvariant()} for {agent} from " +
                      "the effective repository configuration.";
        var failure = new AgentFailure(
            configured.Kind,
            $"wrighty_simulated_{option.Token.Replace('-', '_')}",
            RetryAt: null,
            RetryAfter: retryAfter,
            IsRetryable: retryable,
            AgentFailureConfidence.Authoritative,
            message);
        return new AgentRunResult(AgentOutcome.Failed, sessionId, message, 1, failure);
    }

    public static bool IsSimulated(AgentFailure? failure) =>
        failure?.ProviderCode?.StartsWith("wrighty_simulated_", StringComparison.Ordinal) == true;
}
