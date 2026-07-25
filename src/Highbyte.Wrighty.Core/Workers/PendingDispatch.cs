namespace Highbyte.Wrighty.Workers;

/// <summary>
/// Machine-local pending work for an unclaimed item. The portable dispatch-state field carries
/// only the categorical state; this record owns exact timing and session lineage.
/// </summary>
public sealed record PendingDispatch(
    string WorkItemId,
    string State,
    string Reason,
    string? SessionAgent,
    string? SessionId,
    string? Agent,
    DateTimeOffset NotBefore,
    int Attempt,
    int MaxAttempts,
    AgentFailureConfidence FailureConfidence,
    DateTimeOffset UpdatedAt,
    string? HandoffSummaryPath = null)
{
    public bool IsValid =>
        !string.IsNullOrWhiteSpace(WorkItemId) &&
        State is Models.DispatchStates.RetryScheduled or
            Models.DispatchStates.HandoffQueued &&
        !string.IsNullOrWhiteSpace(Reason) &&
        Attempt > 0 &&
        MaxAttempts >= Attempt &&
        MaxAttempts <= 1000;

    public DispatchInfo ToInfo(bool fromCurrentInstallation) =>
        new(
            State,
            Reason,
            SessionAgent,
            Agent,
            NotBefore,
            Attempt,
            MaxAttempts,
            UpdatedAt,
            fromCurrentInstallation);
}

/// <summary>Backend-neutral projection of machine-local pending dispatch state.</summary>
public sealed record DispatchInfo(
    string State,
    string Reason,
    string? SessionAgent,
    string? Agent,
    DateTimeOffset NotBefore,
    int Attempt,
    int MaxAttempts,
    DateTimeOffset UpdatedAt,
    bool FromCurrentInstallation);

public static class RetrySchedule
{
    private static readonly TimeSpan MaximumJitter = TimeSpan.FromSeconds(30);

    public static DateTimeOffset ChooseNotBefore(
        DateTimeOffset current,
        Models.WorkItemId workItemId,
        AgentFailure failure,
        Configuration.WorkerUsageFailureConfig policy,
        int attempt)
    {
        var jitter = DeterministicJitter(workItemId, attempt);
        if (failure.RetryAt is { } retryAt)
        {
            var resetWithGrace = retryAt + TimeSpan.FromMinutes(policy.ResetGraceMinutes);
            return (resetWithGrace > current ? resetWithGrace : current) + jitter;
        }

        if (failure.RetryAfter is { } retryAfter)
            return current + (retryAfter > TimeSpan.Zero ? retryAfter : TimeSpan.Zero) + jitter;

        var exponent = Math.Max(0, attempt - 1);
        var delayMinutes = policy.InitialRetryMinutes *
                           Math.Pow(policy.BackoffMultiplier, exponent);
        var delay = TimeSpan.FromMinutes(Math.Min(
            delayMinutes,
            TimeSpan.FromHours(policy.MaxRetryHours).TotalMinutes));
        return current + delay + jitter;
    }

    internal static TimeSpan DeterministicJitter(Models.WorkItemId workItemId, int attempt)
    {
        var input = $"{Environment.MachineName}\n{workItemId.Value}\n{attempt}";
        var hash = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(input));
        var fraction = BitConverter.ToUInt32(hash, 0) / (double)uint.MaxValue;
        return TimeSpan.FromTicks((long)(MaximumJitter.Ticks * fraction));
    }
}
