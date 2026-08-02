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
    Func<TrackerConfig, ContextLimits>? limits = null,
    Func<DateTimeOffset>? now = null)
    : ILaunchPreflightCheck, ILaunchSessionContextSource
{
    // Keyed by item so a worker processing several items concurrently cannot compare one item's
    // pre-spawn read against another's post-claim resolution. Entries are replaced on every
    // post-claim evaluation and read once at pre-spawn.
    private readonly ConcurrentDictionary<string, ResolvedLaunchContext> resolved = new();

    // The advisory memory behind the pre-claim stage: the last verdict per item, keyed to the
    // item's observed change stamp. It exists so a worker polling every few seconds does not pay
    // a full conversation read per poll for an item already known to be refused — and, more
    // importantly, so the selection scan can pass over that item without claiming it, which is
    // what moved its status back and forth on every poll before this stage existed.
    private readonly ConcurrentDictionary<string, AdvisoryVerdict> advisory = new();

    private readonly Func<DateTimeOffset> clock = now ?? (() => DateTimeOffset.UtcNow);

    /// <summary>
    /// How long an advisory verdict may be reused when the item's change stamp has not moved.
    /// Not every decision input moves the stamp — a Project-field re-approval changes no issue
    /// timestamp — so an unchanged stamp is a strong hint, never proof, and the verdict expires
    /// on time as well. The post-claim stage re-reads regardless, so this bounds staleness of
    /// *selection*, not of what an agent may be given.
    /// </summary>
    internal static readonly TimeSpan AdvisoryLifetime = TimeSpan.FromSeconds(60);

    private sealed record AdvisoryVerdict(
        DateTimeOffset? Stamp,
        DateTimeOffset ObservedAt,
        LaunchPreflightDecision Decision);

    public string Name => "approved-context";

    /// <summary>
    /// Runs before the claim for fresh selection, after the claim, and again immediately before
    /// the spawn.
    ///
    /// The pre-claim stage is advisory and cached: it lets the selection scan pass over an item
    /// whose context is already known to be refused instead of claiming it, moving its status,
    /// and handing it straight back — which read as visible churn on the tracker and starved
    /// every candidate ranked behind the refused item. The verdict cache is what makes this
    /// affordable; without it, pre-claim evaluation would spend a full conversation read on
    /// every candidate on every poll. Only fresh selection runs it: a resume re-enters an
    /// already-claimed item and never selects.
    /// </summary>
    public bool AppliesTo(LaunchStage stage, LaunchKind kind) =>
        stage is LaunchStage.PostClaim or LaunchStage.PreSpawn ||
        (stage is LaunchStage.PreClaim && kind is LaunchKind.Fresh);

    /// <summary>
    /// The context resolved for an admitted launch, or null when none was resolved. The caller
    /// takes it after the pre-spawn stage admits, and it is removed as it is taken so a later
    /// launch of the same item cannot read a stale one.
    /// </summary>
    public ResolvedLaunchContext? TakeResolved(WorkItemId id) =>
        resolved.TryRemove(id.Value, out var value) ? value : null;

    /// <summary>The seam's name for <see cref="TakeResolved"/>; one launch takes once.</summary>
    public ResolvedLaunchContext? TakeResolvedContext(WorkItemId id) => TakeResolved(id);

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

        if (request.Stage == LaunchStage.PreClaim)
            return await EvaluateAdvisoryAsync(request, provider, contextLimits, cancellationToken);

        var purpose = request.Stage == LaunchStage.PostClaim
            ? ContextReadPurpose.PreClaim
            : ContextReadPurpose.PreLaunch;

        var result = await provider.GetAsync(
            request.Config, request.Detail.Id, purpose, contextLimits, cancellationToken);

        if (result.Snapshot is not { } snapshot)
        {
            var refusal = LaunchPreflightDecision.Refuse(
                result.Code ?? ExecutionContextResult.Codes.ApprovalUnavailable,
                result.Message ?? "The approved context could not be established.",
                result.PendingUrls);
            // The authoritative read just paid for an answer the advisory stage would otherwise
            // re-derive. Recording it here covers the race where a comment lands between the
            // advisory admit and the claim: the unwind of this refusal is the one status flip
            // that race costs, and this entry is what keeps it from repeating.
            if (request.Stage == LaunchStage.PostClaim)
                advisory[request.Detail.Id.Value] = new AdvisoryVerdict(
                    request.Detail.UpdatedAt, clock(), refusal);
            return refusal;
        }

        if (request.Stage == LaunchStage.PostClaim)
        {
            advisory.TryRemove(request.Detail.Id.Value, out _);
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
    /// The pre-claim question: is this candidate already known to be refusable? A cached verdict
    /// is reused while the item's change stamp holds and the verdict is young enough; otherwise
    /// the context is read once and the verdict remembered. Admissions are cached too — an item
    /// whose claim then fails for an unrelated reason (held elsewhere, fenced) would otherwise
    /// pay a fresh read on every retry of the same unchanged content.
    /// </summary>
    private async ValueTask<LaunchPreflightDecision> EvaluateAdvisoryAsync(
        LaunchPreflightRequest request,
        IExecutionContextProvider provider,
        ContextLimits contextLimits,
        CancellationToken cancellationToken)
    {
        var stamp = request.Detail.UpdatedAt;
        var current = clock();
        if (advisory.TryGetValue(request.Detail.Id.Value, out var cached) &&
            cached.Stamp == stamp &&
            current - cached.ObservedAt < AdvisoryLifetime)
            return cached.Decision;

        var result = await provider.GetAsync(
            request.Config, request.Detail.Id, ContextReadPurpose.PreClaim, contextLimits,
            cancellationToken);
        var decision = result.Snapshot is null
            ? LaunchPreflightDecision.Refuse(
                result.Code ?? ExecutionContextResult.Codes.ApprovalUnavailable,
                result.Message ?? "The approved context could not be established.",
                result.PendingUrls)
            : LaunchPreflightDecision.Admit();
        advisory[request.Detail.Id.Value] = new AdvisoryVerdict(stamp, current, decision);
        return decision;
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

        // The same absence as above, reached the other way: a manifest exists but was written under
        // a canonical form this build cannot compare against, so its digest establishes nothing.
        // An operator override does not apply — an operator can accept a change they have read, but
        // nobody can review a difference that cannot be computed. This is the path every session
        // paused across a FormatVersion bump takes, and it must land on "start a fresh session"
        // rather than on the superseded-resume notice, which asserts a reviewed change.
        if (comparison.Kind == ContextChangeKind.ManifestUnavailable)
            return LaunchPreflightDecision.Refuse(
                ExecutionContextResult.Codes.ManifestUnavailable,
                comparison.Reason + " What its agent was already given cannot be established, so " +
                "the session cannot be resumed. Start a fresh session for this item.");

        var evidence = new[]
        {
            $"change {comparison.Kind}",
            $"was {ContextRevision.Shorten(recorded.Manifest.Digest)}",
            $"now {snapshot.Revision.ShortDigest}"
        };

        // The rule is about unattended resume, and only that. An unattended worker picking up a
        // session whose item changed underneath it is what it exists to stop: nobody decided that
        // the agent should carry on with superseded content, and the agent cannot unsee what it
        // read. An operator naming this item has decided — clarifying a paused session by editing
        // it and handing it back is an ordinary, supported way to work, and it is the *only* way on
        // a backend with no discussion to append to.
        //
        // The launch is still reported as proceeding despite the change, so acting on someone's
        // judgement never looks the same as nothing having been wrong.
        if (!comparison.AllowsUnattendedResume && !request.OperatorRequested)
            return LaunchPreflightDecision.Refuse(
                ExecutionContextResult.Codes.ResumeBlocked,
                comparison.Reason + " A session already holding the earlier version cannot be " +
                "given a correction, so it was not resumed. Review the current content and resume " +
                "it yourself, or start a fresh session.",
                evidence);

        resolved[request.Detail.Id.Value] = new ResolvedLaunchContext(
            request.Detail.Id, snapshot, snapshot.Revision.CapturedAt, comparison, recorded);

        return comparison.AllowsUnattendedResume
            ? LaunchPreflightDecision.Admit(
                [$"context {snapshot.Revision.ShortDigest} {comparison.Kind}"])
            : LaunchPreflightDecision.AdmitWithNotice(
                ExecutionContextResult.Codes.ResumeSuperseded,
                comparison.Reason + " Resumed anyway because this run was requested for this item " +
                "by an operator; an unattended worker would have refused it.",
                evidence);
    }
}
