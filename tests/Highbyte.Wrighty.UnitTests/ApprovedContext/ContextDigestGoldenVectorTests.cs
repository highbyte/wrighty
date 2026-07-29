using System.Security.Cryptography;
using System.Text;
using Highbyte.Wrighty.ApprovedContext;
using Highbyte.Wrighty.Models;

namespace Highbyte.Wrighty.UnitTests.ApprovedContext;

/// <summary>
/// Committed vectors for the canonical form: a fixed input, the exact text it canonicalizes to, and
/// the exact digest of that text.
///
/// The other serializer tests are relational — they assert that some change moves the digest, or
/// that some incidental difference does not. Every one of them would still pass if the whole
/// canonical form were rewritten, because they only compare digests against each other. These pin
/// the absolute values, so a change to what is serialized, to field order, or to framing cannot
/// pass review as an internal detail: it appears here as a diff of the literals below, and whoever
/// makes it has to bump <see cref="ContextRevisionSerializer.FormatVersion"/> deliberately.
///
/// Updating an expected value is therefore never routine. A digest that moves without a format bump
/// means recorded sessions silently stop comparing equal to the content they were given.
///
/// The canonical form is framed with ASCII unit and record separators, which no editor renders. The
/// expected text below shows them as the visible tokens US and RS, with a line break after each
/// record so the literal is readable and diffable; <see cref="Readable"/> applies exactly that
/// substitution to the real output. The digest is taken over the real separators, so a readable
/// rendering cannot drift from what is actually hashed without the digest assertion failing too.
/// </summary>
public class ContextDigestGoldenVectorTests
{
    private const string Unit = "\u001f";
    private const string Record = "\u001e";

    private static readonly WorkItemId Item = new("github:owner/repo#42");

    /// <summary>Renders the real separators as visible tokens, one record per line.</summary>
    private static string Readable(string canonical) =>
        canonical.Replace(Unit, "<US>", StringComparison.Ordinal)
                 .Replace(Record, "<RS>\n", StringComparison.Ordinal);

    private static string DigestOf(string canonical) =>
        "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));

    [Fact]
    public void TheFormatVersionIsTwo() =>
        // Named on its own so a bump cannot be absorbed into an expected-value update below without
        // somebody deciding to change this line.
        Assert.Equal(2, ContextRevisionSerializer.FormatVersion);

    [Fact]
    public void AnEmptyDiscussionCanonicalizesToTheBaseFieldsAlone()
    {
        // The Local Markdown backend produces exactly this shape: base content, no entries, no
        // decisions. It is the shortest canonical form there is.
        var canonical = ContextRevisionSerializer.Canonicalize(
            Item, "Add retry handling", "The worker should retry once.", null, [], []);

        Assert.Equal(
            """
            format<US>2<RS>
            item<US>github:owner/repo#42<RS>
            title<US>Add retry handling<RS>
            body<US>The worker should retry once.<RS>
            url<US><RS>

            """,
            Readable(canonical));
        Assert.Equal(
            "sha256:ff9e399a598add32e17bafdab210868b5604cc27e741c00a30c29ce66cacaf6b",
            DigestOf(canonical));
    }

    [Fact]
    public void AnIncludedEntryAndAnExcludedDecisionCanonicalizeInFullDetail()
    {
        // The load-bearing case: one entry supplied to the agent, one entry deliberately kept from
        // it. The included entry contributes its provenance and exact body; the excluded one
        // contributes its identity and verdict, and no text at all.
        var entry = new DiscussionEntry(
            "c1", "octocat",
            new DateTimeOffset(2026, 7, 26, 10, 0, 0, TimeSpan.Zero),
            "Please also handle the empty case.",
            "MEMBER",
            new DateTimeOffset(2026, 7, 26, 11, 30, 0, TimeSpan.Zero),
            "https://github.com/owner/repo/issues/42#issuecomment-1");
        var decisions = new[]
        {
            new DiscussionDecision("c1", DiscussionDecisionKind.Include,
                DiscussionDecisionSource.Batch, "maintainer",
                new DateTimeOffset(2026, 7, 26, 12, 0, 0, TimeSpan.Zero)),
            new DiscussionDecision("c2", DiscussionDecisionKind.Exclude,
                DiscussionDecisionSource.Reaction, "reviewer",
                new DateTimeOffset(2026, 7, 26, 12, 5, 0, TimeSpan.Zero), "reaction-7")
        };

        var canonical = ContextRevisionSerializer.Canonicalize(
            Item, "Add retry handling", "The worker should retry once.",
            "https://github.com/owner/repo/issues/42", [entry], decisions);

        Assert.Equal(
            """
            format<US>2<RS>
            item<US>github:owner/repo#42<RS>
            title<US>Add retry handling<RS>
            body<US>The worker should retry once.<RS>
            url<US>https://github.com/owner/repo/issues/42<RS>
            entry<US>c1<US>octocat<US>MEMBER<US>2026-07-26T10:00:00.0000000Z<US>2026-07-26T11:30:00.0000000Z<US>https://github.com/owner/repo/issues/42#issuecomment-1<US>visible<US>Please also handle the empty case.<RS>
            decision<US>c1<US>Include<RS>
            decision<US>c2<US>Exclude<RS>

            """,
            Readable(canonical));
        Assert.Equal(
            "sha256:9ab4bd292e7073b5363aa762ed074e75ad6489aa7c733c5812a1fc8b613d2a7a",
            DigestOf(canonical));

        // Amendment 3, asserted as absent text rather than only as an equal digest: no deciding
        // actor, decision instant, reaction id, or decision source reaches the canonical form.
        Assert.DoesNotContain("maintainer", canonical, StringComparison.Ordinal);
        Assert.DoesNotContain("reviewer", canonical, StringComparison.Ordinal);
        Assert.DoesNotContain("reaction-7", canonical, StringComparison.Ordinal);
        Assert.DoesNotContain("Batch", canonical, StringComparison.Ordinal);
        Assert.DoesNotContain("12:00:00", canonical, StringComparison.Ordinal);
    }

    [Fact]
    public void ApprovedTextSurvivesByteForByteApartFromLineEndings()
    {
        // What the canonical form does to approved text is a security-relevant choice, so it is
        // pinned rather than described: CRLF and CR collapse to LF, and nothing else — no trimming,
        // no case folding, no Unicode normalization — touches the body. Written as an explicit
        // concatenation because the expected value carries leading and trailing spaces that a raw
        // string literal would make depend on editor behaviour.
        var entry = new DiscussionEntry(
            "c1", "octocat",
            new DateTimeOffset(2026, 7, 26, 10, 0, 0, TimeSpan.Zero),
            "  line one\r\nline two\ttabbed\r\rend  ",
            Minimized: true);

        var canonical = ContextRevisionSerializer.Canonicalize(
            Item, "t", "b", null, [entry],
            [new DiscussionDecision("c1", DiscussionDecisionKind.Include)]);

        Assert.Equal(
            "format<US>2<RS>\n" +
            "item<US>github:owner/repo#42<RS>\n" +
            "title<US>t<RS>\n" +
            "body<US>b<RS>\n" +
            "url<US><RS>\n" +
            "entry<US>c1<US>octocat<US><US>2026-07-26T10:00:00.0000000Z<US><US><US>minimized" +
            "<US>  line one\nline two\ttabbed\n\nend  <RS>\n" +
            "decision<US>c1<US>Include<RS>\n",
            Readable(canonical));
        Assert.Equal(
            "sha256:af709e13e185e8b8dc1cebfb78e0bf1b7c797d4c116dcf9aa04648e5dd821a01",
            DigestOf(canonical));
    }
}
