using static Highbyte.Wrighty.Workers.WorkerPickupOutcomes;
using Highbyte.Wrighty.Claims;
using Highbyte.Wrighty.Configuration;
using Highbyte.Wrighty.Errors;
using Highbyte.Wrighty.Models;

namespace Highbyte.Wrighty.Workers;

public sealed partial class WorkerService
{
    /// <summary>Advisory selection only. No claim, queue repair, workspace creation, or vendor probe.</summary>
    public async Task<WorkerPickupAssessment> AssessPickupAsync(
        TrackerConfig config, WorkerInstanceStatus worker, WorkItemOperationalState state,
        string? configurationRevision, DateTimeOffset observedAt, CancellationToken cancellationToken)
    {
        if (WorkerPickupPolicy.AssessRegistration(worker, state.Item.Id, configurationRevision, observedAt) is { } registration)
            return registration;
        try
        {
            return await AssessSelectionAsync(config, worker, state, cancellationToken);
        }
        catch (TrackerException exception)
        {
            return Pickup(worker, state, Unknown, exception.Code, exception.Message);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or System.ComponentModel.Win32Exception)
        {
            return Pickup(worker, state, Unknown, "ASSESSMENT_UNAVAILABLE", "Required local or tracker evidence could not be read.");
        }
    }

    private async Task<WorkerPickupAssessment> AssessSelectionAsync(TrackerConfig config,
        WorkerInstanceStatus worker, WorkItemOperationalState state, CancellationToken cancellationToken)
    {
        var scheduling = worker.Instance.Scheduling!;
        var options = scheduling.Options();
        if (state.Item.Archived)
            return Pickup(worker, state, CannotPickUp, "ITEM_ARCHIVED", "Archived work is outside worker intake.");
        if (!WorkerPolicyGate.MatchesFilters(state.Item, options.Filters))
            return Pickup(worker, state, CannotPickUp, "FILTER_MISMATCH", "The item does not match the worker's startup filters.");
        if (state.Claim.State == ClaimOwnershipState.HeldByOther)
            return Pickup(worker, state, CannotPickUp, "CLAIM_HELD", "Another installation holds this item's claim.");
        if (!Directory.Exists(scheduling.RepositoryPath))
            return Pickup(worker, state, Unknown, "WORKSPACE_UNAVAILABLE", "The worker's repository is unavailable to this observer.");

        string agent;
        if (string.Equals(state.Item.Status, scheduling.ToStatus, StringComparison.OrdinalIgnoreCase))
        {
            // Reuse the continuous loop's retained-session selection, including due local dispatch,
            // directed handoffs, claims, recorded workspace, and per-agent enablement.
            var queued = await QueuedCandidatesAsync(config, options, scheduling.RepositoryPath, null, cancellationToken);
            var candidate = queued.FirstOrDefault(value => value.Detail.Id == state.Item.Id);
            if (candidate is null)
                return Pickup(worker, state, CannotPickUp, "NOT_QUEUED_FOR_THIS_WORKER",
                    "The continuous worker's retained-session rules do not admit this item now; inspect its actions and dispatch timing.");
            agent = candidate.AgentName;
        }
        else
        {
            var fresh = await AssessFreshPickupAsync(config, worker, state, options, cancellationToken);
            if (fresh.Outcome != CouldPickUp)
                return fresh;
            agent = fresh.Agent!;
        }
        var workspace = state.Session?.WorkspacePath ?? scheduling.RepositoryPath;
        if ((state.Session is not null || scheduling.WorkspaceMode == WorkspaceMode.Current) &&
            workspaceLocks.Inspect(workspace) is { State: not "available" } workspaceState)
            return Pickup(worker, state, Unknown, "WORKSPACE_LOCK_UNVERIFIED", workspaceState.Detail ?? "Workspace availability is unknown.");
        if (config.Testing?.FindCapacityProbe(agent) is not null)
            return Pickup(worker, state, Unknown, "SIMULATED_CAPACITY", "Provider simulation is configured; discovery does not create or consume its state.");
        var capacity = await providerCapacity.GetAsync(agent, cancellationToken);
        if (capacity is { State: not ProviderCapacityState.Available })
            return Pickup(worker, state, CannotPickUp, "PROVIDER_DEFERRED",
                "Cached provider state delays pickup; no paid capacity probe was run.") with { Agent = agent };
        return Pickup(worker, state, CouldPickUp, "ELIGIBLE",
            "Observed selection rules allow pickup. This is not a reservation or a timing guarantee; launch checks run again at pickup.")
            with { Agent = agent, AfterCurrentItem = worker.Instance.CurrentItemId is not null };
    }

    private async Task<WorkerPickupAssessment> AssessFreshPickupAsync(TrackerConfig config,
        WorkerInstanceStatus worker, WorkItemOperationalState state, WorkerOptions options,
        CancellationToken cancellationToken)
    {
        var scheduling = worker.Instance.Scheduling!;
        if (!string.Equals(state.Item.Status, scheduling.FromStatus, StringComparison.OrdinalIgnoreCase))
            return Pickup(worker, state, CannotPickUp, "STATUS_MISMATCH", "The item is outside this worker's source and retained-session statuses.");
        if (state.Claim.State != ClaimOwnershipState.Unclaimed)
            return Pickup(worker, state, CannotPickUp, "CLAIM_HELD", "The item is already claimed.");
        var candidate = ProjectWorkerQueueAuthorization(config, state.Item, scheduling.FromStatus);
        var diagnostics = new WorkerCandidateDiagnostics(scheduling.FromStatus);
        var evaluation = EvaluateCandidate(candidate, options, scheduling.DefaultAgent,
            await LoadUserSettingsAsync(cancellationToken), diagnostics);
        if (!evaluation.Eligible)
            return Pickup(worker, state, CannotPickUp, "WORKER_POLICY_REFUSED", diagnostics.Describe(options.Filters.Count > 0));
        EnsurePreflightWorkspaceReady(options, scheduling.RepositoryPath, evaluation.Agent!, new HashSet<string>());
        var verdict = await LaunchPreflight.EvaluateAsync(new LaunchPreflightRequest(
            config, options, candidate, evaluation.Agent!, LaunchKind.Fresh, LaunchStage.PreClaim), cancellationToken);
        if (!verdict.Admitted)
            return Pickup(worker, state, CannotPickUp, verdict.Code ?? "CONTEXT_BLOCKED", verdict.Message ?? "Pre-claim admission refused this item.");
        return Pickup(worker, state, CouldPickUp, "ELIGIBLE", "Fresh selection policy admits this item.") with { Agent = evaluation.Agent };
    }

    private static WorkerPickupAssessment Pickup(WorkerInstanceStatus worker, WorkItemOperationalState state,
        string outcome, string code, string message) => new(worker.Instance.RunId, state.Item.Id.Value, outcome, code, message);
}
