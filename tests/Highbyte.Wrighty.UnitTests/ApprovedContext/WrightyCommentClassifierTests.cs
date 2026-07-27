using System.Text.Json;
using Highbyte.Wrighty.AgentContext;
using Highbyte.Wrighty.ApprovedContext;
using Highbyte.Wrighty.Claims;
using Highbyte.Wrighty.Models;
using Highbyte.Wrighty.Workers;

namespace Highbyte.Wrighty.UnitTests.ApprovedContext;

/// <summary>
/// The classifier decides which comments an agent never sees, so its dangerous failure is the quiet
/// one: classifying an ordinary comment as protocol removes it from the approved task with nobody
/// deciding to. Most of these cases are therefore attempts to get a comment hidden.
/// </summary>
public class WrightyCommentClassifierTests
{
    private static readonly WorkItemId Item = new("github:owner/repo#42");

    private static bool Authorized(string? author) => author == "maintainer";
    private static bool NobodyAuthorized(string? author) => false;

    private static CommentClassification Classify(
        string? body, string? author = "maintainer", Func<string?, bool>? authority = null) =>
        WrightyCommentClassifier.Classify(body, author, authority ?? Authorized);

    private static string ClaimComment()
    {
        var record = new ClaimRecord(
            Version: 3,
            EventId: Guid.NewGuid().ToString("N"),
            InstallationId: "abcdef123456",
            ClaimedAt: DateTimeOffset.UnixEpoch,
            ExpiresAt: DateTimeOffset.UnixEpoch.AddHours(1),
            EventType: "acquired",
            ClaimantId: "agent:worker:1",
            ClaimToken: "token-1",
            ClaimantKind: ClaimantKinds.ToStorageValue(ClaimantKind.Agent));
        return ClaimMarker.Format(record);
    }

    private static string ReportComment(
        string itemId = "github:owner/repo#42",
        string runId = "run-abc123",
        string reportId = "report-def456") =>
        $$"""
        {{WrightyCommentClassifier.SessionReportPrefix}}
        {"itemId":"{{itemId}}","runId":"{{runId}}","reportId":"{{reportId}}"}
        -->
        ### Wrighty session report

        **Observed outcome:** Needs attention
        """;

    // --- ordinary discussion stays ordinary -----------------------------------------------------

    [Fact]
    public void APlainCommentIsDiscussion()
    {
        var result = Classify("Please also handle the empty case.");
        Assert.Equal(WrightyCommentKind.Discussion, result.Kind);
        Assert.False(result.IsProtocol);
    }

    [Fact]
    public void AnEmptyCommentIsDiscussionNotProtocol()
    {
        // Calling it protocol would let an empty body bypass the decision rules entirely.
        Assert.Equal(WrightyCommentKind.Discussion, Classify("   ").Kind);
        Assert.Equal(WrightyCommentKind.Discussion, Classify(null).Kind);
    }

    [Theory]
    [InlineData("Wrighty should retry this.")]
    [InlineData("wrighty-claim is what we call it internally")]
    [InlineData("<!-- wrighty-something-else -->")]
    [InlineData("<!-- wrighty-claim:v9 {} -->")]
    [InlineData("The bot posts a `<!-- wrighty-handover` marker, see docs.")]
    public void CommentsThatMerelyMentionWrightyStayDiscussion(string body) =>
        Assert.Equal(WrightyCommentKind.Discussion, Classify(body).Kind);

    [Fact]
    public void AMarkerLookingSubstringInQuotedTextStaysDiscussion()
    {
        var body = "Here is what the marker looks like:\n\n```\n" +
                   WrightyCommentClassifier.SessionReportPrefix + "\nnot json\n-->\n```";
        Assert.Equal(WrightyCommentKind.Discussion, Classify(body).Kind);
    }

    // --- claims ---------------------------------------------------------------------------------

    [Fact]
    public void AValidClaimIsProtocol()
    {
        var result = Classify(ClaimComment());
        Assert.Equal(WrightyCommentKind.Claim, result.Kind);
        Assert.True(result.IsProtocol);
    }

    [Fact]
    public void AClaimIsRecognisedWithoutRegardToItsAuthor()
    {
        // The strict payload is the bar for claims. That a collaborator could hand-craft one is a
        // pre-existing property of the unsigned claim protocol, documented rather than solved here.
        Assert.Equal(WrightyCommentKind.Claim,
            Classify(ClaimComment(), "outsider", NobodyAuthorized).Kind);
    }

    [Fact]
    public void AMalformedClaimPayloadIsDiscussion()
    {
        var body = "<!-- wrighty-claim:v3\n{\"version\":3,\"eventId\":\"\"}\n-->";
        Assert.Equal(WrightyCommentKind.Discussion, Classify(body).Kind);
    }

    [Fact]
    public void AClaimMarkerWithNonJsonPayloadIsDiscussion() =>
        Assert.Equal(WrightyCommentKind.Discussion,
            Classify("<!-- wrighty-claim:v3\nnot json at all\n-->").Kind);

    [Fact]
    public void ALegacyClaimMarkerIsStillRecognisedAsProtocol()
    {
        // Needed so migrating repositories do not suddenly surface old protocol comments as task
        // requirements.
        var result = Classify("<!-- wrighty-claim:v2\n{\"whatever\":true}\n-->");
        Assert.Equal(WrightyCommentKind.Claim, result.Kind);
    }

    // --- session reports ------------------------------------------------------------------------

    [Fact]
    public void AValidReportFromAnAuthorisedAuthorIsProtocol()
    {
        var result = Classify(ReportComment());

        Assert.Equal(WrightyCommentKind.SessionReport, result.Kind);
        Assert.Equal("run-abc123", result.Report!.RunId);
        Assert.Equal("report-def456", result.Report.ReportId);
    }

    [Fact]
    public void TheSameReportFromAnUnauthorisedAuthorStaysDiscussion()
    {
        // The whole spoofing case: a report marker is visible in the issue and trivially copyable,
        // so pasting one must not let anyone hide their comment from the agent.
        var result = Classify(ReportComment(), "outsider", NobodyAuthorized);

        Assert.Equal(WrightyCommentKind.Discussion, result.Kind);
        Assert.Contains("exclusion authority", result.Reason!, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"itemId":"github:owner/repo#42","runId":"run-1"}""")]
    [InlineData("""{"itemId":"github:owner/repo#42","reportId":"report-1"}""")]
    [InlineData("""{"runId":"run-1","reportId":"report-1"}""")]
    [InlineData("""{"itemId":"","runId":"run-1","reportId":"report-1"}""")]
    [InlineData("""{"itemId":"x","runId":"   ","reportId":"report-1"}""")]
    [InlineData("not json")]
    [InlineData("")]
    public void AReportMarkerMissingIdentityIsDiscussion(string payload)
    {
        var body = $"{WrightyCommentClassifier.SessionReportPrefix}\n{payload}\n-->\nbody";
        Assert.Equal(WrightyCommentKind.Discussion, Classify(body).Kind);
    }

    [Fact]
    public void AReportMarkerWithNoClosingDelimiterIsDiscussion() =>
        Assert.Equal(WrightyCommentKind.Discussion,
            Classify($"{WrightyCommentClassifier.SessionReportPrefix}\n{{\"itemId\":\"x\"}}").Kind);

    [Fact]
    public void AReportNamingAnotherItemDoesNotBelongToThisOne()
    {
        // Well formed, authorised author, wrong item — the shape a copied marker takes.
        Assert.True(WrightyCommentClassifier.TryParseSessionReport(
            ReportComment(itemId: "github:owner/repo#999"), out var marker));
        Assert.False(WrightyCommentClassifier.BelongsTo(marker, Item));
        Assert.True(WrightyCommentClassifier.BelongsTo(
            new SessionReportMarker(Item.Value, "run-1", "report-1"), Item));
    }

    [Fact]
    public void AHumanReplyBeneathAReportIsOrdinaryDiscussion()
    {
        // Quoting a report while replying to it must not inherit the report's exclusion.
        var body = "> ### Wrighty session report\n> **Observed outcome:** Needs attention\n\n" +
                   "That verification step was not actually run — please redo it.";
        Assert.Equal(WrightyCommentKind.Discussion, Classify(body).Kind);
    }

    // --- handovers ------------------------------------------------------------------------------

    [Fact]
    public void AHandoverFromAnAuthorisedAuthorIsProtocol()
    {
        var body = HandoverRenderer.Marker + "\n\n### Wrighty handover\n\nNext actions…";
        Assert.Equal(WrightyCommentKind.Handover, Classify(body).Kind);
    }

    [Fact]
    public void AHandoverMarkerFromAnUnauthorisedAuthorStaysDiscussion()
    {
        // The handover marker carries no payload, so strict parsing is impossible and the author
        // check is the only defence there is.
        var body = HandoverRenderer.Marker + "\n\nhiding this from the agent";
        var result = Classify(body, "outsider", NobodyAuthorized);

        Assert.Equal(WrightyCommentKind.Discussion, result.Kind);
        Assert.Contains("exclusion authority", result.Reason!, StringComparison.Ordinal);
    }

    // --- ordering between kinds -----------------------------------------------------------------

    [Fact]
    public void AClaimContainingReportLikeTextIsStillAClaim()
    {
        var body = ClaimComment() + "\n" + WrightyCommentClassifier.SessionReportPrefix + "\n{}\n-->";
        Assert.Equal(WrightyCommentKind.Claim, Classify(body).Kind);
    }

    [Fact]
    public void TheAuthorityCheckIsNotConsultedForOrdinaryDiscussion()
    {
        // A comment with no marker must never depend on an authorization lookup to be classified;
        // the common path stays free of remote calls.
        var consulted = false;
        WrightyCommentClassifier.Classify("ordinary text", "anyone", _ =>
        {
            consulted = true;
            return true;
        });
        Assert.False(consulted);
    }

    [Fact]
    public void ReportPayloadFieldNamesAreMatchedCaseInsensitively()
    {
        // The renderer writes camelCase; being strict about case here would reject Wrighty's own
        // reports if the serializer configuration ever drifted.
        var payload = JsonSerializer.Serialize(new { ItemId = Item.Value, RunId = "r", ReportId = "p" });
        var body = $"{WrightyCommentClassifier.SessionReportPrefix}\n{payload}\n-->";
        Assert.True(WrightyCommentClassifier.TryParseSessionReport(body, out var marker));
        Assert.Equal(Item.Value, marker.ItemId);
    }
}
