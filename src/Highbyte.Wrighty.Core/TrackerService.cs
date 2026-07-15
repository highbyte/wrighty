using Highbyte.Wrighty.AgentContext;
using Highbyte.Wrighty.Backends;
using Highbyte.Wrighty.Claims;
using Highbyte.Wrighty.Configuration;
using Highbyte.Wrighty.Errors;
using Highbyte.Wrighty.Models;

namespace Highbyte.Wrighty;

public sealed class TrackerService(ITrackerBackendRegistry backends)
{
    public ITrackerBackend Backend(TrackerConfig config) => backends.Get(config.Backend);

    public WorkItemId ResolveId(TrackerConfig config, string input) =>
        Backend(config).AddressResolver.Resolve(input, config);

    public string FormatShort(TrackerConfig config, WorkItemId id) =>
        Backend(config).AddressResolver.FormatShort(id, config);

    public Task<BackendInitializationResult> InitializeAsync(
        TrackerConfig config,
        bool checkOnly,
        CancellationToken cancellationToken) =>
        Backend(config).InitializeAsync(config, checkOnly, cancellationToken);

    public Task<IReadOnlyList<WorkItemSummary>> ListAsync(
        TrackerConfig config,
        string? status,
        int? limit,
        CancellationToken cancellationToken) =>
        ListAsync(config, new ListWorkItemsRequest(status, limit), cancellationToken);

    public Task<IReadOnlyList<WorkItemSummary>> ListAsync(
        TrackerConfig config,
        ListWorkItemsRequest request,
        CancellationToken cancellationToken) =>
        Backend(config).ListAsync(config, request, cancellationToken);

    public async Task<WorkItemDetail> GetAsync(
        TrackerConfig config,
        WorkItemId id,
        CancellationToken cancellationToken) =>
        await Backend(config).GetAsync(config, id, cancellationToken)
        ?? throw new TrackerException(
            "WORK_ITEM_NOT_FOUND",
            $"Work item '{id}' was not found in the configured tracker.",
            5,
            new Dictionary<string, object?> { ["id"] = id.Value });

    public Task<CreateWorkItemResult> CreateAsync(
        TrackerConfig config,
        CreateWorkItemRequest request,
        CancellationToken cancellationToken) =>
        CreateAsync(config, request, null, cancellationToken);

    public Task<CreateWorkItemResult> CreateAsync(
        TrackerConfig config,
        CreateWorkItemRequest request,
        string? creationAttemptId,
        CancellationToken cancellationToken)
    {
        var status = request.Status ?? config.DefaultPickFrom;
        var resolvedRequest = request with { Status = status };
        return Backend(config).CreateAsync(
            config,
            new CreateWorkItemOperation(
                resolvedRequest,
                config.ShouldArchiveStatus(status),
                CreationAttempt.NormalizeOrCreate(creationAttemptId)),
            cancellationToken);
    }

    public Task<UpdateWorkItemResult> UpdateAsync(
        TrackerConfig config,
        WorkItemId id,
        WorkItemPatch patch,
        CancellationToken cancellationToken)
    {
        WorkItemPatchValidator.Validate(patch);
        return Backend(config).UpdateAsync(
            config,
            id,
            new UpdateWorkItemOperation(
                patch,
                patch.Status.IsSpecified && config.ShouldArchiveStatus(patch.Status.Value)),
            cancellationToken);
    }

    public async Task<ClaimResult> ClaimAsync(
        TrackerConfig config,
        WorkItemId id,
        AgentExecutionContext agentContext,
        CancellationToken cancellationToken)
    {
        var result = await Backend(config).TryClaimAsync(config, id, agentContext, cancellationToken);
        if (result.Outcome == ClaimOutcome.HeldByOther)
        {
            throw new TrackerException(
                "CLAIM_HELD",
                $"Work item '{id}' is claimed by worker {result.WorkerIdentity} until {result.ExpiresAt:O}.",
                6,
                new Dictionary<string, object?>
                {
                    ["workerIdentity"] = result.WorkerIdentity,
                    ["expiresAt"] = result.ExpiresAt
                });
        }

        return result;
    }

    public Task ReleaseAsync(
        TrackerConfig config,
        WorkItemId id,
        CancellationToken cancellationToken) =>
        Backend(config).ReleaseAsync(config, id, cancellationToken);

    public Task<ArchiveWorkItemResult> ArchiveAsync(
        TrackerConfig config,
        WorkItemId id,
        CancellationToken cancellationToken) =>
        Backend(config).ArchiveAsync(config, id, cancellationToken);

    public Task<ArchiveWorkItemResult> UnarchiveAsync(
        TrackerConfig config,
        WorkItemId id,
        CancellationToken cancellationToken) =>
        Backend(config).UnarchiveAsync(config, id, cancellationToken);

    public async Task<FinishWorkItemResult> FinishAsync(
        TrackerConfig config,
        WorkItemId id,
        string? status,
        CancellationToken cancellationToken)
    {
        var targetStatus = string.IsNullOrWhiteSpace(status)
            ? config.DefaultFinishTo
            : status;
        var backend = Backend(config);
        var initial = await GetAsync(config, id, cancellationToken);
        var ownership = await backend.GetClaimOwnershipAsync(config, id, cancellationToken);

        if (ownership.State == ClaimOwnershipState.HeldByOther)
        {
            throw new TrackerException(
                "CLAIM_HELD",
                $"Work item '{id}' is claimed by another worker.",
                6,
                OwnershipDetails(ownership));
        }

        var alreadyAtTarget = string.Equals(
            initial.Status,
            targetStatus,
            StringComparison.OrdinalIgnoreCase);
        if (ownership.State == ClaimOwnershipState.Unclaimed)
        {
            if (alreadyAtTarget)
            {
                return new FinishWorkItemResult(
                    initial,
                    FinishDisposition.AlreadyFinished,
                    false,
                    true);
            }

            throw new TrackerException(
                "CLAIM_REQUIRED",
                $"Work item '{id}' must be claimed by the current worker before it can be finished.",
                6,
                OwnershipDetails(ownership));
        }

        var final = initial;
        var statusChanged = false;
        if (!alreadyAtTarget)
        {
            try
            {
                var update = await backend.UpdateAsync(
                    config,
                    id,
                    new UpdateWorkItemOperation(
                        WorkItemPatch.StatusOnly(targetStatus),
                        config.ShouldArchiveStatus(targetStatus)),
                    cancellationToken);
                final = update.Item;
                statusChanged = update.ChangedFields.Contains("status", StringComparer.OrdinalIgnoreCase);
            }
            catch (TrackerException exception) when (exception.Code == "PARTIAL_UPDATE")
            {
                var applied = exception.Details.TryGetValue("appliedFields", out var fields) &&
                              fields is IEnumerable<string> values
                    ? values.ToArray()
                    : [];
                throw PartialFinish(
                    id,
                    backend.AddressResolver.FormatShort(id, config),
                    targetStatus,
                    exception,
                    applied.Contains("status", StringComparer.OrdinalIgnoreCase),
                    applied.Contains("archived", StringComparer.OrdinalIgnoreCase));
            }
        }

        if (final.Archived)
        {
            return new FinishWorkItemResult(
                final,
                FinishDisposition.Finished,
                statusChanged,
                true);
        }

        try
        {
            await backend.ReleaseAsync(config, id, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw PartialFinish(
                id,
                backend.AddressResolver.FormatShort(id, config),
                targetStatus,
                exception,
                statusApplied: true,
                archived: final.Archived);
        }

        return new FinishWorkItemResult(
            final,
            FinishDisposition.Finished,
            statusChanged,
            true);
    }

    public async Task<WorkItemSummary> PickAsync(
        TrackerConfig config,
        string? fromStatus,
        string? toStatus,
        AgentExecutionContext agentContext,
        CancellationToken cancellationToken)
    {
        var backend = Backend(config);
        var candidates = await backend.ListAsync(
            config,
            new ListWorkItemsRequest(
                fromStatus ?? config.DefaultPickFrom,
                null,
                ArchiveScope.Active),
            cancellationToken);

        foreach (var candidate in candidates)
        {
            var claim = await backend.TryClaimAsync(config, candidate.Id, agentContext, cancellationToken);
            if (claim.Outcome == ClaimOutcome.HeldByOther)
            {
                continue;
            }

            var targetStatus = toStatus ?? config.DefaultPickTo;
            if (!string.IsNullOrWhiteSpace(targetStatus) &&
                !string.Equals(candidate.Status, targetStatus, StringComparison.OrdinalIgnoreCase))
            {
                var update = await backend.UpdateAsync(
                    config,
                    candidate.Id,
                    new UpdateWorkItemOperation(
                        WorkItemPatch.StatusOnly(targetStatus),
                        config.ShouldArchiveStatus(targetStatus)),
                    cancellationToken);
                return Summary(update.Item);
            }

            return candidate;
        }

        throw new TrackerException(
            "NO_ITEM_AVAILABLE",
            $"No claimable item was found in status '{fromStatus ?? config.DefaultPickFrom}'.",
            8);
    }

    private static WorkItemSummary Summary(WorkItemDetail detail) => new(
        detail.Id,
        detail.Title,
        detail.Url,
        detail.Status,
        detail.Priority,
        detail.Archived);

    private static IReadOnlyDictionary<string, object?> OwnershipDetails(
        ClaimOwnershipResult ownership) => new Dictionary<string, object?>
        {
            ["workerIdentity"] = ownership.WorkerIdentity,
            ["expiresAt"] = ownership.ExpiresAt
        };

    private static TrackerException PartialFinish(
        WorkItemId id,
        string displayId,
        string targetStatus,
        Exception cause,
        bool statusApplied = false,
        bool archived = false)
    {
        var causeCode = cause is TrackerException trackerException
            ? trackerException.Code
            : "UNEXPECTED_ERROR";
        var failedStage = cause is TrackerException partial &&
                          partial.Details.TryGetValue("failedStage", out var stage)
            ? stage
            : "claimRelease";
        return new TrackerException(
            "PARTIAL_FINISH",
            $"Work item '{id}' was only partially finished. Retry the same finish command.",
            10,
            new Dictionary<string, object?>
            {
                ["id"] = id.Value,
                ["displayId"] = displayId,
                ["targetStatus"] = targetStatus,
                ["statusApplied"] = statusApplied,
                ["archived"] = archived,
                ["claimReleased"] = false,
                ["failedStage"] = failedStage,
                ["causeCode"] = causeCode,
                ["retry"] = "Retry the same finish command."
            },
            cause);
    }
}
