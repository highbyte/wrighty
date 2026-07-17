using System.Text.Json;
using Highbyte.Wrighty.Claims;
using Highbyte.Wrighty.Errors;
using Highbyte.Wrighty.LocalMarkdown;

namespace Highbyte.Wrighty.Models;

public sealed record WorkItemSummary(
    WorkItemId Id,
    string Title,
    string? Url,
    string? Status,
    string? Priority,
    bool Archived = false);

public sealed record WorkItemDetail(
    WorkItemId Id,
    string Title,
    string Body,
    string? Url,
    string? Status,
    string? Priority,
    bool Archived = false,
    IReadOnlyDictionary<string, JsonElement>? Fields = null,
    string? RawFrontmatter = null)
{
    public IReadOnlyDictionary<string, JsonElement> EffectiveFields =>
        Fields ?? EmptyFields;

    private static readonly IReadOnlyDictionary<string, JsonElement> EmptyFields =
        new Dictionary<string, JsonElement>();
}

public enum ArchiveScope
{
    Active,
    Archived,
    All
}

public sealed record ListWorkItemsRequest(
    string? Status,
    int? Limit,
    ArchiveScope ArchiveScope = ArchiveScope.Active,
    IReadOnlyDictionary<string, string>? Fields = null);

public sealed record CreateWorkItemRequest(
    string Title,
    string Body,
    string? Status,
    string? Priority,
    IReadOnlyDictionary<string, string?>? Fields = null);

public sealed record CreateWorkItemResult(
    WorkItemId Id,
    string? Url,
    WorkItemDetail? Item,
    string CreationAttemptId = "",
    CreateDisposition Disposition = CreateDisposition.Created,
    IReadOnlyList<string>? ReconciledStages = null)
{
    public IReadOnlyList<string> EffectiveReconciledStages => ReconciledStages ?? [];
}

public enum CreateDisposition
{
    Created,
    Resumed
}

public sealed record CreateWorkItemOperation(
    CreateWorkItemRequest Request,
    bool ArchiveAfterCreate,
    string CreationAttemptId = "");

public readonly record struct OptionalValue<T>(bool IsSpecified, T? Value)
{
    public static OptionalValue<T> Unspecified => default;

    public static OptionalValue<T> From(T? value) => new(true, value);
}

public sealed record WorkItemPatch(
    OptionalValue<string> Title,
    OptionalValue<string> Body,
    OptionalValue<string> Status,
    OptionalValue<string?> Priority,
    OptionalValue<IReadOnlyDictionary<string, string?>> Fields = default)
{
    public bool HasChanges =>
        Title.IsSpecified || Body.IsSpecified || Status.IsSpecified || Priority.IsSpecified || Fields.IsSpecified;

    public static WorkItemPatch StatusOnly(string status) => new(
        OptionalValue<string>.Unspecified,
        OptionalValue<string>.Unspecified,
        OptionalValue<string>.From(status),
        OptionalValue<string?>.Unspecified,
        OptionalValue<IReadOnlyDictionary<string, string?>>.Unspecified);
}

public sealed record UpdateWorkItemResult(
    WorkItemDetail Item,
    bool Changed,
    IReadOnlyList<string> ChangedFields);

public sealed record UpdateWorkItemOperation(
    WorkItemPatch Patch,
    bool ArchiveAfterUpdate,
    string? ExpectedRevision = null,
    ClaimHandle? ClaimHandle = null);

public sealed record WorkItemClaimSummary(
    ClaimOwnershipState State,
    string? WorkerIdentity = null,
    DateTimeOffset? ExpiresAt = null,
    string? AgentType = null,
    string? SessionId = null,
    string ClaimantKind = "unknown",
    string? ClaimantId = null,
    bool TakeoverAvailable = false);

public sealed record DashboardWorkItem(
    WorkItemSummary Item,
    WorkItemClaimSummary Claim);

public sealed record DashboardSnapshot(
    IReadOnlyList<string> Statuses,
    IReadOnlyList<string> Priorities,
    IReadOnlyList<DashboardWorkItem> Items,
    string Revision);

public sealed record EditableWorkItem(
    WorkItemDetail Item,
    string Revision,
    WorkItemClaimSummary Claim);

public sealed record ArchiveWorkItemResult(
    WorkItemDetail Item,
    bool Changed,
    bool Archived);

public enum FinishDisposition
{
    Finished,
    AlreadyFinished
}

public sealed record FinishWorkItemResult(
    WorkItemDetail Item,
    FinishDisposition Disposition,
    bool StatusChanged,
    bool ClaimReleased);

public sealed record PickWorkItemResult(WorkItemSummary Item, ClaimResult Claim);

public static class WorkItemPatchValidator
{
    public static void Validate(WorkItemPatch patch)
    {
        if (!patch.HasChanges)
        {
            throw new TrackerException(
                "ARGUMENT_INVALID",
                "At least one work-item field must be specified.",
                2);
        }

        if (patch.Title.IsSpecified &&
            (string.IsNullOrWhiteSpace(patch.Title.Value) ||
             patch.Title.Value.Length > 256 ||
             patch.Title.Value.Contains('\r') ||
             patch.Title.Value.Contains('\n')))
        {
            throw new TrackerException(
                "ARGUMENT_INVALID",
                "title must be a non-empty single line of at most 256 characters.",
                2);
        }

        if (patch.Body.IsSpecified && patch.Body.Value is null)
        {
            throw new TrackerException("ARGUMENT_INVALID", "body cannot be null.", 2);
        }

        if (patch.Status.IsSpecified && string.IsNullOrWhiteSpace(patch.Status.Value))
        {
            throw new TrackerException("ARGUMENT_INVALID", "status cannot be empty.", 2);
        }

        if (patch.Priority.IsSpecified &&
            patch.Priority.Value is not null &&
            string.IsNullOrWhiteSpace(patch.Priority.Value))
        {
            throw new TrackerException("ARGUMENT_INVALID", "priority cannot be empty.", 2);
        }

        if (patch.Fields.IsSpecified)
        {
            foreach (var field in patch.Fields.Value ?? new Dictionary<string, string?>())
            {
                LocalMarkdownReservedFields.ValidateCustomFieldName(field.Key);
            }
        }
    }
}
