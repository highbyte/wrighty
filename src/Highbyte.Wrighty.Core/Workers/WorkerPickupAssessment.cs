using static Highbyte.Wrighty.Workers.WorkerPickupOutcomes;
using Highbyte.Wrighty.Models;

namespace Highbyte.Wrighty.Workers;

public static class WorkerPickupOutcomes
{
    public const string CouldPickUp = "could-pick-up";
    public const string CannotPickUp = "cannot-pick-up";
    public const string Unknown = "unknown";
}

public sealed record WorkerPickupAssessment(
    string RunId,
    string ItemId,
    string Outcome,
    string Code,
    string Message,
    bool AlreadyProcessing = false,
    bool AfterCurrentItem = false,
    string? Agent = null);

/// <summary>Registry evidence only. Item eligibility is evaluated by WorkerService afterwards.</summary>
public static class WorkerPickupPolicy
{
    public static WorkerPickupAssessment? AssessRegistration(WorkerInstanceStatus status,
        WorkItemId itemId, string? configurationRevision, DateTimeOffset observedAt)
    {
        var worker = status.Instance;
        WorkerPickupAssessment Result(string outcome, string code, string message) =>
            new(worker.RunId, itemId.Value, outcome, code, message);
        if (status.Liveness == WorkerInstanceLiveness.Unknown)
            return Result(Unknown, "WORKER_NOT_VERIFIED", "Worker liveness could not be verified.");
        if (status.Liveness != WorkerInstanceLiveness.Running)
            return Result(CannotPickUp, "WORKER_STALE", "This registration is stale; it does not establish active intake.");
        if (worker.CurrentItemId == itemId.Value && worker.State is
            WorkerInstanceState.PreparingItem or WorkerInstanceState.RunningItem or
            WorkerInstanceState.Draining or WorkerInstanceState.StoppingNow)
            return Result(CouldPickUp, "ALREADY_PROCESSING", "This worker reports that it is processing this item.")
                with { AlreadyProcessing = true, Agent = worker.CurrentAgent };
        if (worker.State is WorkerInstanceState.Draining or WorkerInstanceState.Stopping or
            WorkerInstanceState.StoppingNow or WorkerInstanceState.Finalizing)
            return Result(CannotPickUp, "INTAKE_CLOSED", "The worker is draining, stopping, or finalizing.");
        if (!Enum.IsDefined(worker.State))
            return Result(Unknown, "INTAKE_UNKNOWN", "The worker's intake state is not recognized.");
        if (worker.Scheduling is not { } scheduling)
            return Result(Unknown, "SCHEDULING_UNKNOWN", "This registration has no structured startup selection.");
        if (!scheduling.IsUsable())
            return Result(Unknown, "SCHEDULING_UNKNOWN", "The startup selection is incomplete or unsupported.");
        return AssessSelection(worker, itemId, configurationRevision, observedAt);
    }

    private static WorkerPickupAssessment? AssessSelection(WorkerInstance worker,
        WorkItemId itemId, string? configurationRevision, DateTimeOffset observedAt)
    {
        var scheduling = worker.Scheduling!;
        WorkerPickupAssessment Result(string outcome, string code, string message) =>
            new(worker.RunId, itemId.Value, outcome, code, message);
        if (scheduling.DryRun)
            return Result(CannotPickUp, "DRY_RUN", "This run only previews work.");
        if (scheduling.TargetItemId is { } target)
            return target == itemId.Value
                ? Result(Unknown, "TARGETED_STARTING", "This run targets the item but has not reported processing it yet.")
                : Result(CannotPickUp, "TARGET_MISMATCH", "This run targets a different item.");
        if (scheduling.Mode is not ("continuous" or "bounded"))
            return Result(Unknown, "SCHEDULING_UNKNOWN", "The worker selection mode is not recognized.");
        return AssessLifetime(worker, itemId, configurationRevision, observedAt);
    }

    private static WorkerPickupAssessment? AssessLifetime(WorkerInstance worker,
        WorkItemId itemId, string? configurationRevision, DateTimeOffset observedAt)
    {
        var scheduling = worker.Scheduling!;
        WorkerPickupAssessment Result(string outcome, string code, string message) =>
            new(worker.RunId, itemId.Value, outcome, code, message);
        if (worker.Progress is not { } progress || progress.Processed < 0)
            return Result(Unknown, "PROGRESS_UNKNOWN", "Remaining allowance and idle lifetime could not be established.");
        var reserved = worker.CurrentItemId is null ? 0 : 1;
        if (scheduling.ItemLimit is { } limit && progress.Processed >= limit - reserved)
            return Result(CannotPickUp, "ITEM_LIMIT_REACHED", "The run has no allowance for another item.");
        if (worker.CurrentItemId is null && scheduling.IdleTimeout is { } timeout &&
            observedAt - progress.IdleSince >= timeout)
            return Result(CannotPickUp, "IDLE_EXPIRED", "The reported idle lifetime has expired.");
        if (configurationRevision is null || worker.ConfigurationRevision != configurationRevision)
            return Result(Unknown, "CONFIGURATION_DRIFT", "The worker's startup configuration cannot be matched to the current configuration.");
        return null;
    }
}
