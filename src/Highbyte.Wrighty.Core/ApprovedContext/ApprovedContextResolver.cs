using Highbyte.Wrighty.GitHub;
using Highbyte.Wrighty.Models;

namespace Highbyte.Wrighty.ApprovedContext;

/// <summary>
/// Which reactions carry an include/exclude decision. The two must differ, or a single gesture
/// would mean both things at once.
/// </summary>
public sealed record DecisionPolicy(
    string IncludeReaction = ReactionKinds.ThumbsUp,
    string ExcludeReaction = ReactionKinds.ThumbsDown)
{
    public static DecisionPolicy Default { get; } = new();
}

/// <summary>
/// Turns a read conversation plus an approval into an approved context, or into the reason there
/// is not one.
///
/// Two rules run through everything here:
///
/// <b>Strictly later.</b> A decision covers a comment only when it is strictly later than that
/// comment's current revision. Equality is not coverage — the timestamps involved carry
/// whole-second precision, so a maintainer approving in the same second a comment lands is an
/// ordinary occurrence rather than a race, and it resolves against approval.
///
/// <b>Undecided blocks.</b> A comment nobody has decided is <see cref="DiscussionDecisionKind.Pending"/>
/// and stops the launch. It is never quietly omitted: dropping an unreviewed comment would narrow
/// the approved task with nobody choosing to, and the agent would never know a requirement existed.
/// </summary>
public sealed class ApprovedContextResolver(
    Func<string?, bool> isApprover,
    Func<string?, bool> canExcludeContent,
    DecisionPolicy? policy = null,
    Func<string?, bool>? isTrustedAuthor = null)
{
    private readonly Func<string?, bool> isTrustedAuthor = isTrustedAuthor ?? (_ => false);
    private readonly DecisionPolicy policy = policy ?? DecisionPolicy.Default;

    public ExecutionContextResult Resolve(
        WorkItemId id,
        GitHubConversation conversation,
        ContextApproval approval,
        ContextLimits limits,
        DateTimeOffset capturedAt)
    {
        if (!approval.IsApproved || approval.BaseApprovedAt is not { } approvedAt)
            return ExecutionContextResult.Refused(
                ExecutionContextResult.Codes.ApprovalUnavailable,
                "The context approval field is unset or could not be resolved, so no content is " +
                "approved for an unattended run.");

        var baseRevision = conversation.ToBaseRevision();
        if (!baseRevision.IsCoveredBy(approvedAt))
            // The remedy is spelled out because the tracker will not hint at it: after an edit the
            // field still reads as approved while no longer covering the current content, and the
            // Projects UI offers no way to re-select the value it already holds. A maintainer
            // looking at an approved-looking field has no reason to suspect anything is needed.
            return ExecutionContextResult.Refused(
                ExecutionContextResult.Codes.BaseNeedsReview,
                "The issue title or body changed after the context was approved, so the current " +
                "content is not covered. The approval field still reads as approved but no longer " +
                "applies. Review the current content, then change the field to needs-review and " +
                "back to approved — setting it to the value it already holds renews nothing.");

        // Wrighty's own comments are removed before anything else looks at the conversation, so a
        // claim renewal or a handover edit never counts as unreviewed discussion and never has to
        // be re-approved.
        var relevant = new List<GitHubComment>();
        foreach (var comment in conversation.Comments)
        {
            var classification = WrightyCommentClassifier.Classify(
                comment.Body, comment.Author, canExcludeContent);
            if (classification.IsProtocol) continue;
            if (string.IsNullOrWhiteSpace(comment.Body)) continue;
            relevant.Add(comment);
        }

        var cutoff = approval.BatchCommentCutoff ?? approvedAt;
        var decisions = new List<DiscussionDecision>(relevant.Count);
        foreach (var comment in relevant)
        {
            var decision = Decide(comment, cutoff);
            if (decision is null)
                return ExecutionContextResult.Refused(
                    ExecutionContextResult.Codes.DecisionAmbiguous,
                    $"Comment {comment.Url} carries conflicting decisions with the same timestamp, " +
                    "which cannot be ordered. Re-apply the intended decision.");
            decisions.Add(decision);
        }

        // Hidden first, because the remedy differs and the reader should be told the one that
        // applies: a pending comment needs a decision, a hidden one cannot be given one.
        if (RefuseUndecided(relevant, decisions) is { } undecided)
            return undecided;

        var includedIds = decisions
            .Where(d => d.Decision == DiscussionDecisionKind.Include)
            .Select(d => d.CommentId)
            .ToHashSet(StringComparer.Ordinal);
        var relevantEntries = relevant.Select(c => c.ToEntry()).ToArray();
        var includedEntries = ContextRevisionSerializer.Order(
            relevantEntries.Where(e => includedIds.Contains(e.StableId)).ToArray());

        // Every relevant comment counts toward the entry limit, because Wrighty had to retrieve and
        // classify each one; only included bodies count toward the character limit, because only
        // those reach the agent.
        var limitCheck = ContextLimitResult.Check(
            conversation.Title, conversation.Body, relevantEntries, includedEntries, limits);
        if (!limitCheck.Within)
            return ExecutionContextResult.Refused(limitCheck.Code!, limitCheck.Message!);

        var revision = ContextRevisionSerializer.Compute(
            id, conversation.Title, conversation.Body, conversation.Url,
            includedEntries, decisions, capturedAt);

        return ExecutionContextResult.Approved(new ExecutionContextSnapshot(
            id, conversation.Title, conversation.Body,
            approval, baseRevision, revision,
            includedEntries, decisions, conversation.Url));
    }

    /// <summary>
    /// Resolves one comment. Returns null when conflicting decisions share the latest timestamp and
    /// cannot be ordered — the caller turns that into a refusal rather than picking one.
    /// </summary>
    /// <summary>
    /// The refusal for anything left undecided, or null when every relevant comment is settled.
    ///
    /// Two conditions, ordered. A hidden comment is reported first because its remedy is not the
    /// same: a pending comment is waiting for someone to decide it, whereas a hidden one cannot be
    /// decided at all — hiding advances no timestamp and raises no timeline event, so Wrighty
    /// cannot place it against an approval. Reporting "needs a decision" for a comment nobody can
    /// decide would send an operator looking for a control that does not exist.
    ///
    /// A reaction is exempt: it carries its own timestamp, so where somebody has actually stated an
    /// intent for this comment there is nothing left to refuse about.
    /// </summary>
    private static ExecutionContextResult? RefuseUndecided(
        IReadOnlyList<GitHubComment> relevant,
        IReadOnlyList<DiscussionDecision> decisions)
    {
        var hidden = relevant
            .Where(comment => comment.Minimized)
            .Where(comment => !decisions.Any(d =>
                d.CommentId == comment.StableId &&
                d.Source == DiscussionDecisionSource.Reaction))
            .ToArray();
        if (hidden.Length > 0)
            return ExecutionContextResult.Refused(
                ExecutionContextResult.Codes.CommentHidden,
                hidden.Length == 1
                    ? "One comment is hidden on the tracker. Hiding is not a decision Wrighty can " +
                      "act on: GitHub records no time for it, so a hidden comment can be neither " +
                      "trusted as excluded nor quietly included. Delete it if it should not exist, " +
                      "or unhide it and let the approval decide it like any other comment."
                    : $"{hidden.Length} comments are hidden on the tracker. Hiding is not a " +
                      "decision Wrighty can act on: GitHub records no time for it, so a hidden " +
                      "comment can be neither trusted as excluded nor quietly included. Delete " +
                      "those that should not exist, and unhide the rest to let the approval decide " +
                      "them like any other comment.",
                hidden.Select(comment => comment.Url).ToArray());

        var pending = decisions
            .Where(d => d.Decision == DiscussionDecisionKind.Pending)
            .ToArray();
        if (pending.Length == 0) return null;

        return ExecutionContextResult.Refused(
            ExecutionContextResult.Codes.CommentPending,
            pending.Length == 1
                ? "One comment has no approval or exclusion decision covering its current revision."
                : $"{pending.Length} comments have no approval or exclusion decision covering " +
                  "their current revision.",
            pending
                .Select(d => relevant.First(c => c.StableId == d.CommentId).Url)
                .ToArray());
    }

    private DiscussionDecision? Decide(GitHubComment comment, DateTimeOffset batchCutoff)
    {
        DiscussionDecision? latest = null;
        var ambiguous = false;

        foreach (var reaction in comment.Reactions)
        {
            if (ToDecision(comment, reaction) is not { } candidate) continue;

            if (latest is null || reaction.CreatedAt > latest.DecidedAt)
            {
                latest = candidate;
                ambiguous = false;
                continue;
            }

            // Same instant. Two reactions agreeing is not a conflict; two disagreeing cannot be
            // ordered, and guessing would let a coin flip decide what an agent sees.
            if (reaction.CreatedAt == latest.DecidedAt && candidate.Decision != latest.Decision)
                ambiguous = true;
        }

        if (ambiguous) return null;
        if (latest is not null) return latest;

        // A configured trusted author, checked BEFORE the batch and not only as a fallback for
        // comments the batch misses.
        //
        // The order used to be load-bearing: the decision's source was part of the canonical form,
        // so a comment that counted as trusted-author and later counted as batch moved the digest
        // with no change to any content, and a session holding that content could no longer resume
        // unattended. Decision evidence left the canonical form in format 2, so nothing depends on
        // this order any more — it stays because deciding by the more specific rule first is the
        // natural reading, not because a digest hangs on it.
        if (isTrustedAuthor(comment.Author))
            return new DiscussionDecision(
                comment.StableId,
                DiscussionDecisionKind.Include,
                DiscussionDecisionSource.TrustedAuthor,
                DecidedBy: comment.Author,
                DecidedAt: comment.RevisionAt);

        // No explicit decision. The batch covers a comment whose current revision predates it —
        // which an edit undoes, because editing produces a revision the batch never saw.
        return comment.RevisionAt < batchCutoff
            ? new DiscussionDecision(
                comment.StableId,
                DiscussionDecisionKind.Include,
                DiscussionDecisionSource.Batch,
                DecidedAt: batchCutoff)
            : DiscussionDecision.Pending(comment.StableId);
    }

    /// <summary>
    /// The decision one reaction carries, or null when it carries none. Split out of
    /// <see cref="Decide"/> so that method is left expressing only how competing decisions are
    /// ordered.
    /// </summary>
    private DiscussionDecision? ToDecision(GitHubComment comment, GitHubReaction reaction)
    {
        var include = ReactionKinds.Matches(reaction.Content, policy.IncludeReaction);
        var exclude = ReactionKinds.Matches(reaction.Content, policy.ExcludeReaction);
        if (!include && !exclude) return null;

        // Authority is evaluated now rather than trusted from when the reaction was added: GitHub
        // offers no signed historical permission assertion, so current authority is the only thing
        // that can be checked. Someone who has lost access stops deciding.
        if (!isApprover(reaction.Actor)) return null;

        // A reaction added before the current revision decided an older version of the text.
        if (reaction.CreatedAt <= comment.RevisionAt) return null;

        return new DiscussionDecision(
            comment.StableId,
            include ? DiscussionDecisionKind.Include : DiscussionDecisionKind.Exclude,
            DiscussionDecisionSource.Reaction,
            reaction.Actor,
            reaction.CreatedAt,
            reaction.Id);
    }
}
