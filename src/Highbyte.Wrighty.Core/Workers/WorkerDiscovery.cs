using System.Text.Json.Serialization;

namespace Highbyte.Wrighty.Workers;

public sealed record WorkerDiscovery(
    DateTimeOffset ObservedAt,
    string ConfigurationPathHash,
    string Coverage,
    string? Detail,
    string? ConfigurationRevision,
    string? ItemId,
    IReadOnlyList<WorkerDiscoveryEntry> LocalWorkers)
{
    public string Scope { get; } = "registered-workers-in-local-configuration";
}

public sealed record WorkerDiscoveryEntry(
    WorkerInstance Instance,
    [property: JsonConverter(typeof(JsonStringEnumConverter<WorkerInstanceLiveness>))]
    WorkerInstanceLiveness Liveness,
    string? Detail,
    string Intake,
    int? RemainingItemAllowance,
    DateTimeOffset? IdleExpiresAt,
    bool? ConfigurationDrift,
    WorkerPickupAssessment? Pickup)
{
    public string Origin => Instance.HostKind switch
    {
        WorkerHostKind.CliProcess => "cli-process",
        WorkerHostKind.WebHosted => "web-hosted",
        _ => "unknown"
    };
    public string ReportedState => Instance.State.ToString();

    public static WorkerDiscoveryEntry From(WorkerInstanceStatus worker, string? revision,
        WorkerPickupAssessment? pickup = null)
    {
        var instance = worker.Instance;
        int? remaining = null;
        if (instance.Scheduling is { ItemLimit: { } limit } selection && selection.IsUsable() &&
            instance.Progress is { Processed: >= 0 } progress)
            remaining = (int)Math.Max(0L, (long)limit - progress.Processed - (instance.CurrentItemId is null ? 0 : 1));
        return new(instance, worker.Liveness, worker.Detail, IntakeState(worker, remaining), remaining,
            IdleExpiry(instance),
            revision is null || string.IsNullOrEmpty(instance.ConfigurationRevision)
                ? null : revision != instance.ConfigurationRevision, pickup);
    }

    private static DateTimeOffset? IdleExpiry(WorkerInstance instance)
    {
        if (instance.CurrentItemId is not null || instance.Scheduling?.IdleTimeout is not { } timeout ||
            instance.Progress is not { } idle)
            return null;
        try { return idle.IdleSince + timeout; }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    private static string IntakeState(WorkerInstanceStatus worker, int? remaining)
    {
        if (worker.Liveness != WorkerInstanceLiveness.Running)
            return "unknown";
        if (worker.Instance.State == WorkerInstanceState.Draining)
            return "draining";
        if (worker.Instance.State is WorkerInstanceState.Stopping or WorkerInstanceState.StoppingNow
            or WorkerInstanceState.Finalizing)
            return "stopping";
        if (worker.Instance.Scheduling is not { } scheduling || !scheduling.IsUsable() ||
            worker.Instance.Progress is not { Processed: >= 0 } || !Enum.IsDefined(worker.Instance.State))
            return "unknown";
        return scheduling.TargetItemId is not null || scheduling.DryRun || remaining == 0 ? "closed" : "open";
    }
}
