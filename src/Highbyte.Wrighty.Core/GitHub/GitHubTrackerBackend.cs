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
    IWorkItemBackend workItems)
    : ITrackerBackend, IExistingWorkItemAdoptionBackend, IWorkItemImportTargetBackend
{
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
                result.AgentType,
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
            result.AgentType, result.SessionId, cancellationToken);
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
            result.ClaimantId, result.AgentType, result.SessionId, cancellationToken);
        await projects.UpdateWorkspacePathAsync(config, item, result.WorkspacePath, cancellationToken);
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

    public async Task ReleaseAsync(
        TrackerConfig config,
        WorkItemId id,
        CancellationToken cancellationToken)
    {
        await claims.ReleaseAsync(config, id, cancellationToken);
        try
        {
            var item = await FindProjectItemAsync(config, id, ArchiveScope.All, cancellationToken);
            await projects.UpdateClaimantProjectionAsync(config, item, null, null, null, null, cancellationToken);
            await projects.UpdateWorkspacePathAsync(config, item, null, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw PartialUpdate(id, "agentContextClear", exception);
        }
    }

    public async Task ReleaseAsync(TrackerConfig config, WorkItemId id, ClaimHandle claimHandle,
        bool overrideClaimant, CancellationToken cancellationToken)
    {
        await claims.ReleaseAsync(config, id, claimHandle, overrideClaimant, cancellationToken);
        try
        {
            var item = await FindProjectItemAsync(config, id, ArchiveScope.All, cancellationToken);
            await projects.UpdateClaimantProjectionAsync(config, item, null, null, null, null, cancellationToken);
            await projects.UpdateWorkspacePathAsync(config, item, null, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        { throw PartialUpdate(id, "agentContextClear", exception); }
    }

    public async Task RequeueAsync(
        TrackerConfig config,
        WorkItemId id,
        ClaimHandle claimHandle,
        CancellationToken cancellationToken)
    {
        var current = await RequiredDetailAsync(config, id, cancellationToken);
        if (!current.AutomationEligible)
            throw new TrackerException(
                "WORKER_ITEM_INELIGIBLE",
                $"Work item '{id}' must have wrighty-auto=true before it can be queued.",
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
            WorkerState: OptionalValue<string?>.From(WorkerDispatchStates.Queued));
        await workItems.UpdateAsync(config, id, patch, claimHandle, cancellationToken);
        try
        {
            await claims.RequeueAsync(config, id, claimHandle, cancellationToken);
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
                    ["appliedFields"] = new[] { "wrighty-worker-state" },
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
        await projects.ArchiveAsync(config, item, cancellationToken);
        try { await claims.ValidateAsync(config, id, claimHandle, cancellationToken); }
        catch (TrackerException exception) when (exception.Code is "CLAIM_STALE" or "CLAIM_REQUIRED")
        { throw LostDuringUpdate(id, ["archived"], ["claimRelease"], exception); }
        try
        {
            await claims.ReleaseAsync(config, id, claimHandle, false, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new TrackerException(
                "PARTIAL_UPDATE",
                $"Work item '{id}' was archived, but its claim could not be fully released.",
                10,
                new Dictionary<string, object?>
                {
                    ["id"] = id.Value,
                    ["failedStage"] = "claimRelease",
                    ["appliedFields"] = new[] { "archived" },
                    ["pendingFields"] = new[] { "claimRelease" }
                },
                exception);
        }

        return new ArchiveWorkItemResult(
            await RequiredDetailAsync(config, id, cancellationToken),
            true,
            true);
    }

    private static TrackerException LostDuringUpdate(WorkItemId id, IReadOnlyList<string> applied,
        IReadOnlyList<string> pending, Exception cause) => new(
            "CLAIM_LOST_DURING_UPDATE",
            $"Work item '{id}' changed on GitHub, but its claim transferred during the update.",
            10,
            new Dictionary<string, object?> { ["id"] = id.Value, ["appliedFields"] = applied, ["pendingFields"] = pending },
            cause);

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
        var items = await projects.ListAsync(config, null, null, scope, cancellationToken);
        return items.SingleOrDefault(item => item.Summary.Id == id)
            ?? throw new TrackerException(
                "PROJECT_ITEM_NOT_FOUND",
                $"Work item '{id}' was not found in the configured Project.",
                5);
    }

    private async Task<WorkItemDetail> RequiredDetailAsync(
        TrackerConfig config,
        WorkItemId id,
        CancellationToken cancellationToken) =>
        await workItems.GetAsync(config, id, cancellationToken)
        ?? throw new TrackerException(
            "WORK_ITEM_NOT_FOUND",
            $"Work item '{id}' was not found in the configured tracker.",
            5);

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
