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
        CancellationToken cancellationToken) =>
        UpdateAsync(config, id, patch, expectedRevision: null, cancellationToken);

    public Task<UpdateWorkItemResult> UpdateAsync(
        TrackerConfig config,
        WorkItemId id,
        WorkItemPatch patch,
        string? expectedRevision,
        CancellationToken cancellationToken)
        => UpdateAsync(config, id, patch, expectedRevision, null, cancellationToken);

    public Task<UpdateWorkItemResult> UpdateAsync(
        TrackerConfig config, WorkItemId id, WorkItemPatch patch, string? expectedRevision,
        ClaimHandle? claimHandle, CancellationToken cancellationToken)
    {
        if (patch.AutomationEligible is { IsSpecified: true, Value: false })
            patch = patch with { WorkerState = OptionalValue<string?>.From(null) };
        WorkItemPatchValidator.Validate(patch);
        return Backend(config).UpdateAsync(
            config,
            id,
            new UpdateWorkItemOperation(
                patch,
                patch.Status.IsSpecified && config.ShouldArchiveStatus(patch.Status.Value),
                expectedRevision,
                claimHandle),
            cancellationToken);
    }

    public Task<DashboardSnapshot> GetDashboardAsync(
        TrackerConfig config,
        ArchiveScope archiveScope,
        CancellationToken cancellationToken) =>
        DashboardBackend(config).GetDashboardAsync(config, archiveScope, cancellationToken);

    public Task<EditableWorkItem> GetEditableAsync(
        TrackerConfig config,
        WorkItemId id,
        CancellationToken cancellationToken) =>
        DashboardBackend(config).GetEditableAsync(config, id, cancellationToken);

    public async Task<ClaimResult> ClaimAsync(
        TrackerConfig config,
        WorkItemId id,
        AgentExecutionContext agentContext,
        CancellationToken cancellationToken,
        string? expectedClaimToken = null)
    {
        var result = await Backend(config).TryClaimAsync(config, id, agentContext, cancellationToken, expectedClaimToken);
        if (result.Outcome == ClaimOutcome.HeldByOther)
        {
            throw new TrackerException(
                "CLAIM_HELD",
                $"Work item '{id}' is claimed by worker {result.WorkerIdentity} until {result.ExpiresAt:O}.",
                6,
                new Dictionary<string, object?>
                {
                    ["id"] = id.Value,
                    ["workerIdentity"] = result.WorkerIdentity,
                    ["claimantId"] = Short(result.ClaimantId),
                    ["claimantKind"] = result.ClaimantKind,
                    ["agentType"] = result.AgentType,
                    ["expiresAt"] = result.ExpiresAt,
                    ["sameInstallation"] = false,
                    ["takeoverAvailable"] = false
                });
        }
        if (result.Outcome == ClaimOutcome.HeldByLocalClaimant)
            throw new TrackerException("CLAIM_HELD_BY_LOCAL_CLAIMANT",
                $"Work item '{id}' is held by another claimant on this installation.", 6,
                new Dictionary<string, object?>
                {
                    ["id"] = id.Value,
                    ["claimantId"] = Short(result.ClaimantId),
                    ["claimantKind"] = result.ClaimantKind,
                    ["agentType"] = result.AgentType,
                    ["expiresAt"] = result.ExpiresAt,
                    ["sameInstallation"] = true,
                    ["takeoverAvailable"] = true
                });

        return result;
    }

    public Task<ClaimResult> TakeoverAsync(TrackerConfig config, WorkItemId id,
        AgentExecutionContext claimantContext, string? currentClaimToken, CancellationToken cancellationToken) =>
        Backend(config).TakeoverAsync(config, id, claimantContext, currentClaimToken, cancellationToken);

    public Task<ClaimResult> RenewClaimAsync(TrackerConfig config, WorkItemId id,
        ClaimHandle handle, string? workspacePath, string? sessionId,
        CancellationToken cancellationToken) =>
        Backend(config).RenewClaimAsync(config, id, handle, workspacePath, sessionId, cancellationToken);

    public Task<ClaimOwnershipResult> GetClaimOwnershipAsync(TrackerConfig config, WorkItemId id,
        CancellationToken cancellationToken) => Backend(config).GetClaimOwnershipAsync(config, id, cancellationToken);

    public Task<AgentSessionRecord?> GetAgentSessionAsync(
        TrackerConfig config,
        WorkItemId id,
        CancellationToken cancellationToken) =>
        Backend(config).GetAgentSessionAsync(config, id, cancellationToken);

    public Task ReleaseAsync(
        TrackerConfig config,
        WorkItemId id,
        CancellationToken cancellationToken) =>
        Backend(config).ReleaseAsync(config, id, cancellationToken);

    public Task ReleaseAsync(TrackerConfig config, WorkItemId id, ClaimHandle handle,
        bool overrideClaimant, CancellationToken cancellationToken) =>
        Backend(config).ReleaseAsync(config, id, handle, overrideClaimant, cancellationToken);

    public Task RequeueAsync(
        TrackerConfig config,
        WorkItemId id,
        ClaimHandle handle,
        CancellationToken cancellationToken) =>
        Backend(config).RequeueAsync(config, id, handle, cancellationToken);

    public async Task<WorkItemOperationalState> GetOperationalAsync(
        TrackerConfig config,
        WorkItemId id,
        CancellationToken cancellationToken)
    {
        var snapshot = await Backend(config).GetOperationalAsync(config, id, cancellationToken)
            ?? throw new TrackerException(
                "WORK_ITEM_NOT_FOUND",
                $"Work item '{id}' was not found in the configured tracker.",
                5,
                new Dictionary<string, object?> { ["id"] = id.Value });
        return Operational(config, snapshot);
    }

    public async Task<IReadOnlyList<WorkItemOperationalState>> ListOperationalAsync(
        TrackerConfig config,
        ListWorkItemsRequest request,
        CancellationToken cancellationToken) =>
        (await Backend(config).ListOperationalAsync(config, request, cancellationToken))
            .Select(snapshot => Operational(config, snapshot))
            .ToArray();

    private static WorkItemOperationalState Operational(
        TrackerConfig config,
        WorkItemOperationalSnapshot snapshot) => new(
        snapshot.Item,
        snapshot.Claim,
        snapshot.Session,
        WorkItemActivities.Resolve(
            snapshot.Item, snapshot.Claim, snapshot.Session, config.DefaultPickFrom));

    public Task<ArchiveWorkItemResult> ArchiveAsync(
        TrackerConfig config,
        WorkItemId id,
        CancellationToken cancellationToken) =>
        Backend(config).ArchiveAsync(config, id, cancellationToken);

    public Task<ArchiveWorkItemResult> ArchiveAsync(TrackerConfig config, WorkItemId id,
        ClaimHandle handle, CancellationToken cancellationToken) =>
        Backend(config).ArchiveAsync(config, id, handle, cancellationToken);

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
        => await FinishAsync(config, id, status, null, cancellationToken);

    public async Task<FinishWorkItemResult> FinishAsync(
        TrackerConfig config, WorkItemId id, string? status, ClaimHandle? handle,
        CancellationToken cancellationToken)
    {
        var targetStatus = string.IsNullOrWhiteSpace(status)
            ? config.DefaultFinishTo
            : status;
        var backend = Backend(config);
        var initial = await GetAsync(config, id, cancellationToken);
        var ownership = await backend.GetClaimOwnershipAsync(config, id, cancellationToken);
        EnsureFinishClaim(id, ownership);
        var alreadyAtTarget = string.Equals(
            initial.Status,
            targetStatus,
            StringComparison.OrdinalIgnoreCase);
        var updateResult = new FinishUpdate(initial, false);
        if (!alreadyAtTarget || initial.WorkerState is not null)
            updateResult = await UpdateForFinishAsync(
                config, id, targetStatus, alreadyAtTarget, handle, backend, cancellationToken);

        if (updateResult.Item.Archived)
        {
            return new FinishWorkItemResult(
                updateResult.Item,
                FinishDisposition.Finished,
                updateResult.StatusChanged,
                true);
        }

        await ReleaseAfterFinishAsync(
            config, id, targetStatus, handle, backend, updateResult.Item, cancellationToken);
        return new FinishWorkItemResult(
            updateResult.Item,
            FinishDisposition.Finished,
            updateResult.StatusChanged,
            true);
    }

    private static void EnsureFinishClaim(
        WorkItemId id,
        ClaimOwnershipResult ownership)
    {
        if (ownership.State == ClaimOwnershipState.HeldByOther)
            throw new TrackerException(
                "CLAIM_HELD",
                $"Work item '{id}' is claimed by another worker.",
                6,
                OwnershipDetails(ownership));
        if (ownership.State == ClaimOwnershipState.Unclaimed)
            throw new TrackerException(
                "CLAIM_REQUIRED",
                $"Work item '{id}' must be claimed by the current worker before it can be finished.",
                6,
                OwnershipDetails(ownership));
    }

    private static async Task<FinishUpdate> UpdateForFinishAsync(
        TrackerConfig config,
        WorkItemId id,
        string targetStatus,
        bool alreadyAtTarget,
        ClaimHandle? handle,
        ITrackerBackend backend,
        CancellationToken cancellationToken)
    {
        try
        {
            var patch = new WorkItemPatch(
                OptionalValue<string>.Unspecified,
                OptionalValue<string>.Unspecified,
                alreadyAtTarget
                    ? OptionalValue<string>.Unspecified
                    : OptionalValue<string>.From(targetStatus),
                OptionalValue<string?>.Unspecified,
                WorkerState: OptionalValue<string?>.From(null));
            var update = await backend.UpdateAsync(
                config,
                id,
                new UpdateWorkItemOperation(
                    patch,
                    config.ShouldArchiveStatus(targetStatus),
                    ClaimHandle: handle),
                cancellationToken);
            return new FinishUpdate(
                update.Item,
                update.ChangedFields.Contains("status", StringComparer.OrdinalIgnoreCase));
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

    private static async Task ReleaseAfterFinishAsync(
        TrackerConfig config,
        WorkItemId id,
        string targetStatus,
        ClaimHandle? handle,
        ITrackerBackend backend,
        WorkItemDetail final,
        CancellationToken cancellationToken)
    {
        try
        {
            if (handle is null)
                throw new TrackerException(
                    "CLAIM_TOKEN_REQUIRED", "Finish requires a claimant ID and token.", 6);
            await backend.ReleaseAsync(config, id, handle, false, cancellationToken);
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
    }

    private sealed record FinishUpdate(WorkItemDetail Item, bool StatusChanged);

    public async Task<WorkItemSummary> PickAsync(
        TrackerConfig config, string? fromStatus, string? toStatus,
        AgentExecutionContext agentContext, CancellationToken cancellationToken) =>
        (await PickWithClaimAsync(config, fromStatus, toStatus, agentContext, cancellationToken)).Item;

    public async Task<PickWorkItemResult> PickWithClaimAsync(
        TrackerConfig config,
        string? fromStatus,
        string? toStatus,
        AgentExecutionContext agentContext,
        CancellationToken cancellationToken,
        Func<WorkItemDetail, bool>? eligibility = null)
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
            if (eligibility is not null)
            {
                var detail = await backend.GetAsync(config, candidate.Id, cancellationToken);
                if (detail is null || !eligibility(detail))
                    continue;
            }
            var claim = await backend.TryClaimAsync(
                config,
                candidate.Id,
                agentContext,
                cancellationToken,
                agentContext.ClaimToken);
            if (claim.Outcome is ClaimOutcome.HeldByOther or ClaimOutcome.HeldByLocalClaimant)
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
                        config.ShouldArchiveStatus(targetStatus),
                        ClaimHandle: new ClaimHandle(agentContext with { ClaimantId = claim.ClaimantId }, claim.ClaimToken)),
                    cancellationToken);
                return new PickWorkItemResult(Summary(update.Item), claim);
            }

            return new PickWorkItemResult(candidate, claim);
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
        detail.Archived,
        detail.AutomationEligible,
        detail.PreferredAgent,
        detail.WorkerState);

    private static string? Short(string? value) => value is null || value.Length <= 12 ? value : $"{value[..12]}…";

    private static IReadOnlyDictionary<string, object?> OwnershipDetails(
        ClaimOwnershipResult ownership) => new Dictionary<string, object?>
        {
            ["workerIdentity"] = ownership.WorkerIdentity,
            ["expiresAt"] = ownership.ExpiresAt
        };

    private ITrackerDashboardBackend DashboardBackend(TrackerConfig config) =>
        Backend(config) as ITrackerDashboardBackend
        ?? throw new TrackerException(
            "WEB_BACKEND_UNSUPPORTED",
            $"The embedded web application does not support backend '{config.Backend}'.",
            3,
            new Dictionary<string, object?> { ["backend"] = config.Backend });

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
