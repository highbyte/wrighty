using Highbyte.Wrighty.Configuration;

namespace Highbyte.Wrighty.Workers;

/// <summary>
/// One persistent, per-agent failure injected at the production result boundary. The retry delay
/// is meaningful only for the usage failures that enter Wrighty's automatic recovery policy.
/// </summary>
public sealed record AgentFailureSimulation(
    AgentFailureKind Kind,
    double RetryAfterSeconds = 0);

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
