using System.Collections.Concurrent;
using Highbyte.Wrighty.Configuration;
using Highbyte.Wrighty.Models;
using Highbyte.Wrighty.Workers;

namespace Highbyte.Wrighty.ApprovedContext;

/// <summary>
/// The approved context an admitted launch resolved, so the caller can record it with the session
/// and render it into the agent's prompt.
/// </summary>
/// <param name="Comparison">
/// How this context differs from what the session being resumed was already given, or null for a
/// fresh launch, which has nothing to differ from. Phase 5 renders an additive resume from
/// <see cref="ContextComparison.NewEntryIds"/> rather than re-sending the whole snapshot.
/// </param>
public sealed record ResolvedLaunchContext(
    WorkItemId ItemId,
    ExecutionContextSnapshot Snapshot,
    DateTimeOffset ResolvedAt,
    ContextComparison? Comparison = null,
    SessionContextMetadata? Previous = null)
{
    /// <summary>
    /// What the session should record for this launch. Continuation and report state carries
    /// forward from the previous metadata, because those count against budgets that a newly
    /// supplied context does not reset.
    /// </summary>
    public SessionContextMetadata SessionContext =>
        Previous?.Supersede(Snapshot) ?? SessionContextMetadata.For(Snapshot);
}

/// <summary>
/// Refuses a launch whose approved context cannot be established, and — at the last stage — one
/// whose context moved while the workspace was being prepared.
///
/// The two stages ask different questions, and that is the point of registering at both:
///
/// <list type="bullet">
/// <item><b>Post-claim</b> asks whether there is an approved context at all. It is the expensive
/// read, and it happens after the claim so the answer cannot be raced by another worker, but
/// before a workspace exists so a refusal costs nothing to unwind.</item>
/// <item><b>Pre-spawn</b> asks whether it is still the same one. Workspace creation, branch
/// checkout and session metadata all take time, and a maintainer editing the issue during that
/// window would otherwise hand the agent content nobody approved. It compares digests rather than
/// re-deciding, so agreement is cheap to establish and disagreement is unambiguous.</item>
/// </list>
///
/// There remains an unavoidable interval between the final read and the process actually starting.
/// This narrows that window; it does not claim GitHub and a local spawn are one transaction.
///
/// A resume, recovery or retry re-enters an already-claimed item and therefore never runs the
/// post-claim stage at all. Its baseline is the context recorded with the session it is re-entering,
/// and the question it asks is different in kind: not "is this the same revision" but "may this
/// session be given what has changed since". A purely additive change qualifies; an edit to
/// something the session already saw does not, because a resumed model cannot unsee it.
/// </summary>
public sealed class ExecutionContextLaunchCheck(
    Func<TrackerConfig, IExecutionContextProvider?> providers,
    Func<TrackerConfig, ContextLimits>? limits = null)
    : ILaunchPreflightCheck, ILaunchSessionContextSource
{
    // Keyed by item so a worker processing several items concurrently cannot compare one item's
    // pre-spawn read against another's post-claim resolution. Entries are replaced on every
    // post-claim evaluation and read once at pre-spawn.
    private readonly ConcurrentDictionary<string, ResolvedLaunchContext> resolved = new();

    public string Name => "approved-context";

    /// <summary>
    /// Runs after the claim and again immediately before the spawn.
    ///
    /// Not at pre-claim: assembling a context pages an entire conversation, and doing that for
    /// every candidate the selection scan considers would spend a full read on items that are
    /// about to be rejected for far cheaper reasons. The claim is the point at which one item has
    /// been chosen and the cost is justified.
    /// </summary>
    public bool AppliesTo(LaunchStage stage, LaunchKind kind) =>
        stage is LaunchStage.PostClaim or LaunchStage.PreSpawn;

    /// <summary>
    /// The context resolved for an admitted launch, or null when none was resolved. The caller
    /// takes it after the pre-spawn stage admits, and it is removed as it is taken so a later
    /// launch of the same item cannot read a stale one.
    /// </summary>
    public ResolvedLaunchContext? TakeResolved(WorkItemId id) =>
        resolved.TryRemove(id.Value, out var value) ? value : null;

    /// <summary>
    /// The same single take as <see cref="TakeResolved"/>, reduced to what a session records. One
    /// launch takes once: a caller needing both the metadata and the snapshot takes the resolved
    /// context and reads <see cref="ResolvedLaunchContext.SessionContext"/> from it.
    /// </summary>
    public SessionContextMetadata? TakeSessionContext(WorkItemId id) =>
        TakeResolved(id)?.SessionContext;

    public async ValueTask<LaunchPreflightDecision> EvaluateAsync(
        LaunchPreflightRequest request,
        CancellationToken cancellationToken)
    {
        var provider = providers(request.Config);
        if (provider is null)
            // A backend with no approved-context capability cannot be gated on one. This is not a
            // refusal: it is the honest answer for a tracker that has no approval surface, and the
            // launch is decided by the other checks.
            return LaunchPreflightDecision.Admit();

        var contextLimits = limits?.Invoke(request.Config) ?? ContextLimits.Default;
        var purpose = request.Stage == LaunchStage.PostClaim
            ? ContextReadPurpose.PreClaim
            : ContextReadPurpose.PreLaunch;

        var result = await provider.GetAsync(
            request.Config, request.Detail.Id, purpose, contextLimits, cancellationToken);

        if (result.Snapshot is not { } snapshot)
            return LaunchPreflightDecision.Refuse(
                result.Code ?? ExecutionContextResult.Codes.ApprovalUnavailable,
                result.Message ?? "The approved context could not be established.",
                result.PendingUrls);

        if (request.Stage == LaunchStage.PostClaim)
        {
            resolved[request.Detail.Id.Value] = new ResolvedLaunchContext(
                request.Detail.Id, snapshot, snapshot.Revision.CapturedAt,
                Previous: request.Session?.Context);
            return LaunchPreflightDecision.Admit([$"context {snapshot.Revision.ShortDigest}"]);
        }

        // Pre-spawn, and which question this is depends on the launch. A fresh launch resolved a
        // context at post-claim and asks only whether it still holds. A resume, recovery or retry
        // never runs post-claim at all — it re-enters a claimed item — so its baseline is what the
        // session it is re-entering was recorded as having been given, and every one of them
        // re-enters a real vendor session rather than starting a new one.
        if (resolved.TryGetValue(request.Detail.Id.Value, out var thisLaunch))
            return ConfirmUnchanged(request, thisLaunch, snapshot);

        return request.Kind == LaunchKind.Fresh
            // A fresh launch reaching the spawn with nothing recorded means the post-claim stage
            // did not run or its result was lost. Nothing about the session is in question; what is
            // missing is this launch's own validation.
            ? LaunchPreflightDecision.Refuse(
                ExecutionContextResult.Codes.RevisionChanged,
                "No approved context was recorded for this launch, so the content about to be " +
                "given to the agent cannot be confirmed as the content that was approved.")
            : ClassifyAgainstSession(request, snapshot);
    }

    /// <summary>
    /// The fresh-launch pre-spawn question: is this the same revision the post-claim stage
    /// admitted? Nothing but equality will do — the workspace was prepared for that content, and a
    /// difference means the agent would receive something this launch never validated.
    /// </summary>
    private LaunchPreflightDecision ConfirmUnchanged(
        LaunchPreflightRequest request,
        ResolvedLaunchContext thisLaunch,
        ExecutionContextSnapshot snapshot)
    {
        if (!thisLaunch.Snapshot.Revision.Matches(snapshot.Revision))
        {
            resolved.TryRemove(request.Detail.Id.Value, out _);
            return LaunchPreflightDecision.Refuse(
                ExecutionContextResult.Codes.RevisionChanged,
                "The approved context changed while the workspace was being prepared. The agent " +
                "was not started; review the current content and approve it again.",
                [$"was {thisLaunch.Snapshot.Revision.ShortDigest}",
                 $"now {snapshot.Revision.ShortDigest}"]);
        }

        // Refreshed rather than left alone: the pre-spawn read is the newer observation, and it is
        // the one the session should record as what the agent was given.
        resolved[request.Detail.Id.Value] = thisLaunch with
        {
            Snapshot = snapshot,
            ResolvedAt = snapshot.Revision.CapturedAt
        };
        return LaunchPreflightDecision.Admit([$"context {snapshot.Revision.ShortDigest} unchanged"]);
    }

    /// <summary>
    /// The resume pre-spawn question: may this session be given what has changed since it was last
    /// supplied? Equality is not required here — new approved comments are exactly what a resume
    /// usually exists to deliver — but anything that rewrites what the session already saw is
    /// refused, because a resumed model cannot unsee the old version.
    /// </summary>
    private LaunchPreflightDecision ClassifyAgainstSession(
        LaunchPreflightRequest request,
        ExecutionContextSnapshot snapshot)
    {
        var recorded = request.Session?.Context;
        if (recorded?.Manifest is null)
            // Not a change: an absence. A session claimed before approved-context support, or by a
            // host whose local record is gone, cannot say what its agent holds — and a resume that
            // guessed would be handing content to a session on the assumption it already had it.
            return LaunchPreflightDecision.Refuse(
                ExecutionContextResult.Codes.ManifestUnavailable,
                "This session has no recorded approved context, so what its agent was already " +
                "given cannot be established. Start a fresh session for this item rather than " +
                "resuming one whose contents are unknown.");

        var comparison = ContextChangeClassifier.Compare(recorded.Manifest, snapshot);
        if (!comparison.AllowsUnattendedResume)
            return LaunchPreflightDecision.Refuse(
                ExecutionContextResult.Codes.ResumeBlocked,
                comparison.Reason + " A session already holding the earlier version cannot be " +
                "given a correction, so it was not resumed. Review the current content and start " +
                "a fresh session.",
                [$"change {comparison.Kind}",
                 $"was {ContextRevision.Shorten(recorded.Manifest.Digest)}",
                 $"now {snapshot.Revision.ShortDigest}"]);

        resolved[request.Detail.Id.Value] = new ResolvedLaunchContext(
            request.Detail.Id, snapshot, snapshot.Revision.CapturedAt, comparison, recorded);
        return LaunchPreflightDecision.Admit(
            [$"context {snapshot.Revision.ShortDigest} {comparison.Kind}"]);
    }
}
