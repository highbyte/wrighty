using Highbyte.Wrighty.ApprovedContext;
using Highbyte.Wrighty.Workers;
using System.Text.Json.Serialization;

namespace Highbyte.Wrighty.Configuration;

public sealed record ArchiveConfig
{
    public IReadOnlyList<string> OnStatuses { get; init; } = [];
}

public sealed record WebConfig
{
    public bool ProtectNonHumanClaims { get; init; } = true;
}

public sealed record WorkerConfig
{
    public string? DefaultAgent { get; init; }

    public string? WorkspaceMode { get; init; }

    /// <summary>
    /// The profile names this repository recognizes. Shared policy vocabulary only — never model
    /// names, which are machine-local. An empty or absent list means the repository does not use
    /// execution profiles, and every launch keeps the vendor CLI's own defaults.
    /// </summary>
    public IReadOnlyList<string>? ExecutionProfiles { get; init; }

    /// <summary>
    /// Applied when neither the worker invocation nor the item names a profile. Must appear in
    /// <see cref="ExecutionProfiles"/>; a default outside the vocabulary is a configuration error
    /// rather than an implicit addition to it.
    /// </summary>
    public string? DefaultExecutionProfile { get; init; }

    [JsonIgnore]
    public IReadOnlyList<string> EffectiveExecutionProfiles => ExecutionProfiles ?? [];

    public WorkerCompletionConfig? Completion { get; init; }

    public WorkerUsageFailureConfig? UsageFailure { get; init; }

    [JsonIgnore]
    public WorkerUsageFailureConfig EffectiveUsageFailure => UsageFailure ?? new();

    /// <summary>
    /// Tuning for continuing a needs-attention session from a trusted author's comment. Absent
    /// means the defaults, not "disabled": there is deliberately no separate enable switch, because
    /// continuation already requires automatic execution, a resumable session, intact approval, and
    /// a non-empty trusted-author list — all deliberate acts. A further switch would only add a
    /// silent no-op that looks exactly like the defect this feature removes.
    /// </summary>
    public WorkerContinuationConfig? Continuation { get; init; }

    [JsonIgnore]
    public WorkerContinuationConfig EffectiveContinuation => Continuation ?? new();

    /// <summary>
    /// Settings for experimental Desktop session integrations. Supported integrations do not need
    /// configuration; absent means each experimental integration takes its own default.
    /// </summary>
    public WorkerDesktopSessionsConfig? DesktopSessions { get; init; }

    /// <summary>
    /// Claude's Desktop resume route is on by default and can be turned off per repository.
    ///
    /// It was an explicit opt-in while the route had passed qualification only once. Enabling it
    /// by default trades that caution for reach, so the honesty has to move somewhere else rather
    /// than disappear: the route still declares itself <see cref="DesktopSessionSupport"/>
    /// experimental, and its launch surfaces still say so where the operator chooses. Opting in is
    /// no longer the thing that tells them.
    /// </summary>
    public bool AllowsExperimentalDesktopSession(string agentType) =>
        string.Equals(agentType, "claude", StringComparison.OrdinalIgnoreCase) &&
        !string.Equals(DesktopSessions?.Claude, "off", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Legacy compatibility setting. Reports are now always stored locally and the current report
    /// appears in the rolling GitHub handover; accepted old values no longer change publication.
    /// </summary>
    public string? SessionReportMode { get; init; }

    // Kept as an ignored member so configurations written by older Wrighty versions can be read
    // and migrated even though this derived value no longer has runtime meaning.
    [JsonIgnore]
    public string? EffectiveSessionReportMode { get; init; }

    /// <summary>Bounds on the approved context a launch may assemble.</summary>
    public WorkerContextConfig? Context { get; init; }

    [JsonIgnore]
    public WorkerContextConfig EffectiveContext => Context ?? new();

    /// <summary>The permission profile the worker requests when it spawns a headless agent:
    /// "workspace" (default) or "full". "workspace" is the least privilege that still lets the
    /// agent do the tracked work, including its own network-dependent <c>wrighty</c> calls against
    /// the GitHub backend. "full" grants the vendor's unrestricted mode — command execution and
    /// file access across the whole machine — and should be an explicit, considered choice.</summary>
    public string? AgentPermissions { get; init; }

    /// <summary>Per-agent overrides keyed by vendor name (claude, codex, copilot).</summary>
    public IReadOnlyDictionary<string, WorkerAgentConfig>? Agents { get; init; }

    /// <summary>
    /// The profile requested for one agent: the per-agent override when present, otherwise the
    /// worker-wide default, otherwise "workspace". What the vendor actually enforces can be
    /// narrower or broader — ask the adapter through
    /// <see cref="IAgentAdapter.DescribePermissions"/> before telling an operator what a run is
    /// confined to.
    /// </summary>
    public AgentPermissionProfile RequestedAgentPermissions(string agentType)
    {
        var agentOverride = Agents is null
            ? null
            : Agents.FirstOrDefault(entry =>
                string.Equals(entry.Key, agentType, StringComparison.OrdinalIgnoreCase)).Value;
        return agentOverride?.Permissions is { } permissions
            ? AgentPermissionProfiles.Parse(
                permissions, $"worker.agents.{agentType.ToLowerInvariant()}.permissions")
            : AgentPermissionProfiles.Parse(AgentPermissions, "worker.agentPermissions");
    }

    /// <summary>Template for the directory that receives worker worktrees. Placeholders:
    /// {repo}, {repoParent}, {home}, {repoPathHash}. Default: {repoParent}/{repo}.worktrees.</summary>
    public string? WorktreeRoot { get; init; }

    /// <summary>Template for the worker branch name. Placeholders: {id}, {number}, {title},
    /// {unique}, {agent}, {date}. Default: wrighty-worker/{id}-{title}.</summary>
    public string? BranchFormat { get; init; }

    /// <summary>Template for the worktree directory name. Same placeholders as
    /// branchFormat. Default: {id}-{title}.</summary>
    public string? WorktreeNameFormat { get; init; }

    /// <summary>Controls the single overwrite-style handover comment the worker posts on a GitHub
    /// issue when a run ends in needs-attention or finishes with a retained worktree.
    /// "full" (default): includes the workspace path and host; "minimal": omits local machine
    /// details; "off": posts nothing. Ignored by the Local Markdown backend (the web dashboard is
    /// the equivalent surface there).</summary>
    public string? HandoverComment { get; init; }

    [JsonIgnore]
    public HandoverCommentMode EffectiveHandoverComment => HandoverComment?.ToLowerInvariant() switch
    {
        "off" => HandoverCommentMode.Off,
        "minimal" => HandoverCommentMode.Minimal,
        _ => HandoverCommentMode.Full
    };

    /// <summary>Whether absolute local workspace paths may be published to GitHub (the claim-marker
    /// JSON, the Project workspace-path field, and the handover comment). Default false so those
    /// paths — which embed the OS username — are never disclosed unless explicitly opted in; the path
    /// is still kept in the machine-local work-item runtime store, so resume on the recording host is
    /// unaffected, and the handover comment uses path-free <c>wrighty</c> commands. Set to true only
    /// when every collaborator with repository access is trusted to see local machine paths.</summary>
    public bool ShareLocalPaths { get; init; } = false;

    /// <summary>Whether the pick-from status is the worker queue: placing an item there is itself
    /// the automatic-execution authorization. When enabled (the default), an operator moving an
    /// item into <c>defaultPickFrom</c> through a Wrighty surface sets
    /// <c>AutomaticExecutionAllowed</c>, moving it out clears it, and the worker poll authorizes
    /// any item it finds in that status. Pairs with the default dedicated "Worker queue" pick-from
    /// status — pointing the pick-from at a general-purpose backlog column authorizes
    /// everything already sitting there. On GitHub, entering the queue also cycles context
    /// approval through Needs review to Approved. Set false to keep execution and approval as
    /// separate explicit edits.</summary>
    public bool UseWorkerQueue { get; init; } = true;
}

public sealed record WorkerDesktopSessionsConfig
{
    /// <summary>
    /// Claude's undocumented local resume URI. Omit or use "experimental" to allow it — the
    /// default — or set "off" to disable it for this repository.
    /// </summary>
    public string? Claude { get; init; }
}

/// <summary>Per-agent worker settings that override the worker-wide defaults.</summary>
public sealed record WorkerAgentConfig
{
    /// <summary>"workspace" or "full"; unset inherits <c>worker.agentPermissions</c>.</summary>
    public string? Permissions { get; init; }
}

/// <summary>
/// Bounds on the approved context a launch may assemble. Exceeding one refuses the launch; nothing
/// is ever truncated to fit, because dropping part of an approved task would change the
/// requirements while leaving the revision digest looking authoritative.
///
/// The defaults live on <see cref="ApprovedContext.ContextLimits"/>, which is what applies when no
/// configuration is present.
/// </summary>
public sealed record WorkerContextConfig
{
    /// <summary>Entries requiring a decision, whether or not they end up included.</summary>
    public int MaxDiscussionComments { get; init; } =
        ApprovedContext.ContextLimits.DefaultMaxDiscussionEntries;

    public int MaxEntryCharacters { get; init; } =
        ApprovedContext.ContextLimits.DefaultMaxEntryCharacters;

    /// <summary>Title, body, and every included entry together.</summary>
    public int MaxTotalCharacters { get; init; } =
        ApprovedContext.ContextLimits.DefaultMaxTotalCharacters;

    public ApprovedContext.ContextLimits ToLimits() =>
        new(MaxDiscussionComments, MaxEntryCharacters, MaxTotalCharacters);
}

/// <summary>
/// Tuning for trusted-comment continuation (plan 030 decision 19, comment half).
/// </summary>
public sealed record WorkerContinuationConfig
{
    /// <summary>
    /// <c>any-trusted-comment</c> (default) queues on any new comment from a trusted author while
    /// the item waits for input; <c>command-only</c> requires <see cref="Command"/> as the exact
    /// normalized first line, which suits a team where conversational replies should not spend an
    /// agent turn.
    /// </summary>
    public string Trigger { get; init; } = TriggerModes.AnyTrustedComment;

    /// <summary>
    /// The exact control command for <c>command-only</c>. Matched as a whole normalized first line
    /// against a fixed form — never by interpreting natural language, so ordinary prose that
    /// happens to discuss continuing cannot start a run. Any remaining body is still task context.
    /// </summary>
    public string Command { get; init; } = "/wrighty continue";

    /// <summary>
    /// GitHub reaction names used as explicit controls on the latest unresolved Wrighty status
    /// comment.
    /// These are semantic API names, not emoji glyphs. They remain inert unless a trusted author
    /// reacts to a strict current comment while its same-installation session needs attention.
    /// </summary>
    public string ResumeReaction { get; init; } = ReactionKinds.Rocket;

    public string CompletionReaction { get; init; } = ReactionKinds.Hooray;

    public int MaxAutomaticContinuations { get; init; } =
        TrustedContinuationBudget.DefaultMaxAutomaticContinuations;

    public double CooldownSeconds { get; init; } = 30;

    public double DebounceSeconds { get; init; } = 10;

    [JsonIgnore]
    public bool RequiresCommand =>
        string.Equals(Trigger, TriggerModes.CommandOnly, StringComparison.OrdinalIgnoreCase);

    [JsonIgnore]
    public TimeSpan Cooldown => TimeSpan.FromSeconds(Math.Max(0, CooldownSeconds));

    [JsonIgnore]
    public TimeSpan Debounce => TimeSpan.FromSeconds(Math.Max(0, DebounceSeconds));

    public static class TriggerModes
    {
        public const string AnyTrustedComment = "any-trusted-comment";
        public const string CommandOnly = "command-only";
    }
}

public sealed record WorkerUsageFailureConfig
{
    /// <summary>"retry" (default), "handoff", or "needs-attention". "handoff" hands the work to
    /// the first available configured fallback agent instead of retrying the same one; with
    /// "retry", handoff additionally engages after retries are exhausted when
    /// <see cref="AllowCrossAgentHandoff"/> opts in.</summary>
    public string Action { get; init; } = "retry";

    public double InitialRetryMinutes { get; init; } = 30;

    public double BackoffMultiplier { get; init; } = 2;

    public double MaxRetryHours { get; init; } = 6;

    public int MaxAttempts { get; init; } = 5;

    public double ResetGraceMinutes { get; init; } = 2;

    public bool AllowCrossAgentHandoff { get; init; }

    public IReadOnlyDictionary<string, IReadOnlyList<string>> Fallbacks { get; init; } =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["claude"] = ["codex", "copilot"],
            ["codex"] = ["claude", "copilot"],
            ["copilot"] = ["codex", "claude"]
        };
}

public enum HandoverCommentMode
{
    Full,
    Minimal,
    Off
}

/// <summary>
/// Operator policy for what happens when a worker finishes an item. Wrighty never executes
/// merge, push, or PR creation; <see cref="Integration"/> only selects which guidance the
/// finished output and the agent skill render.
/// </summary>
public sealed record WorkerCompletionConfig
{
    /// <summary>"inspect" (default): the agent must leave changes uncommitted for operator
    /// review. "agent": the agent commits its work before finishing.</summary>
    public string? Commit { get; init; }

    /// <summary>"none" (default), "merge-local", or "push-pr".</summary>
    public string? Integration { get; init; }

    /// <summary>
    /// Who decides an item is done. <c>agent</c> (default) keeps the existing behaviour: the agent
    /// calls finish when it judges the approved task satisfied. <c>user-confirmed</c> holds that
    /// decision for a person — the agent reports the work it believes complete and stops, and the
    /// item waits until someone accepts it in the discussion.
    /// </summary>
    public string? Policy { get; init; }

    [JsonIgnore]
    public bool RequiresUserConfirmation =>
        string.Equals(Policy, CompletionPolicies.UserConfirmed, StringComparison.OrdinalIgnoreCase);

    public static class CompletionPolicies
    {
        public const string Agent = "agent";
        public const string UserConfirmed = "user-confirmed";
    }
}

public sealed record GitHubBackendConfig
{
    public required string Repository { get; init; }

    public string? ProjectOwner { get; init; }

    public required int ProjectNumber { get; init; }

    public bool LinkRepository { get; init; } = true;

    public string StatusField { get; init; } = "Status";

    public string PriorityField { get; init; } = "Priority";

    public string ExecutionPolicyField { get; init; } = "Wrighty policy - execution";

    public string AgentPolicyField { get; init; } = "Wrighty policy - agent";

    /// <summary>
    /// The single-select carrying an item's execution profile. Unlike every other Wrighty Project
    /// field, its options are not a fixed vocabulary: they come from
    /// <c>worker.executionProfiles</c>, so provisioning and drift checks are computed per
    /// repository rather than from a constant.
    /// </summary>
    public string WorkerProfileField { get; init; } = "Wrighty policy - profile";

    public string ContextApprovalField { get; init; } = "Wrighty policy - context approval";

    /// <summary>
    /// GitHub logins whose comments count as approved without a separate approval step.
    ///
    /// Empty by default: no author is trusted unless the repository names one. Commit this file if
    /// you set it — the approved-context digest is reproducible across machines only while they
    /// agree on the trusted set.
    ///
    /// Anyone with write access to the repository can edit another user's comment without changing
    /// its author, so naming an author here also trusts every edit those collaborators make to that
    /// author's comments.
    /// </summary>
    public IReadOnlyList<string>? TrustedCommentAuthors { get; init; }

    /// <summary>
    /// GitHub logins whose decision reactions count: a <c>+1</c> from a listed login includes a
    /// pending comment, a <c>-1</c> excludes it. Matched case-insensitively, like
    /// <see cref="TrustedCommentAuthors"/> — and deliberately a separate list: a trusted author's
    /// own comments are auto-included, while an approver decides <em>other people's</em> comments
    /// and can exclude. A login may reasonably be on one list and not the other.
    ///
    /// Unlike an editable comment, a reaction's actor and kind cannot be altered by any third
    /// party, so this authority is more attributable than author-based comment trust. A reaction
    /// covers only the comment revision it postdates: editing the comment afterwards invalidates
    /// the decision.
    /// </summary>
    public IReadOnlyList<string>? ContextApprovers { get; init; }

    public string DispatchStateField { get; init; } = "Wrighty dispatch - state";

    public string DispatchNotBeforeField { get; init; } = "Wrighty dispatch - not before";

    public string DispatchAgentField { get; init; } = "Wrighty dispatch - agent";

    public string DispatchDetailField { get; init; } = "Wrighty dispatch - detail";

    public string ClaimAgentField { get; init; } = "Wrighty claim - agent";

    public string ClaimantTypeField { get; init; } = "Wrighty claim - claimant type";

    public string ClaimantField { get; init; } = "Wrighty claim - claimant";

    public string ClaimSessionIdField { get; init; } = "Wrighty claim - session ID";

    public string ClaimWorkspacePathField { get; init; } = "Wrighty claim - workspace path";

    public string CreationAttemptIdField { get; init; } = "Wrighty creation - attempt ID";

    public int ClaimHistoryLimit { get; init; } = 10;

    public string GitHubHost { get; init; } = "github.com";
}

public sealed record LocalMarkdownBackendConfig
{
    public string Path { get; init; } = ".wrighty";

    public IReadOnlyList<string> Statuses { get; init; } =
        ["Todo", "Worker queue", "In Progress", "Done"];

    public IReadOnlyList<string> Priorities { get; init; } = ["P0", "P1", "P2", "P3"];
}
