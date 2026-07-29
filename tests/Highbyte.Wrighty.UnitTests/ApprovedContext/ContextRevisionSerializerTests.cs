using Highbyte.Wrighty.ApprovedContext;
using Highbyte.Wrighty.Models;

namespace Highbyte.Wrighty.UnitTests.ApprovedContext;

/// <summary>
/// The revision digest decides whether a resumed session is given the same task it started with, so
/// these cover both directions: identical content must hash identically regardless of incidental
/// representation, and any change to meaning must change the digest.
/// </summary>
public class ContextRevisionSerializerTests
{
    private static readonly WorkItemId Item = new("github:owner/repo#42");
    private static readonly DateTimeOffset Captured = new(2026, 7, 26, 12, 0, 0, TimeSpan.Zero);

    private static DiscussionEntry Entry(
        string id = "c1",
        string author = "octocat",
        string body = "Please also handle the empty case.",
        DateTimeOffset? createdAt = null,
        DateTimeOffset? editedAt = null,
        string? association = "MEMBER",
        string? url = "https://github.com/owner/repo/issues/42#issuecomment-1",
        bool minimized = false) =>
        new(id, author, createdAt ?? new DateTimeOffset(2026, 7, 26, 10, 0, 0, TimeSpan.Zero),
            body, association, editedAt, url, minimized);

    private static ContextRevision Compute(
        IReadOnlyList<DiscussionEntry>? included = null,
        IReadOnlyList<DiscussionDecision>? decisions = null,
        string title = "Add retry handling",
        string body = "The worker should retry once.",
        string? url = "https://github.com/owner/repo/issues/42") =>
        ContextRevisionSerializer.Compute(Item, title, body, url,
            included ?? [Entry()],
            decisions ?? [new DiscussionDecision("c1", DiscussionDecisionKind.Include,
                DiscussionDecisionSource.Batch)],
            Captured);

    [Fact]
    public void IdenticalContentProducesAnIdenticalDigest() =>
        Assert.Equal(Compute().Digest, Compute().Digest);

    [Fact]
    public void TheDigestIsNamespacedByFormatVersion()
    {
        // The version is part of the hashed input, so a recorded digest from an older canonical
        // form can never compare equal to one from a newer form describing the same content.
        var canonical = ContextRevisionSerializer.Canonicalize(
            Item, "t", "b", null, [], []);
        Assert.Contains($"format", canonical, StringComparison.Ordinal);
        Assert.Contains(ContextRevisionSerializer.FormatVersion.ToString(), canonical, StringComparison.Ordinal);
        Assert.Equal(ContextRevisionSerializer.FormatVersion, Compute().FormatVersion);
    }

    [Fact]
    public void CapturedAtDoesNotAffectTheDigest()
    {
        // Two reads of unchanged content at different moments are the same approved context.
        var first = ContextRevisionSerializer.Compute(Item, "t", "b", null, [Entry()], [], Captured);
        var second = ContextRevisionSerializer.Compute(
            Item, "t", "b", null, [Entry()], [], Captured.AddHours(3));
        Assert.Equal(first.Digest, second.Digest);
        Assert.NotEqual(first.CapturedAt, second.CapturedAt);
    }

    [Theory]
    [InlineData("different title", null, null)]
    [InlineData(null, "different body", null)]
    [InlineData(null, null, "https://example.invalid/other")]
    public void ChangingTheBaseContentChangesTheDigest(string? title, string? body, string? url)
    {
        var changed = Compute(
            title: title ?? "Add retry handling",
            body: body ?? "The worker should retry once.",
            url: url ?? "https://github.com/owner/repo/issues/42");
        Assert.NotEqual(Compute().Digest, changed.Digest);
    }

    [Fact]
    public void ChangingAnEntryBodyChangesTheDigest() =>
        Assert.NotEqual(Compute().Digest, Compute([Entry(body: "Something else entirely.")]).Digest);

    [Fact]
    public void ChangingEntryProvenanceChangesTheDigest()
    {
        Assert.NotEqual(Compute().Digest, Compute([Entry(author: "someone-else")]).Digest);
        Assert.NotEqual(Compute().Digest, Compute([Entry(association: "NONE")]).Digest);
        Assert.NotEqual(Compute().Digest, Compute([Entry(url: "https://example.invalid/x")]).Digest);
    }

    [Fact]
    public void EditingAnEntryChangesTheDigestEvenWhenTheBodyIsIdentical() =>
        Assert.NotEqual(
            Compute().Digest,
            Compute([Entry(editedAt: new DateTimeOffset(2026, 7, 26, 11, 0, 0, TimeSpan.Zero))]).Digest);

    [Fact]
    public void MinimizedStateParticipatesInTheDigest() =>
        Assert.NotEqual(Compute().Digest, Compute([Entry(minimized: true)]).Digest);

    [Fact]
    public void DecisionEvidenceDoesNotParticipateInTheDigest()
    {
        // Same included text, same resolution, different actor and route. Hashing the evidence made
        // a re-approval of unchanged content look like a context change to a session already
        // holding it; what the digest is relied on for is the content the model saw.
        var byBatch = Compute(decisions:
            [new DiscussionDecision("c1", DiscussionDecisionKind.Include, DiscussionDecisionSource.Batch)]);
        var byReaction = Compute(decisions:
            [new DiscussionDecision("c1", DiscussionDecisionKind.Include, DiscussionDecisionSource.Reaction,
                "maintainer", Captured, "reaction-7")]);
        Assert.Equal(byBatch.Digest, byReaction.Digest);
    }

    [Fact]
    public void ADecisionsResolutionStillParticipatesInTheDigest()
    {
        // The evidence goes; the verdict stays. An entry flipping between include and exclude is a
        // different approved context even where the evidence behind the flip is not compared.
        var included = Compute(decisions:
            [new DiscussionDecision("c1", DiscussionDecisionKind.Include, DiscussionDecisionSource.Batch)]);
        var excluded = Compute(decisions:
            [new DiscussionDecision("c1", DiscussionDecisionKind.Exclude, DiscussionDecisionSource.Batch)]);
        Assert.NotEqual(included.Digest, excluded.Digest);
    }

    [Fact]
    public void AnExcludedEntryStillChangesTheDigest()
    {
        // The excluded entry contributes no text, but its existence and disposition are part of
        // what was approved: an entry silently appearing or disappearing must be visible.
        var withoutExclusion = Compute(decisions:
            [new DiscussionDecision("c1", DiscussionDecisionKind.Include, DiscussionDecisionSource.Batch)]);
        var withExclusion = Compute(decisions:
        [
            new DiscussionDecision("c1", DiscussionDecisionKind.Include, DiscussionDecisionSource.Batch),
            new DiscussionDecision("c2", DiscussionDecisionKind.Exclude, DiscussionDecisionSource.Reaction)
        ]);
        Assert.NotEqual(withoutExclusion.Digest, withExclusion.Digest);
    }

    [Fact]
    public void EntryOrderDoesNotDependOnInputOrder()
    {
        var first = Entry("c1", createdAt: new DateTimeOffset(2026, 7, 26, 9, 0, 0, TimeSpan.Zero));
        var second = Entry("c2", createdAt: new DateTimeOffset(2026, 7, 26, 10, 0, 0, TimeSpan.Zero));
        Assert.Equal(Compute([first, second]).Digest, Compute([second, first]).Digest);
    }

    [Fact]
    public void EntriesCreatedInTheSameInstantOrderByStableId()
    {
        var at = new DateTimeOffset(2026, 7, 26, 9, 0, 0, TimeSpan.Zero);
        var a = Entry("c1", createdAt: at);
        var b = Entry("c2", createdAt: at);
        Assert.Equal(Compute([a, b]).Digest, Compute([b, a]).Digest);
    }

    [Fact]
    public void LineEndingsAreNormalized()
    {
        var lf = Compute([Entry(body: "one\ntwo")]);
        var crlf = Compute([Entry(body: "one\r\ntwo")]);
        var cr = Compute([Entry(body: "one\rtwo")]);
        Assert.Equal(lf.Digest, crlf.Digest);
        Assert.Equal(lf.Digest, cr.Digest);
    }

    [Fact]
    public void MeaningfulWhitespaceIsPreserved()
    {
        // Trimming would quietly change approved Markdown — indentation is a code fence's meaning.
        Assert.NotEqual(
            Compute([Entry(body: "text")]).Digest,
            Compute([Entry(body: "  text  ")]).Digest);
    }

    [Theory]
    [InlineData("emoji 🎯 and combining é")]
    [InlineData("right-to-left אבג")]
    [InlineData("```\nnested ``` fence\n```")]
    [InlineData("<!-- wrighty-claim:v3 {\"looks\":\"like a marker\"} -->")]
    [InlineData("---BEGIN APPROVED CONTEXT---")]
    public void AdversarialBodiesHashDeterministically(string body) =>
        Assert.Equal(Compute([Entry(body: body)]).Digest, Compute([Entry(body: body)]).Digest);

    [Fact]
    public void ContentCannotForgeAFieldBoundary()
    {
        // The canonical form is framed with ASCII separators that cannot occur in GitHub content.
        // Text that merely looks like a boundary must not collide with a genuinely different shape.
        var looksLikeBoundary = Compute([Entry(id: "c1", body: "c2\u001fattacker")]);
        var actuallyTwo = Compute(
            [Entry("c1", body: "x"), Entry("c2", body: "attacker")],
            [new DiscussionDecision("c1", DiscussionDecisionKind.Include, DiscussionDecisionSource.Batch)]);
        Assert.NotEqual(looksLikeBoundary.Digest, actuallyTwo.Digest);
    }

    [Fact]
    public void EmptyDiscussionIsValidAndStable()
    {
        // The Local Markdown backend returns exactly this shape.
        var first = ContextRevisionSerializer.Compute(Item, "t", "b", null, [], [], Captured);
        var second = ContextRevisionSerializer.Compute(Item, "t", "b", null, [], [], Captured);
        Assert.Equal(first.Digest, second.Digest);
        Assert.StartsWith("sha256:", first.Digest, StringComparison.Ordinal);
    }

    [Fact]
    public void TheDigestNeverContainsSourceContent()
    {
        const string secret = "correct-horse-battery-staple";
        var revision = Compute([Entry(body: secret)], title: secret, body: secret);
        Assert.DoesNotContain(secret, revision.Digest, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, revision.ShortDigest, StringComparison.Ordinal);
        Assert.Matches("^sha256:[0-9a-f]{64}$", revision.Digest);
    }

    [Fact]
    public void EqualInstantsInDifferentOffsetsHashIdentically()
    {
        var utc = new DateTimeOffset(2026, 7, 26, 10, 0, 0, TimeSpan.Zero);
        var offset = utc.ToOffset(TimeSpan.FromHours(2));
        Assert.Equal(
            Compute([Entry(createdAt: utc)]).Digest,
            Compute([Entry(createdAt: offset)]).Digest);
    }

    [Fact]
    public void MatchesRequiresBothVersionAndDigest()
    {
        var revision = Compute();
        Assert.True(revision.Matches(revision with { CapturedAt = Captured.AddDays(1) }));
        Assert.False(revision.Matches(revision with { Digest = "sha256:0" }));
        Assert.False(revision.Matches(revision with { FormatVersion = 99 }));
        Assert.False(revision.Matches(null));
    }
}
