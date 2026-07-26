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
    bool Minimized = false);

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
    DateTimeOffset CapturedAt)
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
                    entry.Minimized))
                .ToArray(),
            snapshot.Revision.CapturedAt);
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
    /// Every supplied entry is unchanged, but the approval evidence behind them is not — a decision
    /// was re-made by a different actor or through a different route. Blocked for operator review,
    /// because the approved set moved even though nothing visible did.
    /// </summary>
    [JsonStringEnumMemberName("decision-evidence-changed")]
    DecisionEvidenceChanged,

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
    /// Whether an unattended resume may proceed. Only identical or purely additive context
    /// qualifies: editing or deleting text a running session already saw cannot be undone, because
    /// a resumed model cannot unsee the old version.
    /// </summary>
    public bool AllowsUnattendedResume =>
        Kind is ContextChangeKind.Identical or ContextChangeKind.Additive;
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

        if (FindSuppliedEntryChange(recorded, current) is { } entryChange)
            return entryChange;

        var recordedIds = recorded.Included
            .Select(entry => entry.CommentId)
            .ToHashSet(StringComparer.Ordinal);
        var added = current.Included
            .Where(entry => !recordedIds.Contains(entry.CommentId))
            .Select(entry => entry.CommentId)
            .ToArray();

        // Every previously supplied entry survived intact and the digest still differs, so the
        // difference is either appended entries or changed decision evidence. Appended entries are
        // additive; a decision that changed without any visible content change is not, because the
        // approved set itself moved.
        if (added.Length == 0)
            return new ContextComparison(ContextChangeKind.DecisionEvidenceChanged, [],
                "The approval evidence changed without a change to the supplied entries.");

        var summary = added.Length == 1
            ? "One approved discussion entry was added since this session started."
            : $"{added.Length} approved discussion entries were added since this session started.";
        return new ContextComparison(ContextChangeKind.Additive, added, summary);
    }

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

            if (previous.Minimized != now.Minimized)
                return new ContextComparison(ContextChangeKind.EntryVisibilityChanged, [],
                    now.Minimized
                        ? $"Discussion entry {previous.CommentId} was hidden after it was supplied."
                        : $"Discussion entry {previous.CommentId} was unhidden after it was supplied.");
        }
        return null;
    }
}
