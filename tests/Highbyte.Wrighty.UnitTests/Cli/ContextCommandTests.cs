using System.Text.Json;
using Highbyte.Wrighty.ApprovedContext;
using Highbyte.Wrighty.Configuration;
using Highbyte.Wrighty.Models;

namespace Highbyte.Wrighty.UnitTests.Cli;

/// <summary>
/// The diagnostic surface. Its defining property is what it does not print: this runs on a terminal
/// and into logs, and the approved content is exactly what must not appear there.
/// </summary>
public class ContextCommandTests
{
    private static readonly WorkItemId Id = new("local:1");
    private static readonly DateTimeOffset Now = new(2026, 7, 26, 12, 0, 0, TimeSpan.Zero);

    private static ExecutionContextSnapshot Snapshot(
        string body = "The worker should retry once.",
        params (string Id, string Body, DiscussionDecisionKind Kind)[] entries)
    {
        var included = entries
            .Where(e => e.Kind == DiscussionDecisionKind.Include)
            .Select(e => new DiscussionEntry(e.Id, "octocat", Now, e.Body))
            .ToArray();
        var decisions = entries
            .Select(e => new DiscussionDecision(e.Id, e.Kind, DiscussionDecisionSource.Batch))
            .ToArray();
        return new ExecutionContextSnapshot(Id, "Add retry handling", body,
            new ContextApproval(ContextApprovalSource.ProjectField, Now, Now),
            new BaseContentRevision("t", "b"),
            ContextRevisionSerializer.Compute(Id, "Add retry handling", body, null, included, decisions, Now),
            included, decisions);
    }

    private static async Task<string> Render(ExecutionContextResult result, bool json = false)
    {
        var output = new StringWriter();
        var writer = new Highbyte.Wrighty.Cli.Output.OutputWriter(output, new StringWriter());
        await writer.WriteApprovedContextAsync(Id, result, ContextLimits.Default, json);
        return output.ToString();
    }

    [Fact]
    public async Task AnApprovedContextReportsItsApprovalRevisionAndCounts()
    {
        var text = await Render(ExecutionContextResult.Approved(Snapshot(entries:
        [
            ("c1", "included text", DiscussionDecisionKind.Include),
            ("c2", "excluded text", DiscussionDecisionKind.Exclude)
        ])));

        Assert.Contains("Approved: yes", text, StringComparison.Ordinal);
        Assert.Contains("Base approved at:", text, StringComparison.Ordinal);
        Assert.Contains("Batch comment cutoff:", text, StringComparison.Ordinal);
        Assert.Contains("sha256:", text, StringComparison.Ordinal);
        Assert.Contains("1 included, 1 excluded, 0 pending", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARefusalReportsItsCodeAndTheUndecidedComments()
    {
        var text = await Render(ExecutionContextResult.Refused(
            ExecutionContextResult.Codes.CommentPending,
            "One comment has no approval or exclusion decision covering its current revision.",
            ["https://github.com/owner/repo/issues/42#issuecomment-9"],
            new ExecutionContextDiagnostics(
                new ContextApproval(ContextApprovalSource.ProjectField, Now, Now),
                IncludedCount: 2,
                ExcludedCount: 1,
                PendingCount: 1)));

        Assert.Contains("Approved: no (CONTEXT_COMMENT_PENDING)", text, StringComparison.Ordinal);
        Assert.Contains("Base approved at:", text, StringComparison.Ordinal);
        Assert.Contains("2 included, 1 excluded, 1 pending", text, StringComparison.Ordinal);
        // Named by URL so a maintainer can go and decide them.
        Assert.Contains("issuecomment-9", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoCommentBodyIsEverPrinted()
    {
        const string secret = "correct-horse-battery-staple";
        var text = await Render(ExecutionContextResult.Approved(
            Snapshot(body: secret, entries: [("c1", secret, DiscussionDecisionKind.Include)])));

        Assert.DoesNotContain(secret, text, StringComparison.Ordinal);
        Assert.Contains("1 included", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoCommentBodyIsEverPrintedInJsonEither()
    {
        const string secret = "correct-horse-battery-staple";
        var text = await Render(ExecutionContextResult.Approved(
            Snapshot(body: secret, entries: [("c1", secret, DiscussionDecisionKind.Include)])), json: true);

        Assert.DoesNotContain(secret, text, StringComparison.Ordinal);
        Assert.Contains("\"digest\"", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheJsonShapeCarriesTheApprovalRevisionAndLimits()
    {
        // Parsed rather than substring-matched, so the assertion does not depend on how the writer
        // happens to indent.
        var result = JsonDocument
            .Parse(await Render(ExecutionContextResult.Approved(Snapshot()), json: true))
            .RootElement.GetProperty("result");

        Assert.True(result.GetProperty("approved").GetBoolean());
        Assert.NotEqual(JsonValueKind.Null, result.GetProperty("approval").GetProperty("batchCommentCutoff").ValueKind);
        Assert.StartsWith("sha256:", result.GetProperty("revision").GetProperty("digest").GetString(),
            StringComparison.Ordinal);
        Assert.Equal(100000, result.GetProperty("limits").GetProperty("maxTotalCharacters").GetInt32());
    }

    [Fact]
    public async Task ARefusalJsonCarriesNoSnapshotSections()
    {
        var result = JsonDocument
            .Parse(await Render(ExecutionContextResult.Refused(
                ExecutionContextResult.Codes.BaseNeedsReview, "changed"), json: true))
            .RootElement.GetProperty("result");

        Assert.False(result.GetProperty("approved").GetBoolean());
        Assert.Equal("CONTEXT_BASE_NEEDS_REVIEW", result.GetProperty("code").GetString());
        // The writer omits nulls by convention, so the snapshot sections are absent rather than
        // present-and-null. Either way a consumer cannot read a revision that was never assembled.
        Assert.False(result.TryGetProperty("revision", out _));
        Assert.False(result.TryGetProperty("discussion", out _));
        Assert.False(result.TryGetProperty("approval", out _));
    }
}
