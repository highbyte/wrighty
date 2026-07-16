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
    IWorkItemMutationGuard mutationGuard) : ITrackerBackend
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

    public async Task<CreateWorkItemResult> CreateAsync(
        TrackerConfig config,
        CreateWorkItemOperation operation,
        CancellationToken cancellationToken)
    {
        RejectFields(operation.Request.Fields);
        return await workItems.CreateAsync(config, operation, cancellationToken);
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

        var ownership = await claims.GetOwnershipAsync(config, id, cancellationToken);
        if (ownership.State != ClaimOwnershipState.OwnedByCurrent)
        {
            throw new TrackerException(
                "CLAIM_REQUIRED",
                $"Work item '{id}' must be claimed by the current worker before it can be updated.",
                6,
                ClaimMutationGuard.OwnershipDetails(ownership));
        }

        var updated = await workItems.UpdateAsync(config, id, operation.Patch, cancellationToken);
        if (!operation.ArchiveAfterUpdate)
        {
            return updated;
        }

        try
        {
            var archived = await ArchiveAsync(config, id, cancellationToken);
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

    public async Task<ClaimResult> TryClaimAsync(
        TrackerConfig config,
        WorkItemId id,
        AgentExecutionContext agentContext,
        CancellationToken cancellationToken)
    {
        await projects.EnsureAgentContextSchemaAsync(config, cancellationToken);
        var item = await FindProjectItemAsync(config, id, ArchiveScope.Active, cancellationToken);
        var result = await claims.TryClaimAsync(config, id, agentContext, cancellationToken);
        if (result.Outcome == ClaimOutcome.HeldByOther)
        {
            return result;
        }

        await mutationGuard.EnsureOwnedAsync(config, id, cancellationToken);
        try
        {
            await projects.UpdateAgentContextAsync(
                config,
                item,
                result.AgentType,
                result.SessionId,
                cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw PartialUpdate(id, "agentContext", exception);
        }

        return result;
    }

    public Task<ClaimOwnershipResult> GetClaimOwnershipAsync(
        TrackerConfig config,
        WorkItemId id,
        CancellationToken cancellationToken) => claims.GetOwnershipAsync(config, id, cancellationToken);

    public async Task ReleaseAsync(
        TrackerConfig config,
        WorkItemId id,
        CancellationToken cancellationToken)
    {
        await claims.ReleaseAsync(config, id, cancellationToken);
        try
        {
            var item = await FindProjectItemAsync(config, id, ArchiveScope.All, cancellationToken);
            await projects.UpdateAgentContextAsync(config, item, null, null, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw PartialUpdate(id, "agentContextClear", exception);
        }
    }

    public async Task<ArchiveWorkItemResult> ArchiveAsync(
        TrackerConfig config,
        WorkItemId id,
        CancellationToken cancellationToken)
    {
        var item = await FindProjectItemAsync(config, id, ArchiveScope.All, cancellationToken);
        if (item.Summary.Archived)
        {
            var ownership = await claims.GetOwnershipAsync(config, id, cancellationToken);
            if (ownership.State == ClaimOwnershipState.OwnedByCurrent)
            {
                await ReleaseAsync(config, id, cancellationToken);
            }

            return new ArchiveWorkItemResult(
                await RequiredDetailAsync(config, id, cancellationToken),
                false,
                true);
        }

        await mutationGuard.EnsureOwnedAsync(config, id, cancellationToken);
        await projects.ArchiveAsync(config, item, cancellationToken);
        try
        {
            await claims.ReleaseAsync(config, id, cancellationToken);
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
            await projects.UpdateAgentContextAsync(config, item, null, null, cancellationToken);
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
