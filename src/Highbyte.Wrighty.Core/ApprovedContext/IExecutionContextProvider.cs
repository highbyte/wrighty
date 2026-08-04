using System.Text.Json.Serialization;
using Highbyte.Wrighty.Configuration;
using Highbyte.Wrighty.Models;

namespace Highbyte.Wrighty.ApprovedContext;

/// <summary>
/// Why an approved context is being read. The purpose does not change what a snapshot contains —
/// two reads of unchanged content produce the same revision regardless — but it does change how
/// much a backend may lean on caching, and it is carried into diagnostics so an operator can tell
/// which read refused a launch.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<ContextReadPurpose>))]
public enum ContextReadPurpose
{
    /// <summary>Before claiming, to avoid claiming an item already known to need review.</summary>
    [JsonStringEnumMemberName("pre-claim")]
    PreClaim,

    /// <summary>After claiming and immediately before spawn. Must not reuse a pre-claim read.</summary>
    [JsonStringEnumMemberName("pre-launch")]
    PreLaunch,

    /// <summary>Comparing a resumable session's recorded manifest with the current context.</summary>
    [JsonStringEnumMemberName("resume-comparison")]
    ResumeComparison,

    /// <summary>An explicit operator-facing read. Never claims, launches, or mutates anything.</summary>
    [JsonStringEnumMemberName("diagnostics")]
    Diagnostics
}

/// <summary>
/// One backend's ability to assemble an approved execution context.
///
/// Kept off <see cref="Backends.ITrackerBackend"/> deliberately. An ordinary item read is used by
/// list and dashboard polling and must stay cheap, whereas assembling a context can page an entire
/// conversation and resolve authorization. Backends that cannot do that simply do not implement
/// this, and the worker reports an unsupported capability rather than silently launching with a
/// context nobody approved.
/// </summary>
public interface IExecutionContextProvider
{
    /// <summary>
    /// Assembles the approved context for one item, or explains why it cannot be approved.
    ///
    /// Implementations must fail closed. An unreadable approval field, an unresolvable actor, an
    /// incomplete page, or a timestamp that cannot be ordered safely all produce a refusal, never a
    /// snapshot with the doubtful part quietly omitted.
    /// </summary>
    Task<ExecutionContextResult> GetAsync(
        TrackerConfig config,
        WorkItemId id,
        ContextReadPurpose purpose,
        ContextLimits limits,
        CancellationToken cancellationToken);
}

/// <summary>
/// Either an approved snapshot or the reason there is not one. A refusal carries a stable code and
/// an operator-facing message, and — like every other diagnostic surface in this design — never
/// carries item content: these flow into worker events and logs.
/// </summary>
public sealed record ExecutionContextResult(
    ExecutionContextSnapshot? Snapshot,
    string? Code = null,
    string? Message = null,
    IReadOnlyList<string>? PendingUrls = null,
    ExecutionContextDiagnostics? Diagnostics = null)
{
    public bool IsApproved => Snapshot is not null;

    public ExecutionContextDiagnostics? EffectiveDiagnostics =>
        Snapshot is { } snapshot
            ? ExecutionContextDiagnostics.From(snapshot)
            : Diagnostics;

    public static ExecutionContextResult Approved(ExecutionContextSnapshot snapshot) =>
        new(snapshot, Diagnostics: ExecutionContextDiagnostics.From(snapshot));

    public static ExecutionContextResult Refused(
        string code,
        string message,
        IReadOnlyList<string>? pendingUrls = null,
        ExecutionContextDiagnostics? diagnostics = null) =>
        new(null, code, message, pendingUrls, diagnostics);

    /// <summary>
    /// The refusal codes this design defines. They are stable identifiers an operator can look up,
    /// so they are named here rather than spelled out at each call site.
    /// </summary>
    public static class Codes
    {
        /// <summary>The approval field or its cutoff could not be resolved at all.</summary>
        public const string ApprovalUnavailable = "CONTEXT_APPROVAL_UNAVAILABLE";

        /// <summary>The current title or body is not covered by the recorded approval.</summary>
        public const string BaseNeedsReview = "CONTEXT_BASE_NEEDS_REVIEW";

        /// <summary>At least one relevant entry has no decision covering its current revision.</summary>
        public const string CommentPending = "CONTEXT_COMMENT_PENDING";

        /// <summary>
        /// A hidden comment carries no decision of its own, and hiding is not something Wrighty can
        /// place in time: GitHub advances no timestamp and emits no timeline event when a comment is
        /// minimized. So a hide cannot be read as "exclude this" without also letting a maintainer
        /// silently drop approved content, and it cannot be ignored without shipping the very
        /// comment somebody hid. The operator resolves it instead.
        /// </summary>
        public const string CommentHidden = "CONTEXT_COMMENT_HIDDEN";

        /// <summary>Authorization lookup was incomplete, so no decision can be trusted.</summary>
        public const string AuthorizationUnavailable = "CONTEXT_AUTHORIZATION_UNAVAILABLE";

        /// <summary>Conflicting decisions could not be ordered.</summary>
        public const string DecisionAmbiguous = "CONTEXT_DECISION_AMBIGUOUS";

        /// <summary>Timestamps could not establish a safe ordering.</summary>
        public const string RevisionAmbiguous = "CONTEXT_REVISION_AMBIGUOUS";

        /// <summary>
        /// The context resolved before the spawn differs from the one resolved after the claim, so
        /// the content about to reach the agent is not the content this launch validated.
        /// </summary>
        public const string RevisionChanged = "CONTEXT_REVISION_CHANGED";

        /// <summary>
        /// The approved context changed since the session being resumed was given it, in a way that
        /// cannot be delivered to a session already in progress — an edit, a deletion, a visibility
        /// change, or changed approval evidence. Distinct from
        /// <see cref="RevisionChanged"/>, which is a change within a single launch.
        /// </summary>
        public const string ResumeBlocked = "CONTEXT_RESUME_BLOCKED";

        /// <summary>
        /// A resume proceeded across a change an unattended worker would have refused, because an
        /// operator asked for this item by name. Reported, never silent.
        /// </summary>
        public const string ResumeSuperseded = "CONTEXT_RESUME_SUPERSEDED";

        /// <summary>
        /// The session being resumed has no recorded context manifest, so what its agent was
        /// already given cannot be established and no change can be classified against it.
        /// </summary>
        public const string ManifestUnavailable = "CONTEXT_MANIFEST_UNAVAILABLE";

        /// <summary>A configured size limit was exceeded. Never truncates to fit.</summary>
        public const string TooLarge = ContextLimitResult.TooLargeCode;

        /// <summary>The conversation could not be read completely.</summary>
        public const string ReadFailed = "CONTEXT_READ_FAILED";

        /// <summary>This backend cannot assemble an approved context.</summary>
        public const string Unsupported = "CONTEXT_UNSUPPORTED";
    }
}

/// <summary>
/// Content-free context facts safe for routine operator surfaces. A refusal can retain these facts
/// even though no approved snapshot exists, allowing the dashboard to explain an old approval
/// cutoff or pending decision without retaining or displaying issue bodies.
/// </summary>
public sealed record ExecutionContextDiagnostics(
    ContextApproval Approval,
    int? IncludedCount = null,
    int? ExcludedCount = null,
    int? PendingCount = null,
    ContextRevision? Revision = null)
{
    public static ExecutionContextDiagnostics From(ExecutionContextSnapshot snapshot) =>
        new(
            snapshot.Approval,
            snapshot.IncludedCount,
            snapshot.ExcludedCount,
            snapshot.PendingCount,
            snapshot.Revision);

    public static ExecutionContextDiagnostics From(
        ContextApproval approval,
        IReadOnlyList<DiscussionDecision>? decisions = null) =>
        new(
            approval,
            decisions?.Count(decision => decision.Decision == DiscussionDecisionKind.Include),
            decisions?.Count(decision => decision.Decision == DiscussionDecisionKind.Exclude),
            decisions?.Count(decision => decision.Decision == DiscussionDecisionKind.Pending));
}
