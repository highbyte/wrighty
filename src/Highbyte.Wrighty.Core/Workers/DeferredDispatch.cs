namespace Highbyte.Wrighty.Workers;

/// <summary>
/// Machine-local recovery decision for an unclaimed work item. The portable worker-state field
/// carries only the categorical state; this record owns exact timing and session lineage.
/// </summary>
public sealed record DeferredDispatch(
    string WorkItemId,
    string State,
    string Reason,
    string? SourceAgent,
    string? SourceSessionId,
    string? TargetAgent,
    DateTimeOffset NotBefore,
    int Attempt,
    int MaxAttempts,
    AgentFailureConfidence FailureConfidence,
    DateTimeOffset UpdatedAt,
    string? HandoffSummaryPath = null)
{
    public bool IsValid =>
        !string.IsNullOrWhiteSpace(WorkItemId) &&
        State is Models.WorkerDispatchStates.RetryScheduled or
            Models.WorkerDispatchStates.HandoffQueued &&
        !string.IsNullOrWhiteSpace(Reason) &&
        Attempt > 0 &&
        MaxAttempts >= Attempt &&
        MaxAttempts <= 1000;

    public WorkerDispatchInfo ToInfo(bool fromCurrentInstallation) =>
        new(
            State,
            Reason,
            SourceAgent,
            TargetAgent,
            SourceAgent,
            NotBefore,
            Attempt,
            MaxAttempts,
            UpdatedAt,
            fromCurrentInstallation);
}

/// <summary>Backend-neutral operational projection of machine-local deferred dispatch state.</summary>
public sealed record WorkerDispatchInfo(
    string State,
    string Reason,
    string? CurrentAgent,
    string? TargetAgent,
    string? PreviousAgent,
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
