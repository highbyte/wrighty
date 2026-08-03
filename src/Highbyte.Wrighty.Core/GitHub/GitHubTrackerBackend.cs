using Highbyte.Wrighty.Addressing;
using Highbyte.Wrighty.AgentContext;
using Highbyte.Wrighty.Backends;
using Highbyte.Wrighty.Claims;
using Highbyte.Wrighty.Configuration;
using Highbyte.Wrighty.Errors;
using Highbyte.Wrighty.Models;
using Highbyte.Wrighty.Projects;

namespace Highbyte.Wrighty.GitHub;

public sealed class GitHubTrackerBackend(
    IProjectClient projects,
    IClaimService claims,
    GitHubWorkItemAddressResolver resolver,
    IWorkItemBackend workItems,
    Func<TimeSpan, CancellationToken, Task>? delay = null)
    : ITrackerBackend, IExistingWorkItemAdoptionBackend, IWorkItemImportTargetBackend
{
    private readonly Func<TimeSpan, CancellationToken, Task> retryDelay =
        delay ?? Task.Delay;

    public string Name => "github";

    public IWorkItemAddressResolver AddressResolver => resolver;

    public async Task<BackendInitializationResult> InitializeAsync(
        TrackerConfig config,
        bool checkOnly,
        CancellationToken cancellationToken)
    {
        var result = await projects.InitializeAsync(config, checkOnly, cancellationToken);
        return new BackendInitializationResult(result.Changed, result.Actions);
    }

    public async Task<IReadOnlyList<WorkItemSummary>> ListAsync(
        TrackerConfig config,
        ListWorkItemsRequest request,
        CancellationToken cancellationToken)
    {
        RejectFields(request.Fields);
        return (await projects.ListAsync(
            config,
            request.Status,
            request.Limit,
            request.ArchiveScope,
            cancellationToken)).Select(item => item.Summary).ToArray();
    }

    public Task<WorkItemDetail?> GetAsync(
        TrackerConfig config,
        WorkItemId id,
        CancellationToken cancellationToken) => workItems.GetAsync(config, id, cancellationToken);

    public async Task<WorkItemOperationalSnapshot?> GetOperationalAsync(
        TrackerConfig config,
        WorkItemId id,
        CancellationToken cancellationToken)
    {
        var item = await workItems.GetAsync(config, id, cancellationToken);
        if (item is null)
        {
            return null;
        }

        // One combined read derives ownership and session from a single comment-chain fetch
        // instead of fetching it once per aspect.
        var state = await claims.GetClaimStateAsync(config, id, cancellationToken);
        return new WorkItemOperationalSnapshot(
            item,
            WorkItemClaimSummary.FromOwnership(state.Ownership),
            state.Session);
    }

    public async Task<CreateWorkItemResult> CreateAsync(
        TrackerConfig config,
        CreateWorkItemOperation operation,
        CancellationToken cancellationToken)
    {
        RejectFields(operation.Request.Fields);
        return await workItems.CreateAsync(config, operation, cancellationToken);
    }

    public async Task<AdoptWorkItemResult> AdoptAsync(
        TrackerConfig config,
        string reference,
        AdoptWorkItemOptions options,
        CancellationToken cancellationToken)
    {
        if (workItems is not IExistingWorkItemAdoptionBackend adoption)
        {
            throw new TrackerException(
                "NOT_SUPPORTED",
                "This GitHub backend does not support adoption.",
                3);
        }

        try
        {
            return await adoption.AdoptAsync(config, reference, options, cancellationToken);
        }
        catch (TrackerException exception)
            when (exception.Code == "WORK_ITEM_REPOSITORY_MISMATCH")
        {
            throw new TrackerException(
                "ADOPT_REPOSITORY_MISMATCH",
                exception.Message,
                exception.ExitCode,
                exception.Details,
                exception);
        }
    }

    public Task ValidateImportFieldsAsync(
        TrackerConfig config,
        string status,
        string? priority,
        CancellationToken cancellationToken) =>
        projects.ValidateCreateFieldsAsync(config, status, priority, cancellationToken);

    public async Task ArchiveImportedAsync(
        TrackerConfig config,
        WorkItemId id,
        CancellationToken cancellationToken)
    {
        var item = await FindProjectItemAsync(
            config,
            id,
            ArchiveScope.All,
            cancellationToken);
        if (!item.Summary.Archived)
        {
            await projects.ArchiveAsync(config, item, cancellationToken);
        }
    }

    public async Task<UpdateWorkItemResult> UpdateAsync(
        TrackerConfig config,
        WorkItemId id,
        UpdateWorkItemOperation operation,
        CancellationToken cancellationToken)
    {
        if (operation.Patch.Fields.IsSpecified)
        {
            throw FieldsNotSupported();
        }

        var handle = operation.ClaimHandle
            ?? throw new TrackerException("CLAIM_TOKEN_REQUIRED", $"Work item '{id}' update requires a claimant ID and token.", 6);
        await claims.ValidateAsync(config, id, handle, cancellationToken);

        var updated = await workItems.UpdateAsync(config, id, operation.Patch, handle, cancellationToken);
        if (operation.Patch.DispatchState is { IsSpecified: true } dispatchState &&
            !IsPendingDispatchState(dispatchState.Value))
        {
            await claims.ClearPendingDispatchAsync(config, id, cancellationToken);
        }
        try { await claims.ValidateAsync(config, id, handle, cancellationToken); }
        catch (TrackerException exception) when (exception.Code is "CLAIM_STALE" or "CLAIM_REQUIRED")
        {
            throw LostDuringUpdate(id, updated.ChangedFields, operation.ArchiveAfterUpdate ? ["archived", "claimRelease"] : [], exception);
        }
        if (!operation.ArchiveAfterUpdate)
        {
            return updated;
        }

        try
        {
            var archived = await ArchiveAsync(config, id, handle, cancellationToken);
            var fields = updated.ChangedFields.Concat(["archived"]).Distinct().ToArray();
            return new UpdateWorkItemResult(archived.Item, true, fields);
        }
        catch (TrackerException exception) when (exception.Code == "PARTIAL_UPDATE")
        {
            throw;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new TrackerException(
                "PARTIAL_UPDATE",
                $"Work item '{id}' was updated, but could not be archived.",
                10,
                new Dictionary<string, object?>
                {
                    ["id"] = id.Value,
                    ["failedStage"] = "archive",
                    ["appliedFields"] = updated.ChangedFields,
                    ["pendingFields"] = new[] { "archived", "claimRelease" }
                },
                exception);
        }
    }

    private static void RejectFields<T>(IReadOnlyDictionary<string, T>? fields)
    {
        if (fields is { Count: > 0 }) throw FieldsNotSupported();
    }

    private static TrackerException FieldsNotSupported() => new(
        "NOT_SUPPORTED",
        "Custom fields are supported only by the Local Markdown backend.",
        3);

    public Task<ClaimResult> TryClaimAsync(TrackerConfig config, WorkItemId id,
        AgentExecutionContext agentContext, CancellationToken cancellationToken) =>
        TryClaimAsync(config, id, agentContext, cancellationToken, null);

    public async Task<ClaimResult> TryClaimAsync(
        TrackerConfig config,
        WorkItemId id,
        AgentExecutionContext agentContext,
        CancellationToken cancellationToken,
        string? expectedClaimToken)
    {
        await projects.EnsureAgentContextSchemaAsync(config, cancellationToken);
        var item = await FindProjectItemAsync(config, id, ArchiveScope.Active, cancellationToken);
        var result = await claims.TryClaimAsync(config, id, agentContext, cancellationToken, expectedClaimToken);
        if (result.Outcome is ClaimOutcome.HeldByOther or ClaimOutcome.HeldByLocalClaimant)
        {
            return result;
        }

        var handle = new ClaimHandle(agentContext with { ClaimantId = result.ClaimantId }, result.ClaimToken);
        await claims.ValidateAsync(config, id, handle, cancellationToken);

        try
        {
            await projects.UpdateClaimantProjectionAsync(
                config,
                item,
                result.ClaimantKind,
                result.ClaimantId,
                result.Agent,
                result.SessionId,
                cancellationToken);
            await claims.ValidateAsync(config, id, handle, cancellationToken);
        }
        catch (TrackerException exception) when (exception.Code is "CLAIM_STALE" or "CLAIM_REQUIRED")
        { throw LostDuringUpdate(id, ["claim"], ["claimantProjection"], exception); }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw PartialUpdate(id, "agentContext", exception);
        }

        return result;
    }

    public async Task<ClaimResult> TakeoverAsync(TrackerConfig config, WorkItemId id,
        AgentExecutionContext claimantContext, string? currentClaimToken, CancellationToken cancellationToken)
    {
        await projects.EnsureAgentContextSchemaAsync(config, cancellationToken);
        var item = await FindProjectItemAsync(config, id, ArchiveScope.Active, cancellationToken);
        var result = await claims.TakeoverAsync(config, id, claimantContext, currentClaimToken, cancellationToken);
        var handle = new ClaimHandle(claimantContext with { ClaimantId = result.ClaimantId }, result.ClaimToken);
        await claims.ValidateAsync(config, id, handle, cancellationToken);
        await projects.UpdateClaimantProjectionAsync(config, item, result.ClaimantKind, result.ClaimantId,
            result.Agent, result.SessionId, cancellationToken);
        try { await claims.ValidateAsync(config, id, handle, cancellationToken); }
        catch (TrackerException exception) when (exception.Code is "CLAIM_STALE" or "CLAIM_REQUIRED")
        { throw LostDuringUpdate(id, ["takeover", "claimantProjection"], [], exception); }
        return result;
    }

    public Task<ClaimResult> RenewClaimAsync(
        TrackerConfig config,
        WorkItemId id,
        ClaimHandle claimHandle,
        string? workspacePath,
        string? sessionId,
        CancellationToken cancellationToken) =>
        RenewClaimAsync(config, id, claimHandle, workspacePath, sessionId, branch: null,
            cancellationToken);

    public async Task<ClaimResult> RenewClaimAsync(
        TrackerConfig config,
        WorkItemId id,
        ClaimHandle claimHandle,
        string? workspacePath,
        string? sessionId,
        string? branch,
        CancellationToken cancellationToken)
    {
        var result = await claims.RenewAsync(
            config, id, claimHandle, workspacePath, sessionId, branch, cancellationToken);
        var item = await FindProjectItemAsync(config, id, ArchiveScope.Active, cancellationToken);
        await projects.UpdateClaimantProjectionAsync(config, item, result.ClaimantKind,
            result.ClaimantId, result.Agent, result.SessionId, cancellationToken);
        // The Project workspace-path field is visible in the Project UI; when shareLocalPaths=false
        // do not publish the absolute path there (the machine-local cache retains it for resume).
        await projects.UpdateWorkspacePathAsync(
            config, item,
            config.EffectiveWorker.ShareLocalPaths ? result.WorkspacePath : null,
            cancellationToken);
        return result;
    }

    public Task<ClaimOwnershipResult> GetClaimOwnershipAsync(
        TrackerConfig config,
        WorkItemId id,
        CancellationToken cancellationToken) => claims.GetOwnershipAsync(config, id, cancellationToken);

    public Task<AgentSessionRecord?> GetAgentSessionAsync(
        TrackerConfig config,
        WorkItemId id,
        CancellationToken cancellationToken) =>
        claims.GetAgentSessionAsync(config, id, cancellationToken);

    public Task RecordRunReportAsync(
        TrackerConfig config,
        WorkItemId id,
        ApprovedContext.AgentRunReport report,
        CancellationToken cancellationToken) =>
        claims.RecordRunReportAsync(config, id, report, cancellationToken);

    public Task PublishRunReportAsync(
        TrackerConfig config,
        WorkItemId id,
        ApprovedContext.AgentRunReport report,
        string? branch,
        CancellationToken cancellationToken) =>
        claims.PublishRunReportAsync(config, id, report, branch, cancellationToken);

    public Task RecordSessionContextAsync(
        TrackerConfig config,
        WorkItemId id,
        ApprovedContext.SessionContextMetadata context,
        CancellationToken cancellationToken) =>
        claims.RecordSessionContextAsync(config, id, context, cancellationToken);

    public Task RecordContinuationAsync(
        TrackerConfig config,
        WorkItemId id,
        ApprovedContext.SessionContinuationState continuation,
        CancellationToken cancellationToken) =>
        claims.RecordContinuationAsync(config, id, continuation, cancellationToken);

    public Task RecordRunOutcomeAsync(
        TrackerConfig config,
        WorkItemId id,
        RunOutcome outcome,
        string? finalMessage,
        DateTimeOffset endedAt,
        Workers.AgentFailure? failure,
        CancellationToken cancellationToken) =>
        claims.RecordRunOutcomeAsync(
            config, id, outcome, finalMessage, endedAt, failure, cancellationToken);

    public Task RecordPendingDispatchAsync(
        TrackerConfig config,
        WorkItemId id,
        Workers.PendingDispatch dispatch,
        CancellationToken cancellationToken) =>
        claims.RecordPendingDispatchAsync(config, id, dispatch, cancellationToken);

    public Task ClearPendingDispatchAsync(
        TrackerConfig config,
        WorkItemId id,
        CancellationToken cancellationToken) =>
        claims.ClearPendingDispatchAsync(config, id, cancellationToken);

    public async Task PresentDispatchAsync(
        TrackerConfig config,
        WorkItemId id,
        Workers.DispatchInfo dispatch,
        CancellationToken cancellationToken)
    {
        try
        {
            var item = await FindProjectItemAsync(
                config, id, ArchiveScope.Active, cancellationToken);
            await projects.UpdateDispatchProjectionAsync(
                config, item, dispatch, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The issue label and installation-local dispatch record are authoritative. Project
            // recovery fields are deliberately best-effort and may not exist until init is rerun.
        }
    }

    public Task PostHandoverAsync(
        TrackerConfig config,
        Workers.HandoverContent content,
        CancellationToken cancellationToken) =>
        claims.PostHandoverAsync(config, content, cancellationToken);

    public Task ResolveHandoverAsync(
        TrackerConfig config,
        WorkItemId id,
        string reason,
        CancellationToken cancellationToken) =>
        claims.ResolveHandoverAsync(config, id, reason, cancellationToken);

    public async Task ReleaseAsync(
        TrackerConfig config,
        WorkItemId id,
        CancellationToken cancellationToken)
    {
        await claims.ReleaseAsync(config, id, cancellationToken);
        await ClearClaimantProjectionAsync(config, id, cancellationToken);
    }

    public async Task ReleaseAsync(TrackerConfig config, WorkItemId id, ClaimHandle claimHandle,
        bool overrideClaimant, CancellationToken cancellationToken)
    {
        await claims.ReleaseAsync(config, id, claimHandle, overrideClaimant, cancellationToken);
        await ClearClaimantProjectionAsync(config, id, cancellationToken);
    }

    /// <summary>
    /// Clears the denormalized claimant/workspace project fields after the authoritative claim
    /// release (the issue comment) has already been posted. Best-effort with respect to an archived
    /// or removed project item: GitHub rejects field writes to an archived item, and an archived
    /// item shows no projection anyway, so a missing/archived item here is not a failure — otherwise
    /// a claim released after archival could never be cleared (PROJECT_ITEM_NOT_FOUND / archived
    /// field-write refusal), stranding it.
    /// </summary>
    private async Task ClearClaimantProjectionAsync(
        TrackerConfig config,
        WorkItemId id,
        CancellationToken cancellationToken)
    {
        GitHubProjectItem item;
        try
        {
            item = await FindProjectItemAsync(config, id, ArchiveScope.All, cancellationToken);
        }
        catch (TrackerException exception) when (exception.Code == "PROJECT_ITEM_NOT_FOUND")
        {
            return;
        }

        if (item.Summary.Archived)
        {
            return;
        }

        try
        {
            await projects.UpdateClaimantProjectionAsync(config, item, null, null, null, null, cancellationToken);
            await projects.UpdateWorkspacePathAsync(config, item, null, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw PartialUpdate(id, "agentContextClear", exception);
        }
    }

    /// <summary>
    /// Queues a waiting session without the caller holding a claim.
    ///
    /// <para>Local Markdown can write this transition directly under its store lock. GitHub cannot:
    /// every dispatch-state write passes <c>ClaimMutationGuard</c>, which refuses an unowned handle
    /// and refuses an unclaimed item alike. So the transition is claim-mediated — acquire a claim as
    /// this worker installation, apply the ordinary requeue, and let that path release it. Every step
    /// is an existing fenced operation; nothing here writes around the guard.</para>
    ///
    /// <para>Acquiring the claim is also the concurrency check that matters. A session whose claim is
    /// still live belongs to a run that has not finished releasing it, and the claim attempt fails —
    /// which is the correct answer, not an error to work around. The caller reports it and the item
    /// is reconsidered once the claim lapses.</para>
    /// </summary>
    public async Task QueuePausedAsync(
        TrackerConfig config,
        WorkItemId id,
        CancellationToken cancellationToken)
    {
        var current = await RequiredDetailAsync(config, id, cancellationToken);
        if (!string.Equals(current.DispatchState, DispatchStates.NeedsAttention,
                StringComparison.OrdinalIgnoreCase))
            throw new TrackerException(
                "WORKER_ITEM_NOT_PAUSED",
                $"Work item '{id}' is no longer waiting for attention.",
                6);

        var session = await claims.GetAgentSessionAsync(config, id, cancellationToken);
        if (session is not { IsComplete: true, FromCurrentInstallation: true })
            throw new TrackerException(
                "RESUME_ADDRESS_UNAVAILABLE",
                $"Work item '{id}' has no complete session recorded on this installation to queue.",
                5);

        // Claimed as this worker installation, which is who resumes it moments later. A distinct
        // claimant would put an identity in the claim history that never runs anything.
        //
        // The recorded session id is carried into the claim, and must be. A claim marker is the
        // authoritative record of the session address, and session resolution prefers it over the
        // machine-local cache: claiming with a null id republishes this item as one whose session
        // has no address, so IsComplete goes false and the item silently stops being resumable —
        // by any surface, not just this one. The cache still holds the truth, which is what makes
        // the damage quiet rather than loud.
        var claimant = new AgentExecutionContext(
            session.Agent,
            session.SessionId,
            AgentContextSource.None,
            ClaimantKind: ClaimantKind.Agent);
        var claim = await TryClaimAsync(config, id, claimant, cancellationToken, null);
        // A retained claim from this installation's own finished run is the ordinary state of a
        // waiting item: the worker keeps the claim on needs-attention so the resume address stays
        // owned, and the lease outlives the run by design. TryClaim reports it HeldByLocalClaimant
        // — the ended run's claimant id is not this call's — so without the takeover below, a
        // trusted reply could not queue anything until the lease lapsed, which on the default
        // sixty-minute lease is exactly the window a clarification arrives in. Taking the claim
        // over is the same fenced token rotation `wrighty edit --takeover --requeue` performs, and
        // needs-attention is only ever set after the run that held this claim has exited.
        //
        // Only an agent's claim. A human or automation claimant holding a needs-attention item is
        // an operator intervening, and displacing them would fence the very claim they are using.
        if (claim.Outcome is ClaimOutcome.HeldByLocalClaimant &&
            ClaimantKinds.FromStorageValue(claim.ClaimantKind) == ClaimantKind.Agent)
            claim = await TakeoverAsync(config, id, claimant, claim.ClaimToken, cancellationToken);
        if (claim.Outcome is not (ClaimOutcome.Acquired or ClaimOutcome.AlreadyOwned
                or ClaimOutcome.TakenOver) ||
            claim.ClaimToken is not { Length: > 0 } token)
            throw new TrackerException(
                "CLAIM_HELD",
                $"Work item '{id}' is still claimed elsewhere, so its session cannot be queued yet.",
                6);

        // Built from what the claim service actually recorded, not from what was asked for. The
        // claimant id is generated during the claim, so a handle assembled from the request carries
        // a null id and fails validation against the live claim — including in the AlreadyOwned
        // case, where the id belongs to the earlier run rather than to this call.
        await RequeueAsync(
            config,
            id,
            new ClaimHandle(
                new AgentExecutionContext(
                    claim.Agent ?? session.Agent,
                    claim.SessionId,
                    AgentContextSource.None,
                    ClaimantKind: ClaimantKinds.FromStorageValue(claim.ClaimantKind),
                    ClaimantId: claim.ClaimantId,
                    ClaimToken: token),
                token),
            cancellationToken);
    }

    public async Task RequeueAsync(
        TrackerConfig config,
        WorkItemId id,
        ClaimHandle claimHandle,
        CancellationToken cancellationToken)
    {
        var current = await RequiredDetailAsync(config, id, cancellationToken);
        if (!current.AutomaticExecutionAllowed)
            throw new TrackerException(
                "WORKER_ITEM_INELIGIBLE",
                $"Work item '{id}' must allow automatic execution before it can be queued.",
                5);
        if (!string.Equals(current.Status, config.DefaultPickTo,
                StringComparison.OrdinalIgnoreCase))
            throw new TrackerException(
                "WORKER_ITEM_INELIGIBLE",
                $"Work item '{id}' must have status '{config.DefaultPickTo}' before it can be queued.",
                5);
        var patch = new WorkItemPatch(
            OptionalValue<string>.Unspecified,
            OptionalValue<string>.Unspecified,
            OptionalValue<string>.Unspecified,
            OptionalValue<string?>.Unspecified,
            DispatchState: OptionalValue<string?>.From(DispatchStates.Queued));
        await workItems.UpdateAsync(config, id, patch, claimHandle, cancellationToken);
        try
        {
            await claims.RequeueAsync(config, id, claimHandle, cancellationToken);
            await claims.ClearPendingDispatchAsync(config, id, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new TrackerException(
                "PARTIAL_UPDATE",
                $"Work item '{id}' was marked queued, but its active claim could not be ended.",
                10,
                new Dictionary<string, object?>
                {
                    ["id"] = id.Value,
                    ["appliedFields"] = new[] { "wrighty.dispatch.state" },
                    ["pendingFields"] = new[] { "claimRequeue" }
                },
                exception);
        }
    }

    public async Task<ArchiveWorkItemResult> ArchiveAsync(
        TrackerConfig config,
        WorkItemId id,
        CancellationToken cancellationToken)
    {
        throw new TrackerException("CLAIM_TOKEN_REQUIRED", $"Archive of '{id}' requires a claimant ID and token.", 6);
    }

    public async Task<ArchiveWorkItemResult> ArchiveAsync(TrackerConfig config, WorkItemId id,
        ClaimHandle claimHandle, CancellationToken cancellationToken)
    {
        var item = await FindProjectItemAsync(config, id, ArchiveScope.All, cancellationToken);
        if (item.Summary.Archived)
        {
            throw new TrackerException("WORK_ITEM_ARCHIVED", $"Work item '{id}' is already archived.", 5);
        }

        await claims.ValidateAsync(config, id, claimHandle, cancellationToken);
        // Release the claim (and clear its projection) BEFORE archiving. Archiving sets
        // isArchived=true, after which GitHub rejects project field writes and the item leaves the
        // Active listing — so a claim released only after archival would strand its projection and
        // could never be re-cleared (this is the archive → PARTIAL_UPDATE → un-releasable claim
        // trap). Releasing first also makes the operation recoverable: if release fails the item is
        // left unarchived and still claimed, not archived-and-stranded.
        try
        {
            await ReleaseAsync(config, id, claimHandle, false, cancellationToken);
        }
        catch (TrackerException exception) when (
            exception.Code is "CLAIM_STALE" or "CLAIM_REQUIRED" or "CLAIM_NOT_OWNER")
        {
            throw LostDuringUpdate(id, [], ["claimRelease"], exception);
        }

        await projects.ArchiveAsync(config, item, cancellationToken);
        await claims.ClearPendingDispatchAsync(config, id, cancellationToken);

        // The item is closed out; trim any handover comment so its next-step instructions do not
        // linger. Housekeeping only — never fail a completed archive for it.
        try
        {
            await claims.ResolveHandoverAsync(
                config, id, "The item was archived.", cancellationToken);
        }
        catch (TrackerException)
        {
        }

        return new ArchiveWorkItemResult(
            await RequiredDetailAsync(config, id, cancellationToken),
            true,
            true);
    }

    private static bool IsPendingDispatchState(string? dispatchState) =>
        dispatchState is DispatchStates.RetryScheduled or
            DispatchStates.HandoffQueued;

    private static TrackerException LostDuringUpdate(WorkItemId id, IReadOnlyList<string> applied,
        IReadOnlyList<string> pending, Exception cause) => new(
            "CLAIM_LOST_DURING_UPDATE",
            $"Work item '{id}' changed on GitHub, but its claim transferred during the update.",
            10,
            new Dictionary<string, object?> { ["id"] = id.Value, ["appliedFields"] = applied, ["pendingFields"] = pending },
            cause);

    /// <summary>
    /// Deliberately claim-free: the approval field is content curation, not item mutation — an
    /// operator performs the same cycle in the Projects UI without holding anything, and requiring
    /// a claim here would block reapproving exactly the paused items the cycle exists to serve.
    /// </summary>
    public async Task CycleContextApprovalAsync(
        TrackerConfig config,
        WorkItemId id,
        CancellationToken cancellationToken)
    {
        var item = await FindProjectItemAsync(config, id, ArchiveScope.Active, cancellationToken);
        await projects.CycleContextApprovalAsync(config, item, cancellationToken);
    }

    public async Task<ArchiveWorkItemResult> UnarchiveAsync(
        TrackerConfig config,
        WorkItemId id,
        CancellationToken cancellationToken)
    {
        var item = await FindProjectItemAsync(config, id, ArchiveScope.All, cancellationToken);
        if (!item.Summary.Archived)
        {
            return new ArchiveWorkItemResult(
                await RequiredDetailAsync(config, id, cancellationToken),
                false,
                false);
        }

        var ownership = await claims.GetOwnershipAsync(config, id, cancellationToken);
        if (ownership.State != ClaimOwnershipState.Unclaimed)
        {
            throw new TrackerException(
                "CLAIM_HELD",
                $"Archived work item '{id}' has an active claim.",
                6,
                ClaimMutationGuard.OwnershipDetails(ownership));
        }

        await projects.UnarchiveAsync(config, item, cancellationToken);
        try
        {
            await projects.UpdateClaimantProjectionAsync(config, item, null, null, null, null, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new TrackerException(
                "PARTIAL_UPDATE",
                $"Work item '{id}' was unarchived, but its current-agent projection could not be cleared.",
                10,
                new Dictionary<string, object?>
                {
                    ["id"] = id.Value,
                    ["failedStage"] = "agentContextClear",
                    ["appliedFields"] = new[] { "archived" },
                    ["pendingFields"] = new[] { "agentContext" }
                },
                exception);
        }

        return new ArchiveWorkItemResult(
            await RequiredDetailAsync(config, id, cancellationToken),
            true,
            false);
    }

    private async Task<GitHubProjectItem> FindProjectItemAsync(
        TrackerConfig config,
        WorkItemId id,
        ArchiveScope scope,
        CancellationToken cancellationToken)
    {
        var effectiveScope = scope == ArchiveScope.All ? ArchiveScope.Active : scope;
        var items = await projects.ListAsync(
            config, null, null, effectiveScope, cancellationToken);
        var item = items.SingleOrDefault(item => item.Summary.Id == id);
        if (item is null && scope == ArchiveScope.All)
        {
            items = await projects.ListAsync(
                config, null, null, ArchiveScope.Archived, cancellationToken);
            item = items.SingleOrDefault(item => item.Summary.Id == id);
        }

        return item ?? throw new TrackerException(
            "PROJECT_ITEM_NOT_FOUND",
            $"Work item '{id}' was not found in the configured Project.",
            5);
    }

    private async Task<WorkItemDetail> RequiredDetailAsync(
        TrackerConfig config,
        WorkItemId id,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var item = await workItems.GetAsync(config, id, cancellationToken);
            if (item is not null)
            {
                return item;
            }

            if (attempt < 4)
            {
                await retryDelay(
                    TimeSpan.FromMilliseconds(250 * (1 << attempt)),
                    cancellationToken);
            }
        }

        throw new TrackerException(
            "WORK_ITEM_NOT_FOUND",
            $"Work item '{id}' was not found in the configured tracker.",
            5);
    }

    private static TrackerException PartialUpdate(
        WorkItemId id,
        string stage,
        Exception exception) => new(
        "PARTIAL_UPDATE",
        $"Work item '{id}' changed, but GitHub projection stage '{stage}' failed.",
        10,
        new Dictionary<string, object?> { ["id"] = id.Value, ["failedStage"] = stage },
        exception);
}
