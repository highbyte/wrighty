using Highbyte.Wrighty.ApprovedContext;
using Highbyte.Wrighty.Claims;
using Highbyte.Wrighty.Configuration;
using Highbyte.Wrighty.Models;
using Highbyte.Wrighty.Workers;
using Microsoft.AspNetCore.Html;

namespace Highbyte.Wrighty.Web;

public sealed record BoardPageModel(
    IReadOnlyList<string> Statuses,
    IReadOnlyList<string> Priorities,
    IReadOnlyList<BoardColumnModel> Columns,
    IReadOnlyList<BoardCardModel> Archived,
    string Scope,
    string Revision,
    string? ErrorCode = null,
    string? ErrorMessage = null,
    IReadOnlyList<ProviderCapacityView>? ProviderCapacity = null)
{
    public IReadOnlyList<ProviderCapacityView> EffectiveProviderCapacity =>
        ProviderCapacity ?? [];
}

public sealed record BoardColumnModel(string Name, IReadOnlyList<BoardCardModel> Cards);

public sealed record BoardCardModel(
    string Id,
    string DisplayId,
    string Title,
    string? Status,
    string? Priority,
    bool Archived,
    ClaimOwnershipState ClaimState,
    string ClaimLabel,
    string? ClaimantKindLabel,
    string? AgentLabel,
    bool AutomaticExecutionAllowed,
    string? AgentPolicy,
    string? DispatchState,
    string OperationalStatus,
    bool HasRecordedWorktree = false,
    ProviderCapacityView? ProviderBlock = null,
    bool CanQueueForAgent = false);

public sealed record ItemPageModel(
    string Id,
    string DisplayId,
    string Title,
    string Body,
    string? Status,
    string? Priority,
    bool Archived,
    string Revision,
    ClaimOwnershipState ClaimState,
    string ClaimLabel,
    string? ClaimantKindLabel,
    string? AgentLabel,
    bool WebMutationProtected,
    string? WebMutationProtectionMessage,
    bool TakeoverAvailable,
    string? ClaimantId,
    string? ClaimGeneration,
    bool HasResumeAddress,
    bool CanQueueForWorker,
    string? ResumeCommand,
    string? WorkerResumeCommand,
    string? ResumePrompt,
    string? ResumeAgentLabel,
    bool AutomaticExecutionAllowed,
    string? AgentPolicy,
    string? DispatchState,
    string OperationalStatus,
    IReadOnlyList<string> Statuses,
    IReadOnlyList<string> Priorities,
    IHtmlContent RenderedBody,
    string? Notice = null,
    string? ErrorCode = null,
    string? ErrorMessage = null,
    bool Editing = false,
    IReadOnlyDictionary<string, string>? Fields = null,
    string? RawFrontmatter = null,
    WorkspaceView? Workspace = null,
    LastRunView? LastRun = null,
    DispatchInfo? Dispatch = null,
    ProviderCapacityView? ProviderBlock = null,
    string? SessionAgentLabel = null,
    string? SessionId = null,
    SessionLaunchView? SessionLaunch = null)
{
    public IReadOnlyDictionary<string, string> EffectiveFields =>
        Fields ?? EmptyFields;

    private static readonly IReadOnlyDictionary<string, string> EmptyFields =
        new Dictionary<string, string>();
}

public sealed record SessionLaunchView(
    string Agent,
    string AgentLabel,
    string ExpectedSessionId,
    string ExpectedGeneration,
    bool CanOpenCli,
    string? CliUnavailableReason,
    bool CanOpenDesktop,
    DesktopSessionSupport DesktopSupport,
    string? DesktopUnavailableReason,
    bool DesktopIsHumanSupervised,
    string? DesktopPrerequisite,
    string? DesktopCompatibilityWarning);

/// <summary>
/// Sanitized installation-local provider capacity projected into the Local Markdown dashboard.
/// This deliberately contains neither raw provider responses nor account identity.
/// </summary>
public sealed record ProviderCapacityView(
    string Agent,
    string AgentLabel,
    ProviderCapacityState State,
    string? Reason,
    DateTimeOffset? Until,
    AgentFailureConfidence Confidence,
    int ConsecutiveFailures)
{
    public bool ProbeInProgress => State == ProviderCapacityState.ProbeInProgress;
    public bool HasCapacityFailure => ConsecutiveFailures > 0;

    public static ProviderCapacityView Available(string agentType) => new(
        agentType,
        Label(agentType),
        ProviderCapacityState.Available,
        null,
        null,
        AgentFailureConfidence.Authoritative,
        0);

    public static ProviderCapacityView From(ProviderCapacity availability) => new(
        availability.Agent,
        Label(availability.Agent),
        availability.State,
        availability.Reason,
        availability.UnavailableUntil,
        availability.Confidence,
        availability.ConsecutiveFailures);

    private static string Label(string agentType) =>
        string.IsNullOrWhiteSpace(agentType)
            ? "Provider"
            : char.ToUpperInvariant(agentType[0]) + agentType[1..];
}

public sealed record ProviderCapacityPageModel(
    IReadOnlyList<ProviderCapacityView> Providers,
    string Revision,
    string? Notice = null,
    string? ErrorCode = null,
    string? ErrorMessage = null);

/// <summary>
/// The captured outcome of the most recent agent run, surfaced in the item panel's "Last run"
/// block so an operator can read the block reason and clarify/requeue without opening the vendor
/// session first.
/// </summary>
public sealed record LastRunView(
    RunOutcome Outcome,
    string Label,
    DateTimeOffset? EndedAt,
    string? FinalMessage,
    AgentFailure? Failure,
    // The agent's own account of the run, when it produced one. Rendered separately and labelled:
    // the outcome beside it is Wrighty's observation, and nothing here can contradict it.
    ApprovedContext.AgentRunReport? AgentReport = null)
{
    /// <summary>
    /// What Wrighty observed the run achieve, preferred over the vendor's process result.
    ///
    /// A vendor exits successfully whenever it stops cleanly, including when it stopped to ask a
    /// question — so "Last run: succeeded" over a run that is actually waiting on a human reads as
    /// a verdict the vendor is in no position to give. The published report has always drawn this
    /// line; the panel draws it too.
    /// </summary>
    private static string LabelFor(AgentSessionRecord session, RunOutcome outcome) =>
        session.LastReport?.ObservedDisposition switch
        {
            ApprovedContext.RunReportDisposition.Finished => "finished",
            ApprovedContext.RunReportDisposition.NeedsAttention => "needs attention",
            ApprovedContext.RunReportDisposition.Failed => "failed",
            ApprovedContext.RunReportDisposition.Rejected => "rejected",
            _ => outcome switch
            {
                RunOutcome.Succeeded => "succeeded",
                RunOutcome.Failed => "failed",
                RunOutcome.Rejected => "rejected",
                _ => outcome.ToString().ToLowerInvariant()
            }
        };

    public static LastRunView? From(AgentSessionRecord? session)
    {
        if (session is not { Outcome: { } outcome }) return null;

        // Only when the agent actually said something. An observed-only report has nothing to
        // render, and passing it would put an empty block on the page.
        var reported = session.LastReport is { IsObservedOnly: false } report ? report : null;
        return new LastRunView(
            outcome,
            LabelFor(session, outcome),
            session.EndedAt,
            // Without the report block: it renders as fields below, and showing both puts the same
            // account on the page twice.
            ApprovedContext.AgentReportParser.WithoutReportBlock(session.FinalMessage),
            session.Failure,
            reported);
    }
}

/// <summary>
/// The durable worker worktree recorded for an item, with its git-calculated state when it could
/// be read on this host. <see cref="StatusAvailable"/> is false when the worktree is absent here
/// or git could not be read, in which case <see cref="Unavailable"/> carries a display message.
/// </summary>
public sealed record WorkspaceView(
    string Path,
    string? Branch,
    bool StatusAvailable,
    bool Dirty,
    bool Merged,
    string? Unavailable,
    bool Removed,
    IReadOnlyList<WorkerOperatorAction> CompletionActions);

public sealed record ConflictPageModel(
    ItemPageModel Current,
    string SubmittedTitle,
    string SubmittedBody,
    string SubmittedStatus,
    string? SubmittedPriority,
    bool SubmittedAutomaticExecutionAllowed,
    string? SubmittedAgentPolicy);

public sealed record WebErrorModel(string Code, string Message);

public sealed record CreateItemPageModel(
    string Title,
    string Body,
    string Status,
    string? Priority,
    bool AutomaticExecutionAllowed,
    string? AgentPolicy,
    string CreationAttemptId,
    IReadOnlyList<string> Statuses,
    IReadOnlyList<string> Priorities,
    string? ErrorCode = null,
    string? ErrorMessage = null);

public sealed record OperationsPageModel(
    WebSurfaceCapabilities Capabilities,
    string Backend,
    string? TargetUrl,
    string? TargetDescription,
    string? ActiveConfigurationRevision,
    RepositoryConfigurationSnapshot? Configuration,
    ConfigurationFormDraft? ConfigurationDraft,
    IReadOnlyList<WorkerInstanceStatus> Workers,
    IReadOnlyList<OperationsItemView> Items,
    string? Notice = null,
    string? ConfigurationErrorCode = null,
    string? ConfigurationErrorMessage = null,
    string? OperationsErrorCode = null,
    string? OperationsErrorMessage = null,
    string? TargetNotice = null,
    string? TargetErrorCode = null,
    string? TargetErrorMessage = null);

public sealed record ConfigurationFormDraft(
    string Operation,
    string? DefaultPickFrom,
    string? DefaultPickTo,
    string? DefaultFinishTo,
    string? DefaultAgent,
    string? WorkspaceMode,
    string? CompletionCommit,
    string? CompletionIntegration,
    string? ArchiveStatuses,
    bool ProtectNonHumanClaims,
    bool ApproveCanonicalization);

public sealed record OperationsItemView(
    string Id,
    string Title,
    string? Status,
    string? Priority,
    string? DispatchState,
    string OperationalStatus,
    string? Recovery,
    string? Url,
    bool? ContextApprovalFieldApproved = null)
{
    public string ContextApprovalLabel => ContextApprovalFieldApproved switch
    {
        true => "Approved (*)",
        false => "Needs review",
        _ => "Unknown"
    };

    public string ContextApprovalAppearance => ContextApprovalFieldApproved switch
    {
        true => "approved",
        false => "needs-review",
        _ => "unknown"
    };

    public string ContextApprovalTitle => ContextApprovalFieldApproved switch
    {
        true => "The Project field says Approved. Inspect to verify that the current issue content and discussion are still covered.",
        false => "The Project field says Needs review.",
        _ => "The Project approval field could not be resolved."
    };

    public string AutomationKey => string.Concat(
        Id.Select(character => char.IsLetterOrDigit(character) ? character : '-'));
}

/// <summary>Content-free diagnostics for one GitHub item's context approval.</summary>
public sealed record ContextApprovalView(
    string Id,
    string Title,
    string? Url,
    bool ProjectedApproved,
    bool Approved,
    string? Code,
    string? Message,
    string? ApprovalSource,
    DateTimeOffset? BaseApprovedAt,
    DateTimeOffset? BatchCommentCutoff,
    string? Revision,
    int? IncludedCount,
    int? ExcludedCount,
    int? PendingCount,
    IReadOnlyList<string> PendingUrls,
    string? Notice = null)
{
    public string ActionLabel => ProjectedApproved ? "Reapprove current context" : "Approve current context";

    public string AutomationKey => string.Concat(
        Id.Select(character => char.IsLetterOrDigit(character) ? character : '-'));

    public string InspectedLabel => Approved ? "Approved" : Code is
        ExecutionContextResult.Codes.ReadFailed or
        ExecutionContextResult.Codes.Unsupported
            ? "Unknown"
            : "Needs review";

    public string InspectedAppearance => Approved ? "approved" : Code is
        ExecutionContextResult.Codes.ReadFailed or
        ExecutionContextResult.Codes.Unsupported
            ? "unknown"
            : "needs-review";

    public string InspectedTitle => Approved
        ? "Inspect verified that the current issue content and discussion are approved."
        : Code is ExecutionContextResult.Codes.ReadFailed or ExecutionContextResult.Codes.Unsupported
            ? "Inspect could not verify the current issue content and discussion."
            : "Inspect found that the current issue content or discussion needs review.";

    public bool CanApprove => Approved || Code is
        ExecutionContextResult.Codes.ApprovalUnavailable or
        ExecutionContextResult.Codes.BaseNeedsReview or
        ExecutionContextResult.Codes.CommentPending;
}
