using System.Collections.Concurrent;
using Highbyte.Wrighty.Configuration;
using Highbyte.Wrighty.Models;
using Highbyte.Wrighty.Workers;

namespace Highbyte.Wrighty.ApprovedContext;

/// <summary>
/// The approved context an admitted launch resolved, so the caller can record it with the session
/// and render it into the agent's prompt.
/// </summary>
public sealed record ResolvedLaunchContext(
    WorkItemId ItemId,
    ExecutionContextSnapshot Snapshot,
    DateTimeOffset ResolvedAt);

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
/// </summary>
public sealed class ExecutionContextLaunchCheck(
    Func<TrackerConfig, IExecutionContextProvider?> providers,
    Func<TrackerConfig, ContextLimits>? limits = null) : ILaunchPreflightCheck
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
            resolved[request.Detail.Id.Value] =
                new ResolvedLaunchContext(request.Detail.Id, snapshot, snapshot.Revision.CapturedAt);
            return LaunchPreflightDecision.Admit([$"context {snapshot.Revision.ShortDigest}"]);
        }

        // Pre-spawn. Without a recorded post-claim revision there is nothing to compare against,
        // and admitting would mean starting an agent on a context this launch never validated.
        if (!resolved.TryGetValue(request.Detail.Id.Value, out var previous))
            return LaunchPreflightDecision.Refuse(
                ExecutionContextResult.Codes.RevisionChanged,
                "No approved context was recorded for this launch, so the content about to be " +
                "given to the agent cannot be confirmed as the content that was approved.");

        if (!previous.Snapshot.Revision.Matches(snapshot.Revision))
        {
            resolved.TryRemove(request.Detail.Id.Value, out _);
            return LaunchPreflightDecision.Refuse(
                ExecutionContextResult.Codes.RevisionChanged,
                "The approved context changed while the workspace was being prepared. The agent " +
                "was not started; review the current content and approve it again.",
                [$"was {previous.Snapshot.Revision.ShortDigest}",
                 $"now {snapshot.Revision.ShortDigest}"]);
        }

        // Refreshed rather than left alone: the pre-spawn read is the newer observation, and it is
        // the one the session should record as what the agent was given.
        resolved[request.Detail.Id.Value] =
            new ResolvedLaunchContext(request.Detail.Id, snapshot, snapshot.Revision.CapturedAt);
        return LaunchPreflightDecision.Admit([$"context {snapshot.Revision.ShortDigest} unchanged"]);
    }
}
