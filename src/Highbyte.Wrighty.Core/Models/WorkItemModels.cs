using System.Text.Json;
using Highbyte.Wrighty.AgentContext;
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
    bool Archived = false,
    bool AutomationEligible = false,
    string? PreferredAgent = null,
    string? WorkerState = null);

public sealed record WorkItemDetail(
    WorkItemId Id,
    string Title,
    string Body,
    string? Url,
    string? Status,
    string? Priority,
    bool Archived = false,
    IReadOnlyDictionary<string, JsonElement>? Fields = null,
    string? RawFrontmatter = null,
    bool AutomationEligible = false,
    string? PreferredAgent = null,
    IReadOnlyList<string>? Labels = null,
    string? WorkerState = null)
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
    IReadOnlyDictionary<string, string?>? Fields = null,
    bool AutomationEligible = false,
    string? PreferredAgent = null);

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

public sealed record AdoptWorkItemOptions(
    string? Status,
    string? Priority,
    bool AutomationEligible,
    string? PreferredAgent);

public enum AdoptDisposition
{
    Adopted,
    Reconciled,
    AlreadyAdopted
}

public sealed record AdoptWorkItemResult(
    WorkItemId Id,
    string SourceReference,
    string? Url,
    AdoptDisposition Disposition,
    IReadOnlyList<string> AppliedStages,
    IReadOnlyList<string> PendingStages);

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
    OptionalValue<IReadOnlyDictionary<string, string?>> Fields = default,
    OptionalValue<bool> AutomationEligible = default,
    OptionalValue<string?> PreferredAgent = default,
    OptionalValue<string?> WorkerState = default)
{
    public bool HasChanges =>
        Title.IsSpecified || Body.IsSpecified || Status.IsSpecified || Priority.IsSpecified ||
        Fields.IsSpecified || AutomationEligible.IsSpecified || PreferredAgent.IsSpecified ||
        WorkerState.IsSpecified;

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
    bool TakeoverAvailable = false,
    string? WorkspacePath = null)
{
    public static WorkItemClaimSummary FromOwnership(ClaimOwnershipResult ownership) => new(
        ownership.State,
        ownership.WorkerIdentity,
        ownership.ExpiresAt,
        ownership.AgentType,
        ownership.SessionId,
        ownership.ClaimantKind,
        ownership.ClaimantId,
        ownership.TakeoverAvailable,
        ownership.WorkspacePath);
}

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
            throw new TrackerException(
                "ARGUMENT_INVALID",
                "At least one work-item field must be specified.",
                2);

        ValidateTitle(patch.Title);
        ValidateBody(patch.Body);
        ValidateStatus(patch.Status);
        ValidatePriority(patch.Priority);
        ValidateFields(patch.Fields);
        ValidatePreferredAgent(patch.PreferredAgent);
        if (patch.WorkerState.IsSpecified)
            WorkerDispatchStates.Validate(patch.WorkerState.Value);
    }

    private static void ValidateTitle(OptionalValue<string> title)
    {
        if (!title.IsSpecified)
            return;
        var value = title.Value;
        if (string.IsNullOrWhiteSpace(value) ||
            value.Length > 256 ||
            value.Contains('\r') ||
            value.Contains('\n'))
            throw new TrackerException(
                "ARGUMENT_INVALID",
                "title must be a non-empty single line of at most 256 characters.",
                2);
    }

    private static void ValidateBody(OptionalValue<string> body)
    {
        if (body.IsSpecified && body.Value is null)
            throw new TrackerException("ARGUMENT_INVALID", "body cannot be null.", 2);
    }

    private static void ValidateStatus(OptionalValue<string> status)
    {
        if (status.IsSpecified && string.IsNullOrWhiteSpace(status.Value))
            throw new TrackerException("ARGUMENT_INVALID", "status cannot be empty.", 2);
    }

    private static void ValidatePriority(OptionalValue<string?> priority)
    {
        if (priority is { IsSpecified: true, Value: not null } &&
            string.IsNullOrWhiteSpace(priority.Value))
            throw new TrackerException("ARGUMENT_INVALID", "priority cannot be empty.", 2);
    }

    private static void ValidateFields(
        OptionalValue<IReadOnlyDictionary<string, string?>> fields)
    {
        if (!fields.IsSpecified)
            return;
        foreach (var field in fields.Value ?? new Dictionary<string, string?>())
            LocalMarkdownReservedFields.ValidateCustomFieldName(field.Key);
    }

    private static void ValidatePreferredAgent(OptionalValue<string?> preferredAgent)
    {
        if (preferredAgent.IsSpecified && preferredAgent.Value is not null &&
            preferredAgent.Value.ToLowerInvariant() is not ("claude" or "codex" or "copilot"))
            throw new TrackerException("ARGUMENT_INVALID",
                "worker agent must be claude, codex, or copilot.", 2);
    }
}

public static class WorkerDispatchStates
{
    public const string NeedsAttention = "needs-attention";
    public const string Queued = "queued";

    public static void Validate(string? value)
    {
        if (value is null)
            return;
        if (value is not (NeedsAttention or Queued))
            throw new TrackerException(
                "ARGUMENT_INVALID",
                $"worker state must be '{NeedsAttention}', '{Queued}', or cleared.",
                2);
    }
}

public static class WorkItemActivities
{
    public const string None = "none";
    public const string Ready = "ready";
    public const string NeedsAttention = "needs-attention";
    public const string Queued = "queued";
    public const string AgentActive = "agent-active";
    public const string HumanEditing = "human-editing";
    public const string AutomationActive = "automation-active";
    public const string PausedSession = "paused-session";

    public static string Resolve(
        WorkItemDetail item,
        WorkItemClaimSummary claim,
        AgentSessionRecord? session,
        string defaultPickFrom) =>
        Resolve(item.WorkerState, item.AutomationEligible, item.Status, claim, session,
            defaultPickFrom);

    public static string Resolve(
        WorkItemSummary item,
        WorkItemClaimSummary claim,
        string defaultPickFrom) =>
        Resolve(item.WorkerState, item.AutomationEligible, item.Status, claim, session: null,
            defaultPickFrom);

    public static string Resolve(
        string? workerState,
        bool automationEligible,
        string? status,
        WorkItemClaimSummary claim,
        AgentSessionRecord? session,
        string defaultPickFrom)
    {
        if (string.Equals(workerState, WorkerDispatchStates.NeedsAttention,
                StringComparison.OrdinalIgnoreCase))
            return NeedsAttention;
        if (claim.State == ClaimOwnershipState.Unclaimed &&
            string.Equals(workerState, WorkerDispatchStates.Queued,
                StringComparison.OrdinalIgnoreCase))
            return Queued;

        if (claim.State != ClaimOwnershipState.Unclaimed)
        {
            return ClaimantKinds.FromStorageValue(claim.ClaimantKind, claim.AgentType) switch
            {
                ClaimantKind.Agent => AgentActive,
                ClaimantKind.Human => HumanEditing,
                ClaimantKind.Automation => AutomationActive,
                _ => None
            };
        }

        if (session is { IsComplete: true } || HasCompleteAddress(claim))
            return PausedSession;
        if (automationEligible &&
            string.Equals(status, defaultPickFrom, StringComparison.OrdinalIgnoreCase))
            return Ready;
        return None;
    }

    private static bool HasCompleteAddress(WorkItemClaimSummary claim) =>
        !string.IsNullOrWhiteSpace(claim.AgentType) &&
        !string.IsNullOrWhiteSpace(claim.SessionId) &&
        !string.IsNullOrWhiteSpace(claim.WorkspacePath);
}

public sealed record WorkItemOperationalState(
    WorkItemDetail Item,
    WorkItemClaimSummary Claim,
    AgentSessionRecord? Session,
    string Activity);

/// <summary>
/// One consistent operational read of a work item: content, claim summary, and recorded agent
/// session, produced by the backend from a single snapshot rather than three separate reads.
/// </summary>
public sealed record WorkItemOperationalSnapshot(
    WorkItemDetail Item,
    WorkItemClaimSummary Claim,
    AgentSessionRecord? Session);
