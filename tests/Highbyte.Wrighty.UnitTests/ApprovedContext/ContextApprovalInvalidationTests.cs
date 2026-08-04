using Highbyte.Wrighty.ApprovedContext;
using Highbyte.Wrighty.Errors;

namespace Highbyte.Wrighty.UnitTests.ApprovedContext;

public class ContextApprovalInvalidationTests
{
    private static readonly ContextApproval CurrentApproval = new(
        ContextApprovalSource.ProjectField,
        new DateTimeOffset(2026, 8, 4, 12, 0, 0, TimeSpan.Zero),
        new DateTimeOffset(2026, 8, 4, 12, 0, 0, TimeSpan.Zero));

    [Theory]
    [InlineData(ExecutionContextResult.Codes.BaseNeedsReview)]
    [InlineData(ExecutionContextResult.Codes.ApprovalUnavailable)]
    public void A_stale_or_unset_base_is_reset(string code)
    {
        var current = ExecutionContextResult.Refused(code, "Needs review.");

        Assert.Equal(
            ContextApprovalInvalidationDisposition.ResetToNeedsReview,
            ContextApprovalInvalidation.Decide(current));
    }

    [Fact]
    public void A_newer_base_approval_is_preserved_even_when_a_comment_is_pending()
    {
        var current = ExecutionContextResult.Refused(
            ExecutionContextResult.Codes.CommentPending,
            "A comment needs a decision.",
            diagnostics: new ExecutionContextDiagnostics(CurrentApproval, PendingCount: 1));

        Assert.Equal(
            ContextApprovalInvalidationDisposition.PreservedNewerApproval,
            ContextApprovalInvalidation.Decide(current));
    }

    [Fact]
    public void An_incomplete_read_fails_instead_of_clobbering_a_possible_newer_approval()
    {
        var current = ExecutionContextResult.Refused(
            ExecutionContextResult.Codes.ReadFailed,
            "The conversation was incomplete.");

        var error = Assert.Throws<TrackerException>(
            () => ContextApprovalInvalidation.Decide(current));

        Assert.Equal(ContextApprovalInvalidation.UnsafeCode, error.Code);
    }
}
