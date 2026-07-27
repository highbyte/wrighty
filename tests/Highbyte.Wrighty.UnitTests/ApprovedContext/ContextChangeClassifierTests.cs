using Highbyte.Wrighty.ApprovedContext;
using Highbyte.Wrighty.Models;

namespace Highbyte.Wrighty.UnitTests.ApprovedContext;

/// <summary>
/// The change classification decides whether an unattended resume is safe. Only identical or purely
/// additive context qualifies, because a resumed model cannot unsee text that was edited or removed
/// after it saw it.
/// </summary>
public class ContextChangeClassifierTests
{
    private static readonly WorkItemId Item = new("github:owner/repo#42");
    private static readonly DateTimeOffset Captured = new(2026, 7, 26, 12, 0, 0, TimeSpan.Zero);

    private static DiscussionEntry Entry(string id, string body, int hourCreated = 10, bool minimized = false) =>
        new(id, "octocat",
            new DateTimeOffset(2026, 7, 26, hourCreated, 0, 0, TimeSpan.Zero), body,
            Minimized: minimized);

    private static ExecutionContextSnapshot Snapshot(
        string title = "Add retry handling",
        string body = "The worker should retry once.",
        params DiscussionEntry[] entries)
    {
        var included = entries.Length == 0 ? [Entry("c1", "first")] : entries;
        var decisions = included
            .Select(e => new DiscussionDecision(e.StableId, DiscussionDecisionKind.Include,
                DiscussionDecisionSource.Batch))
            .ToArray();
        return new ExecutionContextSnapshot(
            Item, title, body,
            new ContextApproval(ContextApprovalSource.ProjectField, Captured, Captured),
            new BaseContentRevision(
                ContextRevisionSerializer.HashContent(title),
                ContextRevisionSerializer.HashContent(body)),
            ContextRevisionSerializer.Compute(Item, title, body, null, included, decisions, Captured),
            included, decisions);
    }

    private static ContextComparison CompareAgainst(
        ExecutionContextSnapshot before,
        ExecutionContextSnapshot after) =>
        ContextChangeClassifier.Compare(ContextManifest.From(before), after);

    [Fact]
    public void UnchangedContextIsIdenticalAndResumable()
    {
        var result = CompareAgainst(Snapshot(), Snapshot());
        Assert.Equal(ContextChangeKind.Identical, result.Kind);
        Assert.True(result.AllowsUnattendedResume);
        Assert.Empty(result.NewEntryIds);
    }

    [Fact]
    public void AnAppendedEntryIsAdditiveAndNamesTheDelta()
    {
        var result = CompareAgainst(
            Snapshot(entries: Entry("c1", "first")),
            Snapshot(entries: [Entry("c1", "first"), Entry("c2", "second", 11)]));

        Assert.Equal(ContextChangeKind.Additive, result.Kind);
        Assert.True(result.AllowsUnattendedResume);
        // The delta is exactly what a resume prompt carries inline (decision 20).
        Assert.Equal(["c2"], result.NewEntryIds);
    }

    [Fact]
    public void SeveralAppendedEntriesAreAllReportedAsTheDelta()
    {
        var result = CompareAgainst(
            Snapshot(entries: Entry("c1", "first")),
            Snapshot(entries: [Entry("c1", "first"), Entry("c2", "second", 11), Entry("c3", "third", 12)]));

        Assert.Equal(ContextChangeKind.Additive, result.Kind);
        Assert.Equal(["c2", "c3"], result.NewEntryIds.OrderBy(id => id, StringComparer.Ordinal));
    }

    [Fact]
    public void ATitleChangeBlocksResume()
    {
        var result = CompareAgainst(Snapshot(), Snapshot(title: "Something else"));
        Assert.Equal(ContextChangeKind.BaseChanged, result.Kind);
        Assert.False(result.AllowsUnattendedResume);
        Assert.Contains("title", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ABodyChangeBlocksResume()
    {
        var result = CompareAgainst(Snapshot(), Snapshot(body: "Different requirements now."));
        Assert.Equal(ContextChangeKind.BaseChanged, result.Kind);
        Assert.False(result.AllowsUnattendedResume);
        Assert.Contains("body", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EditingAPreviouslySuppliedEntryBlocksResume()
    {
        var result = CompareAgainst(
            Snapshot(entries: Entry("c1", "first")),
            Snapshot(entries: Entry("c1", "first, amended")));

        Assert.Equal(ContextChangeKind.EntryChanged, result.Kind);
        Assert.False(result.AllowsUnattendedResume);
        Assert.Contains("c1", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void ARemovedEntryBlocksResumeAndNamesTheMissingEntry()
    {
        // Finding F1: absence in the current set is the only signal that identifies WHICH comment
        // went away, because GitHub's deletion event does not name it and may not fire at all.
        var result = CompareAgainst(
            Snapshot(entries: [Entry("c1", "first"), Entry("c2", "second", 11)]),
            Snapshot(entries: Entry("c1", "first")));

        Assert.Equal(ContextChangeKind.EntryRemoved, result.Kind);
        Assert.False(result.AllowsUnattendedResume);
        Assert.Contains("c2", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void RemovalIsDetectedEvenWhenAnEntryIsAlsoAdded()
    {
        // A swap must not read as additive just because the count is unchanged.
        var result = CompareAgainst(
            Snapshot(entries: [Entry("c1", "first"), Entry("c2", "second", 11)]),
            Snapshot(entries: [Entry("c1", "first"), Entry("c3", "third", 12)]));

        Assert.Equal(ContextChangeKind.EntryRemoved, result.Kind);
        Assert.False(result.AllowsUnattendedResume);
    }

    [Fact]
    public void HidingAPreviouslySuppliedEntryBlocksResumeAndSaysSo()
    {
        // Hiding carries no timestamp of its own (finding F4), so the manifest records the state
        // directly. Without it this would still block — the digest covers minimized state — but the
        // operator would be told the approval evidence changed, which is not what happened.
        var result = CompareAgainst(
            Snapshot(entries: Entry("c1", "first")),
            Snapshot(entries: Entry("c1", "first", minimized: true)));

        Assert.Equal(ContextChangeKind.EntryVisibilityChanged, result.Kind);
        Assert.False(result.AllowsUnattendedResume);
        Assert.Contains("c1", result.Reason, StringComparison.Ordinal);
        Assert.Contains("hidden", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UnhidingAPreviouslySuppliedEntryAlsoBlocksResume()
    {
        // The reverse transition matters just as much: unhiding silently widens what the agent is
        // given, and is equally unobservable through timestamps.
        var result = CompareAgainst(
            Snapshot(entries: Entry("c1", "first", minimized: true)),
            Snapshot(entries: Entry("c1", "first")));

        Assert.Equal(ContextChangeKind.EntryVisibilityChanged, result.Kind);
        Assert.False(result.AllowsUnattendedResume);
        Assert.Contains("unhidden", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AnEditIsReportedAheadOfAVisibilityChange()
    {
        // Both changed at once: the operator is told the most fundamental thing, not an incidental
        // consequence of it.
        var result = CompareAgainst(
            Snapshot(entries: Entry("c1", "first")),
            Snapshot(entries: Entry("c1", "amended", minimized: true)));

        Assert.Equal(ContextChangeKind.EntryChanged, result.Kind);
    }

    [Fact]
    public void AMissingManifestBlocksResume()
    {
        var result = ContextChangeClassifier.Compare(null, Snapshot());
        Assert.Equal(ContextChangeKind.ManifestUnavailable, result.Kind);
        Assert.False(result.AllowsUnattendedResume);
    }

    [Fact]
    public void AManifestFromADifferentFormatVersionBlocksResume()
    {
        // Hashes produced by different canonical forms are not comparable, so "unchanged" cannot be
        // established even when the content genuinely did not change.
        var recorded = ContextManifest.From(Snapshot()) with { FormatVersion = 99 };
        var result = ContextChangeClassifier.Compare(recorded, Snapshot());

        Assert.Equal(ContextChangeKind.ManifestUnavailable, result.Kind);
        Assert.False(result.AllowsUnattendedResume);
    }

    [Fact]
    public void ChangedApprovalEvidenceAloneBlocksResume()
    {
        // Same entries and same text, but re-decided by a different route. The approved set moved
        // even though nothing visible changed, so this is not additive.
        var before = Snapshot(entries: Entry("c1", "first"));
        var entries = new[] { Entry("c1", "first") };
        var decisions = new[]
        {
            new DiscussionDecision("c1", DiscussionDecisionKind.Include,
                DiscussionDecisionSource.Reaction, "maintainer", Captured, "reaction-9")
        };
        var after = before with
        {
            Decisions = decisions,
            Revision = ContextRevisionSerializer.Compute(
                Item, before.Title, before.Body, null, entries, decisions, Captured)
        };

        var result = CompareAgainst(before, after);
        Assert.Equal(ContextChangeKind.DecisionEvidenceChanged, result.Kind);
        Assert.False(result.AllowsUnattendedResume);
        Assert.Contains("approval evidence", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheManifestCarriesNoContent()
    {
        const string secret = "correct-horse-battery-staple";
        var manifest = ContextManifest.From(Snapshot(title: secret, body: secret,
            entries: Entry("c1", secret)));

        var rendered = string.Join('\n',
            manifest.TitleHash, manifest.BodyHash, manifest.Digest,
            string.Join('\n', manifest.Included.Select(e => $"{e.CommentId} {e.BodyHash} {e.RevisionAt:O}")));

        Assert.DoesNotContain(secret, rendered, StringComparison.Ordinal);
    }
}
