using Highbyte.Wrighty.Errors;

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
    bool Archived = false);

public enum ArchiveScope
{
    Active,
    Archived,
    All
}

public sealed record ListWorkItemsRequest(
    string? Status,
    int? Limit,
    ArchiveScope ArchiveScope = ArchiveScope.Active);

public sealed record CreateWorkItemRequest(
    string Title,
    string Body,
    string? Status,
    string? Priority);

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
    OptionalValue<string?> Priority)
{
    public bool HasChanges =>
        Title.IsSpecified || Body.IsSpecified || Status.IsSpecified || Priority.IsSpecified;

    public static WorkItemPatch StatusOnly(string status) => new(
        OptionalValue<string>.Unspecified,
        OptionalValue<string>.Unspecified,
        OptionalValue<string>.From(status),
        OptionalValue<string?>.Unspecified);
}

public sealed record UpdateWorkItemResult(
    WorkItemDetail Item,
    bool Changed,
    IReadOnlyList<string> ChangedFields);

public sealed record UpdateWorkItemOperation(
    WorkItemPatch Patch,
    bool ArchiveAfterUpdate);

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
    }
}
