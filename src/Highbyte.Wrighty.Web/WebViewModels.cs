using System.Globalization;
using Highbyte.Wrighty.ApprovedContext;
using Highbyte.Wrighty.Claims;
using Highbyte.Wrighty.Configuration;
using Highbyte.Wrighty.Models;
using Highbyte.Wrighty.Storage;
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
    IReadOnlyList<ProviderCapacityView>? ProviderCapacity = null,
    BoardListQuery? Query = null,
    BoardBatchResult? BatchResult = null)
{
    public IReadOnlyList<ProviderCapacityView> EffectiveProviderCapacity =>
        ProviderCapacity ?? [];

    public BoardListQuery EffectiveQuery => Query ?? BoardListQuery.Parse(new BoardListInput());
}

public sealed record BoardColumnModel(
    string Name,
    IReadOnlyList<BoardCardModel> Cards,
    int Index = 0,
    ItemSort? Sort = null,
    BoardBulkActionView? BulkAction = null)
{
    public ItemSort EffectiveSort => Sort ?? ItemSort.Default;
}

public sealed record BoardBulkActionView(
    string Id,
    string IntentId,
    string Label,
    string Description,
    string ConfirmTitle,
    string ConfirmMessage,
    string ConfirmAction,
    int EligibleCount,
    int ShownCount);

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
    IReadOnlyList<CardActionView>? Actions = null,
    // The statuses this card may be dragged to, resolved from the item's own state. Empty means
    // the card is not draggable at all, which is the honest answer for an item that belongs to a
    // claimant or has a decision pending.
    IReadOnlyList<string>? DropTargets = null,
    DateTimeOffset? CreatedAt = null,
    DateTimeOffset? UpdatedAt = null,
    string? AgentKey = null)
{
    public ItemSortField DisplayTimestampField { get; init; } = ItemSortField.Updated;

    /// <summary>The card's actions in offer order; the first is its primary affordance.</summary>
    public IReadOnlyList<CardActionView> EffectiveActions => Actions ?? [];

    public IReadOnlyList<string> EffectiveDropTargets => DropTargets ?? [];

    public bool IsDraggable => EffectiveDropTargets.Count > 0;
}

/// <summary>
/// One operation a card offers, resolved on the server from the same eligibility the item panel
/// uses. The view renders what this says and decides nothing: it carries the handler to post to,
/// the words to show, and — when an action is offered but not currently possible — the reason,
/// so no policy leaks into the board template.
///
/// Deliberately shaped like a miniature of plan 036's action catalogue: when that lands, the
/// resolver behind this record is replaced by catalogue availability and <see cref="Handler"/>
/// becomes the stable action name, without the board changing.
/// </summary>
public sealed record CardActionView(
    string Id,
    string Handler,
    string Label,
    string Title,
    string ScreenReaderSuffix,
    bool IsPrimary = true,
    string? UnavailableReason = null,
    // Whether this action's answer belongs in the item panel. False for the fire-and-forget
    // moves, whose feedback is the card moving; true for the ones that open something — the edit
    // form, a terminal, a Desktop app — where there is a result to read. Stated per action
    // because a card action that silently opened the panel is exactly the inconsistency this
    // model exists to prevent.
    bool OpensPanel = false,
    // Whether the panel this action opens should behave as the tail of a card gesture: it ends by
    // releasing and returning to the board, rather than leaving the operator inside the item.
    bool BoundedGesture = false,
    // Confirmation carried onto the card, so an action whose warning lived in the item panel
    // does not lose it on the way out.
    string? ConfirmTitle = null,
    string? ConfirmMessage = null,
    string? ConfirmAction = null,
    // Fencing inputs a session launch must post with the request.
    string? ExpectedSessionId = null,
    string? ExpectedSessionGeneration = null,
    // Modes this action offers instead of acting directly. Present only when a card would
    // otherwise carry several buttons for one intent — opening a session, which may go to a
    // terminal or to the vendor's Desktop app. One button, then a choice.
    IReadOnlyList<CardActionOption>? Options = null)
{
    public bool IsAvailable => UnavailableReason is null;

    public bool NeedsConfirmation => ConfirmMessage is not null;

    public IReadOnlyList<CardActionOption> EffectiveOptions => Options ?? [];

    public bool OffersChoice => EffectiveOptions.Count > 0;
}

/// <summary>
/// One mode of a <see cref="CardActionView"/> that offers a choice.
///
/// <paramref name="Consequence"/> is not decoration. The modes differ in who ends up holding the
/// item — a terminal continues the session as the agent, Desktop is supervised by the operator and
/// must be handed back — and that difference is invisible from the labels alone. Stating it beside
/// each option puts it in front of the operator while they are choosing, which is also where a
/// vendor prerequisite or experimental-integration warning belongs.
/// </summary>
public sealed record CardActionOption(
    string Id,
    string Handler,
    string Label,
    string Consequence,
    string ScreenReaderSuffix);

/// <summary>
/// The item identity and action list consumed by the shared action renderer. Board cards and
/// Operations rows differ in everything around the actions, but opening a retained session must
/// keep one chooser, one set of fencing fields, and one accessibility contract on both surfaces.
/// </summary>
public sealed record ActionListView(
    string Id,
    string DisplayId,
    IReadOnlyList<CardActionView> Actions);

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
    // Whether this edit was opened as a bounded gesture from a board card rather than from inside
    // the item panel. A card action claims, acts and releases; an edit that kept its claim would
    // be the one card gesture that leaves the item held — and the card would then stop offering
    // the very action that got the operator here.
    bool CardEntry = false,
    IReadOnlyDictionary<string, string>? Fields = null,
    string? RawFrontmatter = null,
    WorkspaceView? Workspace = null,
    LastRunView? LastRun = null,
    DispatchInfo? Dispatch = null,
    ProviderCapacityView? ProviderBlock = null,
    string? SessionAgentLabel = null,
    string? SessionId = null,
    SessionLaunchView? SessionLaunch = null,
    /// <summary>True only for an unclaimed Local Markdown item with no processing history.</summary>
    bool CanDelete = false,
    /// <summary>This item's execution profile, or null for the repository default.</summary>
    string? ExecutionProfile = null,
    /// <summary>
    /// The profile names this editor may offer. Empty hides the control entirely, so a repository
    /// that does not use profiles sees no new field.
    /// </summary>
    IReadOnlyList<string>? ExecutionProfiles = null,
    DateTimeOffset? CreatedAt = null,
    DateTimeOffset? UpdatedAt = null,
    bool QueueAuthorizesExecution = false,
    string WorkerQueueStatus = "Worker queue",
    IReadOnlyList<AgentOptionView>? AvailableAgents = null)
{
    public IReadOnlyList<string> EffectiveExecutionProfiles => ExecutionProfiles ?? [];

    public IReadOnlyDictionary<string, string> EffectiveFields =>
        Fields ?? EmptyFields;

    public IReadOnlyList<AgentOptionView> AgentOptions => AvailableAgents ?? [];

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
    bool UnmanagedTerminal,
    string? DesktopPrerequisite,
    string? DesktopCompatibilityWarning);

/// <summary>
/// Sanitized installation-local provider capacity projected into the web console.
/// This deliberately contains neither raw provider responses nor account identity.
/// </summary>
public sealed record ProviderCapacityView(
    string Agent,
    string AgentLabel,
    ProviderCapacityState State,
    string? Reason,
    DateTimeOffset? Until,
    AgentFailureConfidence Confidence,
    int ConsecutiveFailures,
    bool Simulated)
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
        0,
        false);

    public static ProviderCapacityView From(ProviderCapacity availability) => new(
        availability.Agent,
        Label(availability.Agent),
        availability.State,
        availability.Reason,
        availability.UnavailableUntil,
        availability.Confidence,
        availability.ConsecutiveFailures,
        availability.Simulated);

    private static string Label(string agentType) =>
        string.IsNullOrWhiteSpace(agentType)
            ? "Provider"
            : char.ToUpperInvariant(agentType[0]) + agentType[1..];
}

/// <summary>One compact row in the machine-local agent inventory.</summary>
public sealed record AgentInventoryRow(
    string Agent,
    string AgentLabel,
    bool Detected,
    bool Selected,
    bool Enabled,
    string? ExecutablePath,
    ProviderCapacityView? Capacity,
    SkillTargetStatus? Skill)
{
    public bool ProbeInProgress => Capacity?.ProbeInProgress == true;

    public bool CapacityUnavailable =>
        Capacity?.State == ProviderCapacityState.UnavailableUntil;

    public bool NeedsAttention => (Selected && !Detected) || Enabled &&
        (CapacityUnavailable || Capacity?.Simulated == true ||
         Skill?.NeedsAttention == true);
}

public sealed record AgentInventoryPageModel(
    IReadOnlyList<AgentInventoryRow> Agents,
    string Revision,
    string UserConfigurationRevision,
    string? Notice = null,
    string? ErrorCode = null,
    string? ErrorMessage = null,
    bool MenuOpen = false)
{
    public int EnabledCount => Agents.Count(agent => agent.Enabled);

    public int AttentionCount => Agents.Count(agent => agent.NeedsAttention);
}

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
    bool QueueAuthorizesExecution,
    string WorkerQueueStatus,
    string? ErrorCode = null,
    string? ErrorMessage = null,
    IReadOnlyList<AgentOptionView>? AvailableAgents = null)
{
    public IReadOnlyList<AgentOptionView> AgentOptions => AvailableAgents ?? [];
}

public sealed record AgentOptionView(string Id, string DisplayName);

public sealed record GitHubTargetView(
    string Host,
    string Repository,
    string RepositoryUrl,
    string ProjectLabel,
    string? ProjectUrl);

public sealed record WorkerSummaryPageModel(
    int RunningCount,
    int ProcessingCount,
    int AttentionCount)
{
    public string Revision => $"{RunningCount}-{ProcessingCount}-{AttentionCount}";

    public static WorkerSummaryPageModel From(IReadOnlyList<WorkerInstanceStatus> workers) => new(
        workers.Count(worker => worker.Liveness == WorkerInstanceLiveness.Running),
        workers.Count(worker =>
            worker.Liveness == WorkerInstanceLiveness.Running &&
            worker.Instance.CurrentItemId is not null),
        workers.Count(worker => worker.Liveness != WorkerInstanceLiveness.Running));
}

public sealed record OperationsPageModel(
    WebSurfaceCapabilities Capabilities,
    string Backend,
    GitHubTargetView? Target,
    IReadOnlyList<WorkerInstanceStatus> Workers,
    IReadOnlyList<HostedWorkerSnapshot> HostedWorkers,
    bool HostedWorkerAvailable,
    IReadOnlyList<OperationsItemView> Items,
    string? OperationsErrorCode = null,
    string? OperationsErrorMessage = null,
    string? TargetNotice = null,
    string? TargetErrorCode = null,
    string? TargetErrorMessage = null,
    OperationsListQuery? Query = null,
    bool IsTruncated = false,
    IReadOnlyList<AgentOptionView>? AvailableAgents = null,
    IReadOnlyList<string>? AvailablePriorities = null,
    IReadOnlyList<string>? AvailableWorkflowStatuses = null,
    string? WorkerNotice = null,
    string? WorkerErrorCode = null,
    string? WorkerErrorMessage = null)
{
    public OperationsListQuery EffectiveQuery => Query ?? OperationsListQuery.Parse(
        new OperationsListInput());

    public bool LocalClaimFiltersAvailable => Capabilities.LocalBoard;

    public IReadOnlyList<AgentOptionView> AgentOptions => AvailableAgents ?? [];

    public IReadOnlyList<string> PriorityOptions => AvailablePriorities ?? [];

    public IReadOnlyList<string> WorkflowStatusOptions => AvailableWorkflowStatuses ?? [];
}

public sealed record SettingsPageModel(
    WebSurfaceCapabilities Capabilities,
    string Backend,
    string? ActiveConfigurationRevision,
    IReadOnlySet<string> WorkerCompatibleConfigurationRevisions,
    RepositoryConfigurationSnapshot? Configuration,
    ConfigurationFormDraft? ConfigurationDraft,
    // This machine's own settings, which the console could not previously show at all. Nullable
    // for the same reason the repository snapshot is: a build without the service still renders.
    Highbyte.Wrighty.Settings.UserConfigurationSnapshot? UserConfiguration,
    // Registered adapters with this host's effective installation state. Testing overrides may
    // deliberately turn an installed runtime into a simulated missing one.
    IReadOnlyList<Highbyte.Wrighty.Workers.AgentRuntime> AgentRuntimes,
    // Immutable built-in identity and display metadata; unlike runtimes this is not affected by
    // installation or testing overlays.
    IReadOnlyList<Highbyte.Wrighty.Workers.AgentDescriptor> AgentDescriptors,
    // What each installed agent reports it can run. Empty when discovery is unavailable, which the
    // form renders as a free-text field rather than an empty picker.
    IReadOnlyList<Highbyte.Wrighty.Workers.AgentModelCatalog> AgentModels,
    // Only for the restart warning's drift count; the worker list itself lives on Operations.
    IReadOnlyList<WorkerInstanceStatus> Workers,
    // The read-only filesystem footprint. Paths are local inspection data; credential values are
    // never included.
    IReadOnlyList<StorageLocationDescriptor> StorageLocations,
    string? Notice = null,
    string? ConfigurationErrorCode = null,
    string? ConfigurationErrorMessage = null,
    string ActiveSection = "repository");

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
    bool ApproveCanonicalization,
    string? ExecutionProfiles = null,
    string? DefaultExecutionProfile = null,
    string? Agent = null,
    bool PretendNotInstalled = false,
    string? FailureKind = null,
    double? RetryAfterSeconds = null,
    string? UsageFailureAction = null,
    string? UsageFailureInitialRetryMinutes = null,
    string? UsageFailureBackoffMultiplier = null,
    string? UsageFailureMaxRetryHours = null,
    string? UsageFailureMaxAttempts = null,
    string? UsageFailureResetGraceMinutes = null,
    bool UsageFailureAllowCrossAgentHandoff = false,
    IReadOnlyDictionary<string, string?>? UsageFailureFallbacks = null,
    string? LeaseMinutes = null,
    bool UseWorkerQueue = true,
    string? RequirementsAssessmentMode = null,
    string? AgentPermissions = null,
    IReadOnlyDictionary<string, string?>? AgentPermissionOverrides = null,
    string? WorktreeRoot = null,
    string? BranchFormat = null,
    string? WorktreeNameFormat = null,
    string? CompletionPolicy = null,
    string? HandoverComment = null,
    bool ShareLocalPaths = false,
    string? TrustedCommentAuthors = null,
    string? ContextApprovers = null,
    string? ClaimHistoryLimit = null,
    string? MaxDiscussionComments = null,
    string? MaxEntryCharacters = null,
    string? MaxTotalCharacters = null,
    string? ContinuationTrigger = null,
    string? ContinuationCommand = null,
    string? ResumeReaction = null,
    string? CompletionReaction = null,
    string? MaxAutomaticContinuations = null,
    string? CooldownSeconds = null,
    string? DebounceSeconds = null,
    string? LocalMarkdownStatuses = null,
    string? LocalMarkdownPriorities = null,
    string? DefaultCreateStatus = null,
    string? CapacityProbeResult = null,
    double? CapacityProbeRetryAfterSeconds = null);

/// <summary>
/// The one place a machine operational status becomes a human label, shared by the board cards
/// and the Operations table so every surface shows the same vocabulary.
/// </summary>
public static class OperationalStatusDisplay
{
    public static string? Label(string operationalStatus, string? agentLabel = null) =>
        operationalStatus switch
        {
            OperationalStatuses.NeedsAttention => "Needs attention",
            OperationalStatuses.AgentActive => $"{agentLabel ?? "Agent"} active",
            OperationalStatuses.Queued => "Resume queued",
            OperationalStatuses.RetryScheduled => "Retry scheduled",
            OperationalStatuses.HandoffQueued => "Handoff queued",
            OperationalStatuses.PausedSession => "Session retained",
            OperationalStatuses.Completed => "Completed",
            OperationalStatuses.HumanEditing => "Human editing",
            OperationalStatuses.AutomationActive => "Automation active",
            OperationalStatuses.Ready => "Ready for worker",
            _ => null
        };
}

/// <summary>
/// Matches the browser's relative-time labels so fragment swaps do not briefly expose the
/// absolute fallback timestamp before JavaScript processes the new elements.
/// </summary>
public static class RelativeTimeDisplay
{
    public static string Label(DateTimeOffset value, DateTimeOffset? now = null)
    {
        var seconds = JavaScriptRound((value - (now ?? DateTimeOffset.UtcNow)).TotalSeconds);
        var future = seconds > 0;
        var absolute = Math.Abs(seconds);
        if (absolute < 45) return "just now";

        var (amount, unit) = absolute switch
        {
            < 3_600 => (JavaScriptRound(absolute / 60d), "m"),
            < 86_400 => (JavaScriptRound(absolute / 3_600d), "h"),
            < 2_592_000 => (JavaScriptRound(absolute / 86_400d), "d"),
            < 31_536_000 => (JavaScriptRound(absolute / 2_592_000d), "mo"),
            _ => (JavaScriptRound(absolute / 31_536_000d), "y")
        };
        var relative = $"{amount.ToString(CultureInfo.InvariantCulture)}{unit}";
        return future ? $"in {relative}" : $"{relative} ago";
    }

    private static long JavaScriptRound(double value) => (long)Math.Floor(value + .5d);
}

public sealed record OperationsItemView(
    string Id,
    string Title,
    string? Status,
    string? Priority,
    string? DispatchState,
    string OperationalStatus,
    string? Recovery,
    string? Url,
    bool? ContextApprovalFieldApproved = null,
    IReadOnlyList<CardActionView>? SessionActions = null,
    string? RequestedAgent = null,
    DateTimeOffset? CreatedAt = null,
    DateTimeOffset? UpdatedAt = null,
    string? ClaimantKind = null,
    ClaimOwnershipState? ClaimState = null)
{
    public IReadOnlyList<CardActionView> EffectiveSessionActions => SessionActions ?? [];

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

    private (string Label, string Appearance, string Title) InspectedState =>
        (Approved, Code) switch
        {
            (true, _) => (
                "Approved",
                "approved",
                "Inspect verified that the current issue content and discussion are approved."),
            (false, ExecutionContextResult.Codes.ReadFailed or
                ExecutionContextResult.Codes.Unsupported) => (
                    "Unknown",
                    "unknown",
                    "Inspect could not verify the current issue content and discussion."),
            _ => (
                "Needs review",
                "needs-review",
                "Inspect found that the current issue content or discussion needs review.")
        };

    public string InspectedLabel => InspectedState.Label;

    public string InspectedAppearance => InspectedState.Appearance;

    public string InspectedTitle => InspectedState.Title;

    public bool CanApprove => Approved || Code is
        ExecutionContextResult.Codes.ApprovalUnavailable or
        ExecutionContextResult.Codes.BaseNeedsReview or
        ExecutionContextResult.Codes.CommentPending;
}

/// <summary>
/// How a discovered model reads in a picker: what it resolves to, what it costs relative to its
/// siblings where the vendor says, and whether its effort support is known.
///
/// Unknown is shown rather than omitted. An operator choosing a model deserves to know that the
/// effort they pair with it may go unchecked, which is exactly the case a blank would hide.
/// </summary>
public static class AgentModelChoice
{
    public static string Describe(Highbyte.Wrighty.Workers.AgentModel model)
    {
        var notes = new List<string>();
        if (model.ResolvedId is { } resolved &&
            !string.Equals(resolved, model.Id, StringComparison.OrdinalIgnoreCase))
        {
            notes.Add(resolved);
        }

        if (model.RelativeCost is { } cost)
        {
            notes.Add(cost);
        }

        notes.Add(model.Effort switch
        {
            Highbyte.Wrighty.Workers.EffortSupport.Yes when model.Efforts.Count > 0 =>
                string.Join("/", model.Efforts),
            Highbyte.Wrighty.Workers.EffortSupport.No => "no effort",
            Highbyte.Wrighty.Workers.EffortSupport.Yes => "effort accepted",
            _ => "effort unknown"
        });

        return $"{model.Id} — {string.Join(", ", notes)}";
    }
}

/// <summary>What the shared model control renders: the agent's catalog and the stored value.</summary>
public sealed record MappingModelControl(
    Highbyte.Wrighty.Workers.AgentModelCatalog? Catalog,
    string? Selected);
