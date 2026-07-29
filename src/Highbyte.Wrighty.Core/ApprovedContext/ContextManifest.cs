using System.Text.Json.Serialization;

namespace Highbyte.Wrighty.ApprovedContext;

/// <summary>
/// One included entry as the manifest records it. The body hash is what makes a resumed session's
/// comparison possible without keeping the body itself: plan 030's privacy rules forbid retaining
/// full comment bodies in durable machine-local state.
/// </summary>
public sealed record ContextManifestEntry(
    string CommentId,
    string BodyHash,
    DateTimeOffset RevisionAt,
    // Recorded because hiding an entry carries no timestamp of its own (finding F4). Without it
    // here, a hide between runs would still block resume — the digest covers it — but only through
    // the catch-all branch, which would tell the operator the approval evidence changed rather than
    // that a specific entry was hidden.
    bool Minimized = false,
    // The rest of what the digest covers for this entry: author, association, timestamps, URL. See
    // ContextRevisionSerializer.HashProvenance. Optional so an entry recorded before it existed
    // still reads; absent means this field cannot be compared, and the classifier falls back to
    // refusing an unaccountable difference rather than assuming one.
    string? ProvenanceHash = null);

/// <summary>
/// One relevant entry's resolution as the manifest records it: exactly what the canonical form
/// covers for a decision, and nothing more.
///
/// The evidence behind the resolution — who decided, when, by which route — is deliberately absent,
/// because it is absent from the digest too (plan 030 amendment 3). It is kept on the session record
/// as diagnostics. Recording the resolutions here is what lets the classifier tell a decision on an
/// entry the session never saw apart from a digest movement it cannot account for.
/// </summary>
public sealed record ContextManifestDecision(string CommentId, DiscussionDecisionKind Decision);

/// <summary>
/// The compact record of what one run was actually given, kept with the local session so a later
/// launch can classify what changed.
///
/// It holds hashes and identifiers, never content. That is both a privacy requirement and the
/// reason a deletion is detectable at all: phase 0 found GitHub emits no usable
/// <c>CommentDeletedEvent</c> for a self-deleted comment and never identifies which comment went
/// away (finding F1), so detection is the difference between the recorded IDs and the current ones.
/// </summary>
public sealed record ContextManifest(
    int FormatVersion,
    string Digest,
    string TitleHash,
    string BodyHash,
    IReadOnlyList<ContextManifestEntry> Included,
    DateTimeOffset CapturedAt,
    // Optional so a manifest written before decisions were recorded still reads. Absent means the
    // classifier cannot attribute a digest movement to a decision, which fails closed.
    IReadOnlyList<ContextManifestDecision>? Decisions = null,
    // The item's source link, the last base field the digest covers that had no manifest entry.
    string? SourceUrlHash = null)
{
    public static ContextManifest From(ExecutionContextSnapshot snapshot) =>
        new(snapshot.Revision.FormatVersion,
            snapshot.Revision.Digest,
            snapshot.BaseRevision.TitleHash,
            snapshot.BaseRevision.BodyHash,
            ContextRevisionSerializer.Order(snapshot.Discussion)
                .Select(entry => new ContextManifestEntry(
                    entry.StableId,
                    ContextRevisionSerializer.HashContent(entry.Body),
                    entry.RevisionAt,
                    entry.Minimized,
                    ContextRevisionSerializer.HashProvenance(entry)))
                .ToArray(),
            snapshot.Revision.CapturedAt,
            snapshot.Decisions
                .Select(decision => new ContextManifestDecision(decision.CommentId, decision.Decision))
                .ToArray(),
            ContextRevisionSerializer.HashContent(snapshot.SourceUrl ?? string.Empty));
}

/// <summary>How the newly approved context differs from what a resumable session was given.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ContextChangeKind>))]
public enum ContextChangeKind
{
    /// <summary>Same revision. Resume needs no context beyond the manifest.</summary>
    [JsonStringEnumMemberName("identical")]
    Identical,

    /// <summary>Only new approved entries were appended. Resume carries those entries.</summary>
    [JsonStringEnumMemberName("additive")]
    Additive,

    /// <summary>The title or body changed. Blocked for operator review.</summary>
    [JsonStringEnumMemberName("base-changed")]
    BaseChanged,

    /// <summary>A previously supplied entry was edited. Blocked for operator review.</summary>
    [JsonStringEnumMemberName("entry-changed")]
    EntryChanged,

    /// <summary>
    /// A previously supplied entry was hidden or unhidden on the tracker. Blocked for operator
    /// review: the plan requires renewed approval for a minimize or unminimize, and the transition
    /// itself carries no timestamp to bind an approval to.
    /// </summary>
    [JsonStringEnumMemberName("entry-visibility-changed")]
    EntryVisibilityChanged,

    /// <summary>
    /// Every entry this session was given survives unchanged, nothing was added to what it will see,
    /// and the recorded decisions account for the difference: a comment it was never given was
    /// excluded, or a previously excluded one is gone. Unattended resume is allowed — nothing the
    /// agent holds changed and there is nothing new to hand it — and it is reported separately from
    /// <see cref="Identical"/> so an operator can still see that the approved set was not idle.
    ///
    /// Granted on positive evidence, never as a fall-through: an unexplained difference is
    /// <see cref="UnattributedChange"/> instead.
    /// </summary>
    [JsonStringEnumMemberName("decisions-changed")]
    DecisionsChanged,

    /// <summary>
    /// The digest moved and nothing the manifest records accounts for it. Blocked for operator
    /// review.
    ///
    /// This is the classifier's fail-closed floor. Every input to the canonical form now has a
    /// matching manifest field, so reaching this means either a manifest written before one of
    /// those fields existed, or a field added to the canonical form that was never mirrored here.
    /// The second is a bug, and this is what makes it announce itself as a refused resume instead
    /// of hiding as a silently permitted one.
    ///
    /// It must therefore never be the answer for a case that has a name: if a real transition ends
    /// up here, the fix is a comparison that recognises it, not widening what this permits.
    /// </summary>
    [JsonStringEnumMemberName("unattributed-change")]
    UnattributedChange,

    /// <summary>
    /// A previously supplied entry's provenance changed while its text did not: its author,
    /// association, timestamps or link. Blocked for operator review.
    ///
    /// Usually benign in origin — a commenter deleting their account, a repository rename moving
    /// every URL — but the agent was told who said each thing and where to find it, and that is now
    /// wrong. It is also not separable, from here, from an attribution that changed for a reason
    /// that matters, so it is reported precisely and left to a person.
    /// </summary>
    [JsonStringEnumMemberName("entry-provenance-changed")]
    EntryProvenanceChanged,

    /// <summary>A previously supplied entry is gone. Blocked for operator review.</summary>
    [JsonStringEnumMemberName("entry-removed")]
    EntryRemoved,

    /// <summary>No readable prior manifest and the digest differs. Blocked for operator review.</summary>
    [JsonStringEnumMemberName("manifest-unavailable")]
    ManifestUnavailable
}

/// <summary>
/// The classification plus what a resume prompt would need to carry. <see cref="NewEntryIds"/> is
/// populated only for an additive change, and identifies exactly the delta that plan 030 decision 20
/// sends inline instead of re-sending the whole snapshot.
/// </summary>
public sealed record ContextComparison(
    ContextChangeKind Kind,
    IReadOnlyList<string> NewEntryIds,
    string Reason)
{
    /// <summary>
    /// Whether an unattended resume may proceed. Only context that is identical, purely additive,
    /// or changed outside what the session was given qualifies: editing or deleting text a running
    /// session already saw cannot be undone, because a resumed model cannot unsee the old version.
    /// </summary>
    public bool AllowsUnattendedResume =>
        Kind is ContextChangeKind.Identical
             or ContextChangeKind.Additive
             or ContextChangeKind.DecisionsChanged;
}

public static class ContextChangeClassifier
{
    /// <summary>
    /// Compares a recorded manifest with a freshly approved snapshot and applies plan 030's
    /// change-classification table. Order matters: the base content is checked before the entries,
    /// so an operator reviewing a blocked resume is told the most fundamental thing that changed
    /// rather than an incidental consequence of it.
    /// </summary>
    public static ContextComparison Compare(ContextManifest? recorded, ExecutionContextSnapshot current) =>
        Compare(recorded, ContextManifest.From(current));

    public static ContextComparison Compare(ContextManifest? recorded, ContextManifest current)
    {
        if (recorded is null)
            return new ContextComparison(ContextChangeKind.ManifestUnavailable, [],
                "No recorded context manifest exists for this session, so what the agent was " +
                "previously given cannot be established.");

        // A format change means the two manifests were produced by different canonical forms and
        // their hashes are not comparable, even where the underlying content is unchanged.
        if (recorded.FormatVersion != current.FormatVersion)
            return new ContextComparison(ContextChangeKind.ManifestUnavailable, [],
                $"The recorded manifest uses context format {recorded.FormatVersion} and this run " +
                $"uses format {current.FormatVersion}; the two cannot be compared.");

        if (string.Equals(recorded.Digest, current.Digest, StringComparison.Ordinal))
            return new ContextComparison(ContextChangeKind.Identical, [],
                "The approved context is unchanged.");

        if (!string.Equals(recorded.TitleHash, current.TitleHash, StringComparison.Ordinal))
            return new ContextComparison(ContextChangeKind.BaseChanged, [],
                "The item title changed after the context was supplied.");

        if (!string.Equals(recorded.BodyHash, current.BodyHash, StringComparison.Ordinal))
            return new ContextComparison(ContextChangeKind.BaseChanged, [],
                "The item body changed after the context was supplied.");

        // Compared only when both sides recorded one, so an older manifest degrades to the
        // fail-closed floor rather than to a false match against an empty hash.
        if (recorded.SourceUrlHash is { } recordedUrl && current.SourceUrlHash is { } currentUrl &&
            !string.Equals(recordedUrl, currentUrl, StringComparison.Ordinal))
            return new ContextComparison(ContextChangeKind.BaseChanged, [],
                "The item's source link changed after the context was supplied.");

        if (FindSuppliedEntryChange(recorded, current) is { } entryChange)
            return entryChange;

        var recordedIds = recorded.Included
            .Select(entry => entry.CommentId)
            .ToHashSet(StringComparer.Ordinal);
        var added = current.Included
            .Where(entry => !recordedIds.Contains(entry.CommentId))
            .Select(entry => entry.CommentId)
            .ToArray();

        // Every previously supplied entry survived intact and the digest still differs. Appended
        // entries are additive; anything else has to be explained before it may resume.
        if (added.Length == 0)
            return ClassifyWithoutAddedEntries(recorded, current);

        var summary = added.Length == 1
            ? "One approved discussion entry was added since this session started."
            : $"{added.Length} approved discussion entries were added since this session started.";
        return new ContextComparison(ContextChangeKind.Additive, added, summary);
    }

    /// <summary>
    /// The digest moved, the base content did not, every supplied entry survived intact, and nothing
    /// was added. Either the decisions on entries this session was never given moved — which is
    /// harmless to it — or something the manifest does not record did, which cannot be judged and
    /// therefore cannot be waved through.
    ///
    /// The decision comparison is what makes the permissive answer evidence-based. Without it this
    /// would be "no other branch matched, so resume", and the first canonical-form field added
    /// without a matching manifest field would quietly join the permitted set.
    /// </summary>
    private static ContextComparison ClassifyWithoutAddedEntries(
        ContextManifest recorded, ContextManifest current)
    {
        // A manifest written before decisions were recorded cannot attribute anything.
        if (recorded.Decisions is null || current.Decisions is null)
            return Unattributed;

        var before = recorded.Decisions
            .Select(decision => (decision.CommentId, decision.Decision))
            .ToHashSet();
        var after = current.Decisions
            .Select(decision => (decision.CommentId, decision.Decision))
            .ToHashSet();

        return before.SetEquals(after)
            ? Unattributed
            : new ContextComparison(ContextChangeKind.DecisionsChanged, [],
                "Discussion entries were decided or excluded without changing what this session " +
                "was given.");
    }

    private static ContextComparison Unattributed { get; } =
        new(ContextChangeKind.UnattributedChange, [],
            "The approved context changed in a way this session's record cannot account for: the " +
            "content it was given is intact, and the difference is in provenance the manifest does " +
            "not retain.");

    /// <summary>
    /// Whether any entry this session was already given has changed. Split out of
    /// <see cref="Compare(ContextManifest?, ContextManifest)"/> so that method stays within its
    /// complexity budget as further transitions are recognised.
    ///
    /// Returns null when every previously supplied entry survived intact.
    /// </summary>
    private static ContextComparison? FindSuppliedEntryChange(
        ContextManifest recorded, ContextManifest current)
    {
        var currentById = current.Included.ToDictionary(entry => entry.CommentId, StringComparer.Ordinal);
        foreach (var previous in recorded.Included)
        {
            // Absence IS the deletion signal (finding F1). Nothing else reports which comment went.
            if (!currentById.TryGetValue(previous.CommentId, out var now))
                return new ContextComparison(ContextChangeKind.EntryRemoved, [],
                    $"Discussion entry {previous.CommentId} was supplied to this session and is no " +
                    "longer part of the approved context.");

            if (!string.Equals(previous.BodyHash, now.BodyHash, StringComparison.Ordinal))
                return new ContextComparison(ContextChangeKind.EntryChanged, [],
                    $"Discussion entry {previous.CommentId} was edited after it was supplied.");

            // An edit that leaves the text identical still counts. It advances the revision every
            // approval decision is measured against, so a decision that covered the old revision no
            // longer covers this one — and an edit-and-revert is exactly how a supplied entry gets
            // rewritten and put back while a session is paused.
            if (previous.RevisionAt != now.RevisionAt)
                return new ContextComparison(ContextChangeKind.EntryChanged, [],
                    $"Discussion entry {previous.CommentId} was edited after it was supplied, " +
                    "even though its text is unchanged.");

            if (previous.Minimized != now.Minimized)
                return new ContextComparison(ContextChangeKind.EntryVisibilityChanged, [],
                    now.Minimized
                        ? $"Discussion entry {previous.CommentId} was hidden after it was supplied."
                        : $"Discussion entry {previous.CommentId} was unhidden after it was supplied.");

            // Last, because it is the least severe of the per-entry differences: the text stood
            // still and only its attribution moved. Compared only when both sides recorded one.
            if (previous.ProvenanceHash is { } was && now.ProvenanceHash is { } isNow &&
                !string.Equals(was, isNow, StringComparison.Ordinal))
                return new ContextComparison(ContextChangeKind.EntryProvenanceChanged, [],
                    $"Discussion entry {previous.CommentId} still reads the same, but its author, " +
                    "timestamps or link changed after it was supplied.");
        }
        return null;
    }
}
