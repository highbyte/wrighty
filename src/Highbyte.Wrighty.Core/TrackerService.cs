using Highbyte.Wrighty.AgentContext;
using Highbyte.Wrighty.Backends;
using Highbyte.Wrighty.Claims;
using Highbyte.Wrighty.Configuration;
using Highbyte.Wrighty.Errors;
using Highbyte.Wrighty.Models;

namespace Highbyte.Wrighty;

public sealed class TrackerService(ITrackerBackendRegistry backends)
{
    private static readonly string[] ContextApprovalPendingFields = ["contextApproval"];

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

    public Task<AdoptWorkItemResult> AdoptAsync(
        TrackerConfig config,
        string reference,
        AdoptWorkItemOptions options,
        CancellationToken cancellationToken)
    {
        if (Backend(config) is not IExistingWorkItemAdoptionBackend adoption)
        {
            var guidance = string.Equals(
                config.Backend,
                "local-markdown",
                StringComparison.OrdinalIgnoreCase)
                ? " Unmanaged Markdown documents use 'wrighty import --in-place <path>'."
                : string.Empty;
            throw new TrackerException(
                "NOT_SUPPORTED",
                $"Adoption is not supported by backend '{config.Backend}'.{guidance}",
                3);
        }

        return adoption.AdoptAsync(config, reference, options, cancellationToken);
    }

    public Task ValidateImportFieldsAsync(
        TrackerConfig config,
        string status,
        string? priority,
        CancellationToken cancellationToken)
    {
        if (Backend(config) is not IWorkItemImportTargetBackend importTarget)
        {
            throw new TrackerException(
                "NOT_SUPPORTED",
                $"Backend '{config.Backend}' does not support remote import validation.",
                3);
        }
        return importTarget.ValidateImportFieldsAsync(
            config,
            status,
            priority,
            cancellationToken);
    }

    public Task ArchiveImportedAsync(
        TrackerConfig config,
        WorkItemId id,
        CancellationToken cancellationToken)
    {
        if (Backend(config) is not IWorkItemImportTargetBackend importTarget)
        {
            throw new TrackerException(
                "NOT_SUPPORTED",
                $"Backend '{config.Backend}' does not support imported archive state.",
                3);
        }
        return importTarget.ArchiveImportedAsync(config, id, cancellationToken);
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

    public async Task<UpdateWorkItemResult> UpdateAsync(
        TrackerConfig config, WorkItemId id, WorkItemPatch patch, string? expectedRevision,
        ClaimHandle? claimHandle, CancellationToken cancellationToken,
        bool applyWorkerQueue = true)
    {
        var workerQueueRule = new WorkerQueueRuleResult(patch, false);
        if (applyWorkerQueue)
            workerQueueRule = await ApplyWorkerQueueRuleAsync(
                config, id, patch, cancellationToken);
        patch = workerQueueRule.Patch;
        if (patch.AutomaticExecutionAllowed is { IsSpecified: true, Value: false })
            patch = patch with { DispatchState = OptionalValue<string?>.From(null) };
        WorkItemPatchValidator.Validate(patch);
        var result = await Backend(config).UpdateAsync(
            config,
            id,
            new UpdateWorkItemOperation(
                patch,
                patch.Status.IsSpecified && config.ShouldArchiveStatus(patch.Status.Value),
                expectedRevision,
                claimHandle),
            cancellationToken);
        if (!workerQueueRule.CycleContextApproval)
            return result;

        try
        {
            await Backend(config).CycleContextApprovalAsync(config, id, cancellationToken);
            return result;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new TrackerException(
                "PARTIAL_UPDATE",
                $"Work item '{id}' entered '{config.DefaultPickFrom}', but its context " +
                "approval could not be refreshed.",
                10,
                new Dictionary<string, object?>
                {
                    ["id"] = id.Value,
                    ["appliedFields"] = result.ChangedFields.ToArray(),
                    ["pendingFields"] = ContextApprovalPendingFields
                },
                exception);
        }
    }

    /// <summary>
    /// The worker queue (on by default, <c>worker.useWorkerQueue</c>): an operator moving an item
    /// into the pick-from status through a Wrighty surface authorizes automatic execution and, on
    /// a backend with a projected context-approval field, cycles that field through Needs review to
    /// Approved. Moving out revokes execution only: approval remains content authority until an
    /// edit invalidates its coverage or someone resets it. Only operator surfaces reach this: the
    /// worker's own status moves pass <c>applyWorkerQueue: false</c> so a pick, finish, or
    /// refusal-restore can never self-authorize. An explicitly patched execution flag always wins.
    /// </summary>
    private async Task<WorkerQueueRuleResult> ApplyWorkerQueueRuleAsync(
        TrackerConfig config,
        WorkItemId id,
        WorkItemPatch patch,
        CancellationToken cancellationToken)
    {
        if (!config.EffectiveWorker.UseWorkerQueue ||
            !patch.Status.IsSpecified ||
            patch.AutomaticExecutionAllowed.IsSpecified)
            return new WorkerQueueRuleResult(patch, false);

        var current = await Backend(config).GetAsync(config, id, cancellationToken);
        if (current is null)
            return new WorkerQueueRuleResult(patch, false); // the update itself reports not-found

        var entersQueue = IsPickFrom(config, patch.Status.Value) &&
                          !IsPickFrom(config, current.Status);
        var leavesQueue = !IsPickFrom(config, patch.Status.Value) &&
                          IsPickFrom(config, current.Status);
        if (entersQueue)
        {
            return new WorkerQueueRuleResult(
                patch with { AutomaticExecutionAllowed = OptionalValue<bool>.From(true) },
                CycleContextApproval: current.ContextApprovalFieldApproved is not null);
        }
        if (leavesQueue)
        {
            return new WorkerQueueRuleResult(
                patch with { AutomaticExecutionAllowed = OptionalValue<bool>.From(false) },
                false);
        }
        return new WorkerQueueRuleResult(patch, false);
    }

    private readonly record struct WorkerQueueRuleResult(
        WorkItemPatch Patch,
        bool CycleContextApproval);

    private static bool IsPickFrom(TrackerConfig config, string? status) =>
        string.Equals(status, config.DefaultPickFrom, StringComparison.OrdinalIgnoreCase);

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
                $"Work item '{id}' is claimed by installation {result.InstallationId} until {result.ExpiresAt:O}.",
                6,
                new Dictionary<string, object?>
                {
                    ["id"] = id.Value,
                    ["installationId"] = result.InstallationId,
                    ["claimantId"] = Short(result.ClaimantId),
                    ["claimantKind"] = result.ClaimantKind,
                    ["agent"] = result.Agent,
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
                    ["agent"] = result.Agent,
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
        RenewClaimAsync(config, id, handle, workspacePath, sessionId, branch: null, cancellationToken);

    public Task<ClaimResult> RenewClaimAsync(TrackerConfig config, WorkItemId id,
        ClaimHandle handle, string? workspacePath, string? sessionId, string? branch,
        CancellationToken cancellationToken) =>
        Backend(config).RenewClaimAsync(
            config, id, handle, workspacePath, sessionId, branch, cancellationToken);

    public Task<ClaimOwnershipResult> GetClaimOwnershipAsync(TrackerConfig config, WorkItemId id,
        CancellationToken cancellationToken) => Backend(config).GetClaimOwnershipAsync(config, id, cancellationToken);

    public Task<AgentSessionRecord?> GetAgentSessionAsync(
        TrackerConfig config,
        WorkItemId id,
        CancellationToken cancellationToken) =>
        Backend(config).GetAgentSessionAsync(config, id, cancellationToken);

    public Task RecordRunReportAsync(
        TrackerConfig config,
        WorkItemId id,
        ApprovedContext.AgentRunReport report,
        CancellationToken cancellationToken) =>
        Backend(config).RecordRunReportAsync(config, id, report, cancellationToken);

    public Task RecordSessionContextAsync(
        TrackerConfig config,
        WorkItemId id,
        ApprovedContext.SessionContextMetadata context,
        CancellationToken cancellationToken) =>
        Backend(config).RecordSessionContextAsync(config, id, context, cancellationToken);

    public Task RecordContinuationAsync(
        TrackerConfig config,
        WorkItemId id,
        ApprovedContext.SessionContinuationState continuation,
        CancellationToken cancellationToken) =>
        Backend(config).RecordContinuationAsync(config, id, continuation, cancellationToken);

    public Task RecordRunOutcomeAsync(
        TrackerConfig config,
        WorkItemId id,
        RunOutcome outcome,
        string? finalMessage,
        DateTimeOffset endedAt,
        Workers.AgentFailure? failure,
        CancellationToken cancellationToken) =>
        Backend(config).RecordRunOutcomeAsync(
            config, id, outcome, finalMessage, endedAt, failure, cancellationToken);

    public Task RecordExecutionSelectionAsync(
        TrackerConfig config,
        WorkItemId id,
        Workers.ExecutionSelection selection,
        CancellationToken cancellationToken) =>
        Backend(config).RecordExecutionSelectionAsync(config, id, selection, cancellationToken);

    public Task RecordPendingDispatchAsync(
        TrackerConfig config,
        WorkItemId id,
        Workers.PendingDispatch dispatch,
        CancellationToken cancellationToken) =>
        Backend(config).RecordPendingDispatchAsync(config, id, dispatch, cancellationToken);

    public Task ClearPendingDispatchAsync(
        TrackerConfig config,
        WorkItemId id,
        CancellationToken cancellationToken) =>
        Backend(config).ClearPendingDispatchAsync(config, id, cancellationToken);

    public Task PresentDispatchAsync(
        TrackerConfig config,
        WorkItemId id,
        Workers.DispatchInfo dispatch,
        CancellationToken cancellationToken) =>
        Backend(config).PresentDispatchAsync(
            config, id, dispatch, cancellationToken);

    public Task PostHandoverAsync(
        TrackerConfig config,
        Workers.HandoverContent content,
        CancellationToken cancellationToken) =>
        Backend(config).PostHandoverAsync(config, content, cancellationToken);

    public Task ResolveHandoverAsync(
        TrackerConfig config,
        WorkItemId id,
        string reason,
        CancellationToken cancellationToken) =>
        Backend(config).ResolveHandoverAsync(config, id, reason, cancellationToken);

    /// <summary>Decision 10's reapproval cycle, delegated to the backend's approval surface.</summary>
    public Task CycleContextApprovalAsync(
        TrackerConfig config,
        WorkItemId id,
        CancellationToken cancellationToken) =>
        Backend(config).CycleContextApprovalAsync(config, id, cancellationToken);

    /// <summary>Revokes base context approval without granting any new authority.</summary>
    public Task InvalidateContextApprovalAsync(
        TrackerConfig config,
        WorkItemId id,
        CancellationToken cancellationToken) =>
        Backend(config).InvalidateContextApprovalAsync(config, id, cancellationToken);

    public Task ReleaseAsync(
        TrackerConfig config,
        WorkItemId id,
        CancellationToken cancellationToken) =>
        Backend(config).ReleaseAsync(config, id, cancellationToken);

    /// <summary>
    /// Ends a fenced claim. <paramref name="dispatchState"/> is required so the decision is made
    /// where the caller knows the answer; see <see cref="DispatchStateOnRelease"/>.
    /// </summary>
    public Task ReleaseAsync(TrackerConfig config, WorkItemId id, ClaimHandle handle,
        bool overrideClaimant, DispatchStateOnRelease dispatchState,
        CancellationToken cancellationToken) =>
        Backend(config).ReleaseAsync(
            config, id, handle, overrideClaimant, dispatchState, cancellationToken);

    public Task RequeueAsync(
        TrackerConfig config,
        WorkItemId id,
        ClaimHandle handle,
        CancellationToken cancellationToken) =>
        Backend(config).RequeueAsync(config, id, handle, cancellationToken);

    public Task QueuePausedAsync(
        TrackerConfig config,
        WorkItemId id,
        CancellationToken cancellationToken) =>
        Backend(config).QueuePausedAsync(config, id, cancellationToken);

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
        OperationalStatuses.Resolve(
            snapshot.Item, snapshot.Claim, snapshot.Session, config.DefaultPickFrom,
            config.DefaultFinishTo));

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
        if (!alreadyAtTarget || initial.DispatchState is not null)
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
                $"Work item '{id}' is claimed by another installation.",
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
                DispatchState: OptionalValue<string?>.From(null));
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
            await backend.ReleaseAsync(
                config, id, handle, false, DispatchStateOnRelease.Clear, cancellationToken);
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

    /// <param name="preClaimGate">
    /// A final asynchronous veto on a candidate that passed <paramref name="eligibility"/>, run
    /// before any claim is attempted. It exists for verdicts that need a read of their own — the
    /// worker's advisory approved-context probe — so an item already known to be refusable is
    /// passed over entirely rather than claimed, status-moved, refused, and handed back. Ordered
    /// after <paramref name="eligibility"/> deliberately: the gate may be expensive, and a
    /// candidate the cheap checks reject must never pay for it.
    /// </param>
    public async Task<PickWorkItemResult> PickWithClaimAsync(
        TrackerConfig config,
        string? fromStatus,
        string? toStatus,
        AgentExecutionContext agentContext,
        CancellationToken cancellationToken,
        Func<WorkItemDetail, bool>? eligibility = null,
        Func<WorkItemDetail, CancellationToken, Task<bool>>? preClaimGate = null)
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
            if (!await IsSelectableAsync(
                    backend, config, candidate, eligibility, preClaimGate, cancellationToken))
                continue;
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

    /// <summary>
    /// Whether a candidate survives the caller's filters: the cheap synchronous eligibility check
    /// first, then the potentially expensive pre-claim gate, with the detail read paid only when
    /// at least one of them needs it.
    /// </summary>
    private static async Task<bool> IsSelectableAsync(
        ITrackerBackend backend,
        TrackerConfig config,
        WorkItemSummary candidate,
        Func<WorkItemDetail, bool>? eligibility,
        Func<WorkItemDetail, CancellationToken, Task<bool>>? preClaimGate,
        CancellationToken cancellationToken)
    {
        if (eligibility is null && preClaimGate is null)
            return true;
        var detail = await backend.GetAsync(config, candidate.Id, cancellationToken);
        if (detail is null || (eligibility is not null && !eligibility(detail)))
            return false;
        return preClaimGate is null || await preClaimGate(detail, cancellationToken);
    }

    private static WorkItemSummary Summary(WorkItemDetail detail) => new(
        detail.Id,
        detail.Title,
        detail.Url,
        detail.Status,
        detail.Priority,
        detail.Archived,
        detail.AutomaticExecutionAllowed,
        detail.AgentPolicy,
        detail.DispatchState);

    private static string? Short(string? value) => value is null || value.Length <= 12 ? value : $"{value[..12]}…";

    private static IReadOnlyDictionary<string, object?> OwnershipDetails(
        ClaimOwnershipResult ownership) => new Dictionary<string, object?>
        {
            ["installationId"] = ownership.InstallationId,
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
        // A denied local write is environment-permanent for the calling process — a sandboxed
        // agent that cannot write outside its workspace will be denied identically on every
        // retry — so it gets its own cause and honest guidance instead of the generic
        // "UNEXPECTED_ERROR" plus a retry hint that can never come true.
        var deniedWrite = cause is UnauthorizedAccessException ||
                          cause is IOException;
        var causeCode = cause switch
        {
            TrackerException trackerException => trackerException.Code,
            _ when deniedWrite => "LOCAL_WRITE_DENIED",
            _ => "UNEXPECTED_ERROR"
        };
        var failedStage = cause is TrackerException partial &&
                          partial.Details.TryGetValue("failedStage", out var stage)
            ? stage
            : "claimRelease";
        var retry = deniedWrite
            ? "A local file write was denied; retrying from this environment will fail the same " +
              "way. Complete the finish from the worker host or an unsandboxed shell."
            : "Retry the same finish command.";
        return new TrackerException(
            "PARTIAL_FINISH",
            $"Work item '{id}' was only partially finished. {retry}",
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
                ["retry"] = retry
            },
            cause);
    }
}
