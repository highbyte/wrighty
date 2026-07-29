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
    bool AutomaticExecutionAllowed = false,
    string? AgentPolicy = null,
    string? DispatchState = null);

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
    bool AutomaticExecutionAllowed = false,
    string? AgentPolicy = null,
    IReadOnlyList<string>? Labels = null,
    string? DispatchState = null,
    bool? ContextApprovalFieldApproved = null)
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
    bool AutomaticExecutionAllowed = false,
    string? AgentPolicy = null);

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
    bool AutomaticExecutionAllowed,
    string? AgentPolicy);

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
    OptionalValue<bool> AutomaticExecutionAllowed = default,
    OptionalValue<string?> AgentPolicy = default,
    OptionalValue<string?> DispatchState = default)
{
    public bool HasChanges =>
        Title.IsSpecified || Body.IsSpecified || Status.IsSpecified || Priority.IsSpecified ||
        Fields.IsSpecified || AutomaticExecutionAllowed.IsSpecified || AgentPolicy.IsSpecified ||
        DispatchState.IsSpecified;

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
    string? InstallationId = null,
    DateTimeOffset? ExpiresAt = null,
    string? Agent = null,
    string? SessionId = null,
    string ClaimantKind = "unknown",
    string? ClaimantId = null,
    bool TakeoverAvailable = false,
    string? WorkspacePath = null)
{
    public static WorkItemClaimSummary FromOwnership(ClaimOwnershipResult ownership) => new(
        ownership.State,
        ownership.InstallationId,
        ownership.ExpiresAt,
        ownership.Agent,
        ownership.SessionId,
        ownership.ClaimantKind,
        ownership.ClaimantId,
        ownership.TakeoverAvailable,
        ownership.WorkspacePath);
}

public sealed record DashboardWorkItem(
    WorkItemSummary Item,
    WorkItemClaimSummary Claim,
    bool HasRecordedWorktree = false);

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
        ValidateAgentPolicy(patch.AgentPolicy);
        if (patch.DispatchState.IsSpecified)
            DispatchStates.Validate(patch.DispatchState.Value);
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

    private static void ValidateAgentPolicy(OptionalValue<string?> agentPolicy)
    {
        if (agentPolicy.IsSpecified && agentPolicy.Value is not null &&
            agentPolicy.Value.ToLowerInvariant() is not ("claude" or "codex" or "copilot"))
            throw new TrackerException("ARGUMENT_INVALID",
                "worker agent must be claude, codex, or copilot.", 2);
    }
}

public static class DispatchStates
{
    public const string NeedsAttention = "needs-attention";
    public const string Queued = "queued";
    public const string RetryScheduled = "retry-scheduled";
    public const string HandoffQueued = "handoff-queued";

    public static void Validate(string? value)
    {
        if (value is null)
            return;
        if (value is not (NeedsAttention or Queued or RetryScheduled or HandoffQueued))
            throw new TrackerException(
                "ARGUMENT_INVALID",
                $"dispatch state must be '{NeedsAttention}', '{Queued}', '{RetryScheduled}', " +
                $"'{HandoffQueued}', or cleared.",
                2);
    }
}

public static class OperationalStatuses
{
    public const string None = "none";
    public const string Ready = "ready";
    public const string NeedsAttention = "needs-attention";
    public const string Queued = "queued";
    public const string RetryScheduled = "retry-scheduled";
    public const string HandoffQueued = "handoff-queued";
    public const string AgentActive = "agent-active";
    public const string HumanEditing = "human-editing";
    public const string AutomationActive = "automation-active";
    public const string PausedSession = "paused-session";
    public const string Completed = "completed";

    public static string Resolve(
        WorkItemDetail item,
        WorkItemClaimSummary claim,
        AgentSessionRecord? session,
        string defaultPickFrom,
        string? defaultFinishTo = null) =>
        Resolve(item.DispatchState, item.AutomaticExecutionAllowed, item.Status, claim, session,
            defaultPickFrom, defaultFinishTo);

    public static string Resolve(
        WorkItemSummary item,
        WorkItemClaimSummary claim,
        string defaultPickFrom) =>
        Resolve(item.DispatchState, item.AutomaticExecutionAllowed, item.Status, claim, session: null,
            defaultPickFrom);

    public static string Resolve(
        string? dispatchState,
        bool automaticExecutionAllowed,
        string? status,
        WorkItemClaimSummary claim,
        AgentSessionRecord? session,
        string defaultPickFrom,
        string? defaultFinishTo = null)
    {
        if (string.Equals(dispatchState, DispatchStates.NeedsAttention,
                StringComparison.OrdinalIgnoreCase))
            return NeedsAttention;
        if (claim.State == ClaimOwnershipState.Unclaimed &&
            string.Equals(dispatchState, DispatchStates.Queued,
                StringComparison.OrdinalIgnoreCase))
            return Queued;
        if (claim.State == ClaimOwnershipState.Unclaimed &&
            string.Equals(dispatchState, DispatchStates.RetryScheduled,
                StringComparison.OrdinalIgnoreCase))
            return RetryScheduled;
        if (claim.State == ClaimOwnershipState.Unclaimed &&
            string.Equals(dispatchState, DispatchStates.HandoffQueued,
                StringComparison.OrdinalIgnoreCase))
            return HandoffQueued;

        if (claim.State != ClaimOwnershipState.Unclaimed)
        {
            return ClaimantKinds.FromStorageValue(claim.ClaimantKind) switch
            {
                ClaimantKind.Agent => AgentActive,
                ClaimantKind.Human => HumanEditing,
                ClaimantKind.Automation => AutomationActive,
                _ => None
            };
        }

        if (session is { IsComplete: true } || HasCompleteAddress(claim))
            return IsCompletedRun(status, session, defaultFinishTo)
                ? Completed
                : PausedSession;
        if (automaticExecutionAllowed &&
            string.Equals(status, defaultPickFrom, StringComparison.OrdinalIgnoreCase))
            return Ready;
        return None;
    }

    // A retained session is "completed" (finished and landed) rather than "paused" (waiting to be
    // resumed) when the captured run outcome succeeded and the item reached the configured finish
    // status. Without the outcome (older records, or a finish status not supplied) it stays paused,
    // preserving the pre-plan-023 behavior. Resume durability is unchanged either way.
    private static bool IsCompletedRun(
        string? status,
        AgentSessionRecord? session,
        string? defaultFinishTo) =>
        session is { Outcome: RunOutcome.Succeeded } &&
        !string.IsNullOrWhiteSpace(defaultFinishTo) &&
        string.Equals(status, defaultFinishTo, StringComparison.OrdinalIgnoreCase);

    private static bool HasCompleteAddress(WorkItemClaimSummary claim) =>
        !string.IsNullOrWhiteSpace(claim.Agent) &&
        !string.IsNullOrWhiteSpace(claim.SessionId) &&
        !string.IsNullOrWhiteSpace(claim.WorkspacePath);
}

public sealed record WorkItemOperationalState(
    WorkItemDetail Item,
    WorkItemClaimSummary Claim,
    AgentSessionRecord? Session,
    string OperationalStatus);

/// <summary>
/// One consistent operational read of a work item: content, claim summary, and recorded agent
/// session, produced by the backend from a single snapshot rather than three separate reads.
/// </summary>
public sealed record WorkItemOperationalSnapshot(
    WorkItemDetail Item,
    WorkItemClaimSummary Claim,
    AgentSessionRecord? Session);
