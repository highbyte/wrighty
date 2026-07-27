using Highbyte.Wrighty.Backends;
using Highbyte.Wrighty.Configuration;
using Highbyte.Wrighty.Models;
using Highbyte.Wrighty.Time;

namespace Highbyte.Wrighty.ApprovedContext;

/// <summary>
/// The approved context for a backend that has no discussion surface.
///
/// It returns the item's title and body with an **empty** discussion, never a fabricated one. That
/// is an honest capability difference rather than a gap to paper over: a store with no comments has
/// no comments to approve, and inventing entries would put text in an agent prompt that no
/// maintainer ever wrote.
///
/// There is likewise no separate approval gesture here. The store is machine-local and edited by
/// the operator directly, so the item's own content is the approved content — recorded as
/// <see cref="ContextApprovalSource.BackendLocal"/> so a reader can tell this apart from a
/// maintainer having approved a revision on a tracker.
/// </summary>
public sealed class LocalExecutionContextProvider(
    IWorkItemContentReader items,
    IClock? clock = null) : IExecutionContextProvider
{
    private readonly IClock clock = clock ?? new SystemClock();

    public async Task<ExecutionContextResult> GetAsync(
        TrackerConfig config,
        WorkItemId id,
        ContextReadPurpose purpose,
        ContextLimits limits,
        CancellationToken cancellationToken)
    {
        var detail = await items.GetAsync(config, id, cancellationToken);
        if (detail is null)
            return ExecutionContextResult.Refused(
                ExecutionContextResult.Codes.ReadFailed,
                $"Work item '{id}' could not be read.");

        // Limits still apply. A local item can carry an oversized body just as an issue can, and
        // failing here is the same refusal an operator would get from any other backend.
        var limitCheck = ContextLimitResult.Check(
            detail.Title, detail.Body,
            ExecutionContextSnapshot.NoDiscussion,
            ExecutionContextSnapshot.NoDiscussion,
            limits);
        if (!limitCheck.Within)
            return ExecutionContextResult.Refused(limitCheck.Code!, limitCheck.Message!);

        var capturedAt = clock.UtcNow;
        var approval = new ContextApproval(
            ContextApprovalSource.BackendLocal,
            BaseApprovedAt: capturedAt,
            BatchCommentCutoff: capturedAt);

        // No edit history to bind to, so the hashes are the whole of the base revision evidence.
        // A local edit changes them, which changes the digest, which blocks an unattended resume
        // exactly as an edited issue body would.
        var baseRevision = new BaseContentRevision(
            ContextRevisionSerializer.HashContent(detail.Title),
            ContextRevisionSerializer.HashContent(detail.Body));

        var revision = ContextRevisionSerializer.Compute(
            detail.Id, detail.Title, detail.Body, detail.Url,
            ExecutionContextSnapshot.NoDiscussion,
            ExecutionContextSnapshot.NoDecisions,
            capturedAt);

        return ExecutionContextResult.Approved(new ExecutionContextSnapshot(
            detail.Id, detail.Title, detail.Body,
            approval, baseRevision, revision,
            ExecutionContextSnapshot.NoDiscussion,
            ExecutionContextSnapshot.NoDecisions,
            detail.Url));
    }
}
