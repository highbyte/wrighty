using Highbyte.Wrighty.ApprovedContext;
using Highbyte.Wrighty.Models;

namespace Highbyte.Wrighty.UnitTests.ApprovedContext;

/// <summary>
/// Serving a context an agent has lost. The value of this path is entirely in what it will not
/// serve: an agent that can ask for any revision has discovery, which is what the approval gate
/// exists to prevent.
/// </summary>
public class PinnedContextRetrievalTests
{
    private static readonly WorkItemId Id = new("github:owner/repo#42");
    private static readonly DateTimeOffset Now = new(2026, 7, 28, 12, 0, 0, TimeSpan.Zero);

    private static ExecutionContextResult Current(string body = "Approved requirement.")
    {
        var decisions = Array.Empty<DiscussionDecision>();
        return ExecutionContextResult.Approved(new ExecutionContextSnapshot(
            Id, "Title", body,
            new ContextApproval(ContextApprovalSource.ProjectField, Now, Now),
            new BaseContentRevision("t", "b"),
            ContextRevisionSerializer.Compute(Id, "Title", body, null, [], decisions, Now),
            [], decisions));
    }

    [Fact]
    public void TheRevisionTheRunWasLaunchedWithIsServed()
    {
        var current = Current();

        var served = PinnedContextRetrieval.Serve(current, current.Snapshot!.Revision.Digest);

        Assert.True(served.Served);
        Assert.Equal("Approved requirement.", served.Snapshot!.Body);
    }

    [Fact]
    public void AContextThatMovedSinceTheRunStartedIsNotServed()
    {
        // The agent holds a superseded set of requirements. Handing it the new ones would have it
        // continue against something nobody approved for this session.
        var pinned = Current().Snapshot!.Revision.Digest;

        var served = PinnedContextRetrieval.Serve(Current("Different requirement now."), pinned);

        Assert.False(served.Served);
        Assert.Equal(ExecutionContextResult.Codes.RevisionChanged, served.RefusalCode);
        Assert.Contains("Stop and report", served.RefusalMessage!, StringComparison.Ordinal);
        // The refusal must not leak the content it declined to serve.
        Assert.DoesNotContain("Different requirement", served.RefusalMessage!, StringComparison.Ordinal);
    }

    [Fact]
    public void AnArbitraryRevisionCannotBeRequested()
    {
        // The discovery this design prohibits would look exactly like this: an agent naming a
        // revision of its choosing and being given whatever matches.
        var served = PinnedContextRetrieval.Serve(Current(), "sha256:" + new string('0', 64));

        Assert.False(served.Served);
        Assert.Null(served.Snapshot);
    }

    [Fact]
    public void ARefusedContextIsReportedRatherThanServedAsEmpty()
    {
        var served = PinnedContextRetrieval.Serve(
            ExecutionContextResult.Refused(
                ExecutionContextResult.Codes.CommentPending, "One comment has no decision."),
            "sha256:whatever");

        Assert.False(served.Served);
        Assert.Equal(ExecutionContextResult.Codes.CommentPending, served.RefusalCode);
        Assert.Contains("no decision", served.RefusalMessage!, StringComparison.Ordinal);
    }

    [Fact]
    public void MatchingIsExactRatherThanByPrefix()
    {
        // A short digest is what an operator sees in logs and prompts. Accepting one here would let
        // a truncated value match a revision it only resembles.
        var current = Current();

        var served = PinnedContextRetrieval.Serve(current, current.Snapshot!.Revision.ShortDigest);

        Assert.False(served.Served);
    }
}
