using System.Text.Json.Serialization;
using Highbyte.Wrighty.Models;

// The namespace is "ApprovedContext" rather than "ExecutionContext": the latter collides with
// System.Threading.ExecutionContext wherever System.Threading is in scope, and "approved context"
// is the phrase plan 030 uses throughout. AgentContext is a different concept — the claimant
// identity a command runs under — and is deliberately left alone.
namespace Highbyte.Wrighty.ApprovedContext;

/// <summary>
/// One human or bot discussion entry that a maintainer may approve as task context. Backend-neutral:
/// the GitHub backend fills this from issue comments, and a backend without a comment system returns
/// an empty discussion rather than fabricating entries.
/// </summary>
public sealed record DiscussionEntry(
    string StableId,
    string Author,
    DateTimeOffset CreatedAt,
    string Body,
    string? AuthorAssociation = null,
    DateTimeOffset? LastEditedAt = null,
    string? Url = null,
    bool Minimized = false)
{
    /// <summary>
    /// The revision an approval decision has to cover. A decision counts only when it is strictly
    /// later than this instant, so editing an entry invalidates every earlier decision on it.
    /// </summary>
    public DateTimeOffset RevisionAt => LastEditedAt ?? CreatedAt;

    /// <summary>
    /// Whether <paramref name="decidedAt"/> covers this entry's current revision. Equality is not
    /// coverage: phase 0 measured whole-second precision on the GitHub timestamps this compares
    /// (finding F5), so same-second collisions are ordinary rather than exotic and must fail closed.
    /// </summary>
    public bool IsCoveredBy(DateTimeOffset decidedAt) => decidedAt > RevisionAt;
}

/// <summary>How a relevant discussion entry was resolved for one launch.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<DiscussionDecisionKind>))]
public enum DiscussionDecisionKind
{
    /// <summary>No valid decision covers the current revision. Blocks unattended launch.</summary>
    [JsonStringEnumMemberName("pending")]
    Pending,

    [JsonStringEnumMemberName("include")]
    Include,

    [JsonStringEnumMemberName("exclude")]
    Exclude
}

/// <summary>What produced a decision, kept for the diagnostic manifest and the revision digest.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<DiscussionDecisionSource>))]
public enum DiscussionDecisionSource
{
    [JsonStringEnumMemberName("none")]
    None,

    /// <summary>Covered by the batch cutoff, without an explicit per-entry reaction.</summary>
    [JsonStringEnumMemberName("batch")]
    Batch,

    /// <summary>An authorized include/exclude reaction on the current revision.</summary>
    [JsonStringEnumMemberName("reaction")]
    Reaction,

    /// <summary>Automatic include of a configured trusted-continuation author's current revision.</summary>
    [JsonStringEnumMemberName("trusted-author")]
    TrustedAuthor
}

/// <summary>
/// The resolved decision for one relevant entry, including the evidence that produced it. The
/// evidence participates in the revision digest, so a context approved by a different actor or a
/// different reaction is a different revision even when the text is identical.
/// </summary>
public sealed record DiscussionDecision(
    string CommentId,
    DiscussionDecisionKind Decision,
    DiscussionDecisionSource Source = DiscussionDecisionSource.None,
    string? DecidedBy = null,
    DateTimeOffset? DecidedAt = null,
    string? ReactionId = null)
{
    public static DiscussionDecision Pending(string commentId) => new(commentId, DiscussionDecisionKind.Pending);
}

/// <summary>Where a base approval came from, so an unset or unknown value cannot read as approved.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ContextApprovalSource>))]
public enum ContextApprovalSource
{
    /// <summary>No approval resolved. Fails closed.</summary>
    [JsonStringEnumMemberName("none")]
    None,

    /// <summary>A GitHub Project single-select field resolved to its exact approved option.</summary>
    [JsonStringEnumMemberName("project-field")]
    ProjectField,

    /// <summary>A backend with no discussion or approval surface, which approves its own title/body.</summary>
    [JsonStringEnumMemberName("backend-local")]
    BackendLocal
}

/// <summary>
/// Base title/body approval plus the batch cutoff for comments. Both come from the same field
/// update, but they answer different questions and are kept separate so a caller cannot use one
/// where it means the other.
/// </summary>
public sealed record ContextApproval(
    ContextApprovalSource Source = ContextApprovalSource.None,
    DateTimeOffset? BaseApprovedAt = null,
    DateTimeOffset? BatchCommentCutoff = null,
    int DecisionPolicyVersion = 1)
{
    public static ContextApproval NotApproved { get; } = new();

    public bool IsApproved => Source != ContextApprovalSource.None && BaseApprovedAt is not null;
}

/// <summary>
/// The observable evidence that binds the current title and body to an approval cutoff.
///
/// Title and body are tracked separately because GitHub exposes them differently, which phase 0
/// measured (finding F3): a body edit advances <c>lastEditedAt</c> and the user-content edit
/// history, while a title edit advances neither and is visible only as a timestamped rename event.
/// An implementation that read only the issue's edit metadata would miss every title change.
/// </summary>
public sealed record BaseContentRevision(
    string TitleHash,
    string BodyHash,
    DateTimeOffset? BodyLastEditedAt = null,
    int BodyEditCount = 0,
    DateTimeOffset? TitleLastRenamedAt = null,
    int TitleRenameCount = 0)
{
    /// <summary>
    /// The latest instant at which the base content is known to have changed, or null when neither
    /// the title nor the body has ever been edited.
    /// </summary>
    public DateTimeOffset? LastChangedAt => (BodyLastEditedAt, TitleLastRenamedAt) switch
    {
        (null, null) => null,
        ({ } body, null) => body,
        (null, { } title) => title,
        ({ } body, { } title) => body > title ? body : title
    };

    /// <summary>
    /// Whether an approval taken at <paramref name="approvedAt"/> still covers this content. An edit
    /// at exactly the approval instant is not covered: the timestamps compared here carry
    /// whole-second precision (finding F5), so ambiguity is common and resolves against approval.
    /// </summary>
    public bool IsCoveredBy(DateTimeOffset approvedAt) =>
        LastChangedAt is not { } changed || changed < approvedAt;
}

/// <summary>
/// A deterministic identifier for the exact normalized content supplied to one agent run. It is a
/// digest, not a signature: it identifies content, and proves nothing about who approved it or that
/// GitHub served it.
/// </summary>
public sealed record ContextRevision(int FormatVersion, string Digest, DateTimeOffset CapturedAt)
{
    /// <summary>A short form for operator diagnostics. Never used for comparison.</summary>
    public string ShortDigest => Digest.Length <= 19 ? Digest : Digest[..19] + "…";

    public bool Matches(ContextRevision? other) =>
        other is not null &&
        other.FormatVersion == FormatVersion &&
        string.Equals(other.Digest, Digest, StringComparison.Ordinal);
}

/// <summary>
/// One immutable approved context for one launch. Assembled by the worker before the agent starts
/// and never updated afterwards: a comment created while an agent is running belongs to a later
/// snapshot, not this one.
/// </summary>
public sealed record ExecutionContextSnapshot(
    WorkItemId ItemId,
    string Title,
    string Body,
    ContextApproval Approval,
    BaseContentRevision BaseRevision,
    ContextRevision Revision,
    IReadOnlyList<DiscussionEntry> Discussion,
    IReadOnlyList<DiscussionDecision> Decisions,
    string? SourceUrl = null)
{
    /// <summary>
    /// Every relevant entry resolved to Include or Exclude. A single Pending entry blocks the
    /// launch: silently omitting an undecided comment would quietly narrow the approved task.
    /// </summary>
    public bool IsFullyResolved => Decisions.All(d => d.Decision != DiscussionDecisionKind.Pending);

    /// <summary>
    /// The undecided entries. A method rather than a property because it allocates: a property
    /// reads as cheap and would be called repeatedly in a diagnostic loop.
    /// </summary>
    public IReadOnlyList<DiscussionDecision> PendingDecisions() =>
        Decisions.Where(d => d.Decision == DiscussionDecisionKind.Pending).ToArray();

    public int IncludedCount => Decisions.Count(d => d.Decision == DiscussionDecisionKind.Include);
    public int ExcludedCount => Decisions.Count(d => d.Decision == DiscussionDecisionKind.Exclude);
    public int PendingCount => Decisions.Count(d => d.Decision == DiscussionDecisionKind.Pending);

    /// <summary>An empty approved discussion for a backend that has no comment system.</summary>
    public static IReadOnlyList<DiscussionEntry> NoDiscussion { get; } = [];

    public static IReadOnlyList<DiscussionDecision> NoDecisions { get; } = [];
}
