using Highbyte.Wrighty.ApprovedContext;
using Highbyte.Wrighty.GitHub;
using Highbyte.Wrighty.Models;

namespace Highbyte.Wrighty.UnitTests.ApprovedContext;

/// <summary>
/// Resolving a conversation into an approved context. The rules that matter are the conservative
/// ones: a decision must be strictly later than the revision it covers, and an undecided comment
/// blocks rather than being quietly dropped.
/// </summary>
public class ApprovedContextResolverTests
{
    private static readonly WorkItemId Item = new("github:owner/repo#42");
    private static readonly DateTimeOffset Created = new(2026, 7, 26, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Cutoff = new(2026, 7, 26, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Captured = new(2026, 7, 26, 13, 0, 0, TimeSpan.Zero);

    private static bool IsMaintainer(string? actor) => actor == "maintainer";
    private static bool Nobody(string? actor) => false;

    private static GitHubReaction Reaction(
        string content, string actor = "maintainer", DateTimeOffset? at = null, string id = "R1") =>
        new(id, actor, content, at ?? Created.AddMinutes(30));

    private static GitHubComment Comment(
        string id = "c1",
        string body = "Please also handle the empty case.",
        string author = "octocat",
        DateTimeOffset? createdAt = null,
        DateTimeOffset? editedAt = null,
        bool minimized = false,
        params GitHubReaction[] reactions) =>
        new(id, author, "MEMBER", createdAt ?? Created, editedAt,
            $"https://github.com/owner/repo/issues/42#issuecomment-{id}", body,
            minimized, minimized ? "outdated" : null, reactions);

    private static GitHubConversation Conversation(
        string title = "Add retry handling",
        string body = "The worker should retry once.",
        DateTimeOffset? bodyEditedAt = null,
        DateTimeOffset? titleRenamedAt = null,
        params GitHubComment[] comments) =>
        new(title, body, "https://github.com/owner/repo/issues/42",
            Created.AddHours(-1), bodyEditedAt, bodyEditedAt is null ? 0 : 1,
            titleRenamedAt, titleRenamedAt is null ? 0 : 1, comments);

    private static ContextApproval Approved(DateTimeOffset? at = null) =>
        new(ContextApprovalSource.ProjectField, at ?? Cutoff, at ?? Cutoff);

    private static ExecutionContextResult Resolve(
        GitHubConversation conversation,
        ContextApproval? approval = null,
        Func<string?, bool>? approver = null,
        ContextLimits? limits = null,
        DecisionPolicy? policy = null,
        Func<string?, bool>? canExclude = null) =>
        new ApprovedContextResolver(approver ?? IsMaintainer, canExclude ?? IsMaintainer, policy)
            .Resolve(Item, conversation, approval ?? Approved(), limits ?? ContextLimits.Default, Captured);

    // --- base approval --------------------------------------------------------------------------

    [Fact]
    public void AnUnsetApprovalRefuses()
    {
        var result = Resolve(Conversation(), ContextApproval.NotApproved);

        Assert.False(result.IsApproved);
        Assert.Equal(ExecutionContextResult.Codes.ApprovalUnavailable, result.Code);
    }

    [Fact]
    public void ABodyEditAfterApprovalRefuses()
    {
        var result = Resolve(Conversation(bodyEditedAt: Cutoff.AddMinutes(5)));

        Assert.False(result.IsApproved);
        Assert.Equal(ExecutionContextResult.Codes.BaseNeedsReview, result.Code);
    }

    [Fact]
    public void ATitleRenameAfterApprovalRefuses()
    {
        // The rename event is the only evidence of a title change; nothing on the issue's own edit
        // metadata moves.
        var result = Resolve(Conversation(titleRenamedAt: Cutoff.AddMinutes(5)));

        Assert.False(result.IsApproved);
        Assert.Equal(ExecutionContextResult.Codes.BaseNeedsReview, result.Code);
    }

    [Fact]
    public void AnEditBeforeApprovalIsFine() =>
        Assert.True(Resolve(Conversation(bodyEditedAt: Cutoff.AddMinutes(-5))).IsApproved);

    // --- batch coverage -------------------------------------------------------------------------

    [Fact]
    public void ACommentPredatingTheCutoffIsIncludedByTheBatch()
    {
        var result = Resolve(Conversation(comments: Comment()));

        Assert.True(result.IsApproved);
        var decision = Assert.Single(result.Snapshot!.Decisions);
        Assert.Equal(DiscussionDecisionKind.Include, decision.Decision);
        Assert.Equal(DiscussionDecisionSource.Batch, decision.Source);
        Assert.Single(result.Snapshot.Discussion);
    }

    [Fact]
    public void ACommentAfterTheCutoffIsPendingAndBlocks()
    {
        var result = Resolve(Conversation(comments: Comment(createdAt: Cutoff.AddMinutes(5))));

        Assert.False(result.IsApproved);
        Assert.Equal(ExecutionContextResult.Codes.CommentPending, result.Code);
        Assert.Single(result.PendingUrls!);
    }

    [Fact]
    public void ACommentAtExactlyTheCutoffIsPending()
    {
        // Whole-second precision makes same-instant collisions ordinary, so equality resolves
        // against approval rather than being treated as covered.
        var result = Resolve(Conversation(comments: Comment(createdAt: Cutoff)));

        Assert.False(result.IsApproved);
        Assert.Equal(ExecutionContextResult.Codes.CommentPending, result.Code);
    }

    [Fact]
    public void EditingABatchCoveredCommentMakesItPendingAgain()
    {
        // The edit produces a revision the batch never saw.
        var result = Resolve(Conversation(comments: Comment(editedAt: Cutoff.AddMinutes(5))));

        Assert.False(result.IsApproved);
        Assert.Equal(ExecutionContextResult.Codes.CommentPending, result.Code);
    }

    [Fact]
    public void EveryPendingCommentIsReportedNotJustTheFirst()
    {
        var result = Resolve(Conversation(comments:
        [
            Comment("c1", createdAt: Cutoff.AddMinutes(5)),
            Comment("c2", createdAt: Cutoff.AddMinutes(6))
        ]));

        Assert.Equal(2, result.PendingUrls!.Count);
    }

    // --- explicit reactions ---------------------------------------------------------------------

    [Fact]
    public void AnAuthorisedIncludeCoversACommentTheBatchDoesNot()
    {
        var late = Cutoff.AddMinutes(5);
        var result = Resolve(Conversation(comments:
            Comment(createdAt: late, reactions: Reaction("THUMBS_UP", at: late.AddMinutes(1)))));

        Assert.True(result.IsApproved);
        var decision = Assert.Single(result.Snapshot!.Decisions);
        Assert.Equal(DiscussionDecisionSource.Reaction, decision.Source);
        Assert.Equal("maintainer", decision.DecidedBy);
        Assert.Equal("R1", decision.ReactionId);
    }

    [Fact]
    public void AnAuthorisedExcludeOverridesBatchInclusion()
    {
        var result = Resolve(Conversation(comments:
            Comment(reactions: Reaction("THUMBS_DOWN", at: Created.AddMinutes(30)))));

        Assert.True(result.IsApproved);
        Assert.Equal(DiscussionDecisionKind.Exclude, result.Snapshot!.Decisions[0].Decision);
        // Excluded content never reaches the agent.
        Assert.Empty(result.Snapshot.Discussion);
    }

    [Fact]
    public void AReactionFromAnUnauthorisedActorDecidesNothing()
    {
        var late = Cutoff.AddMinutes(5);
        var result = Resolve(Conversation(comments:
            Comment(createdAt: late, reactions: Reaction("THUMBS_UP", "bystander", late.AddMinutes(1)))));

        Assert.False(result.IsApproved);
        Assert.Equal(ExecutionContextResult.Codes.CommentPending, result.Code);
    }

    [Fact]
    public void AnActorWhoHasLostAuthorityStopsDeciding()
    {
        // Authority is evaluated now, not trusted from when the reaction was added — GitHub offers
        // no signed historical permission assertion.
        var late = Cutoff.AddMinutes(5);
        var conversation = Conversation(comments:
            Comment(createdAt: late, reactions: Reaction("THUMBS_UP", at: late.AddMinutes(1))));

        Assert.True(Resolve(conversation).IsApproved);
        Assert.False(Resolve(conversation, approver: Nobody).IsApproved);
    }

    [Fact]
    public void AReactionAtOrBeforeTheCommentRevisionDecidesNothing()
    {
        var late = Cutoff.AddMinutes(5);
        // Exactly at the revision: not strictly later, so it does not cover it.
        var atRevision = Resolve(Conversation(comments:
            Comment(createdAt: late, reactions: Reaction("THUMBS_UP", at: late))));
        Assert.False(atRevision.IsApproved);

        // Before the edit: decided the older text.
        var beforeEdit = Resolve(Conversation(comments:
            Comment(createdAt: Created, editedAt: late,
                reactions: Reaction("THUMBS_UP", at: Created.AddMinutes(1)))));
        Assert.False(beforeEdit.IsApproved);
    }

    [Fact]
    public void TheLatestAuthorisedDecisionWins()
    {
        var late = Cutoff.AddMinutes(5);
        var result = Resolve(Conversation(comments: Comment(createdAt: late, reactions:
        [
            Reaction("THUMBS_UP", at: late.AddMinutes(1), id: "R1"),
            Reaction("THUMBS_DOWN", at: late.AddMinutes(2), id: "R2")
        ])));

        Assert.True(result.IsApproved);
        Assert.Equal(DiscussionDecisionKind.Exclude, result.Snapshot!.Decisions[0].Decision);
        Assert.Equal("R2", result.Snapshot.Decisions[0].ReactionId);
    }

    [Fact]
    public void ConflictingDecisionsAtTheSameInstantRefuseRatherThanGuess()
    {
        var late = Cutoff.AddMinutes(5);
        var same = late.AddMinutes(1);
        var result = Resolve(Conversation(comments: Comment(createdAt: late, reactions:
        [
            Reaction("THUMBS_UP", at: same, id: "R1"),
            Reaction("THUMBS_DOWN", at: same, id: "R2")
        ])));

        Assert.False(result.IsApproved);
        Assert.Equal(ExecutionContextResult.Codes.DecisionAmbiguous, result.Code);
    }

    [Fact]
    public void AgreeingDecisionsAtTheSameInstantAreNotAConflict()
    {
        var late = Cutoff.AddMinutes(5);
        var same = late.AddMinutes(1);
        var result = Resolve(Conversation(comments: Comment(createdAt: late, reactions:
        [
            Reaction("THUMBS_UP", at: same, id: "R1"),
            Reaction("THUMBS_UP", "maintainer", same, "R2")
        ])));

        Assert.True(result.IsApproved);
        Assert.Equal(DiscussionDecisionKind.Include, result.Snapshot!.Decisions[0].Decision);
    }

    [Fact]
    public void UnrelatedReactionsAreIgnored()
    {
        var result = Resolve(Conversation(comments:
            Comment(reactions: Reaction("HEART", at: Created.AddMinutes(30)))));

        // Still batch-covered; the heart neither included nor excluded anything.
        Assert.True(result.IsApproved);
        Assert.Equal(DiscussionDecisionSource.Batch, result.Snapshot!.Decisions[0].Source);
    }

    [Fact]
    public void TheConfiguredReactionsAreHonoured()
    {
        var late = Cutoff.AddMinutes(5);
        var policy = new DecisionPolicy(ReactionKinds.Rocket, ReactionKinds.Hooray);
        var result = Resolve(
            Conversation(comments: Comment(createdAt: late, reactions: Reaction("ROCKET", at: late.AddMinutes(1)))),
            policy: policy);

        Assert.True(result.IsApproved);
        Assert.Equal(DiscussionDecisionKind.Include, result.Snapshot!.Decisions[0].Decision);
    }

    // --- protocol comments ----------------------------------------------------------------------

    [Fact]
    public void WrightyCommentsAreNeitherIncludedNorPending()
    {
        // A claim renewal after approval must not make a launchable item unlaunchable.
        var handover = Comment("c2", HandoverBody(), "maintainer", createdAt: Cutoff.AddMinutes(5));
        var result = Resolve(Conversation(comments: [Comment("c1"), handover]));

        Assert.True(result.IsApproved);
        Assert.Single(result.Snapshot!.Decisions);
        Assert.Equal("c1", result.Snapshot.Decisions[0].CommentId);
    }

    [Fact]
    public void OurOwnHandoverIsExcludedSoAPausedSessionResumesWithoutReapproval()
    {
        // The behaviour this policy exists for. A paused run posts a handover; without recognising
        // it, that comment is undecided discussion, the resume refuses, and the operator has to
        // re-approve to continue a session nothing external changed.
        var policy = new SelfAuthoredExclusionPolicy("wrighty-bot");
        var handover = Comment("c2", HandoverBody(), "wrighty-bot", createdAt: Cutoff.AddMinutes(5));

        var result = Resolve(
            Conversation(comments: [Comment("c1"), handover]),
            canExclude: policy.CanExcludeContent);

        Assert.True(result.IsApproved);
        Assert.Single(result.Snapshot!.Decisions);
        Assert.Equal("c1", result.Snapshot.Decisions[0].CommentId);
    }

    [Fact]
    public void AHandoverMarkerOnSomeoneElsesCommentStillBlocks()
    {
        // The reason the predicate is an identity rather than an authority level. A user with write
        // access can edit a maintainer's comment without changing its author, so a marker appended
        // there must not drop that maintainer's requirement from the agent's context. It stays
        // ordinary discussion and is decided like any other comment — here, pending, which blocks.
        var policy = new SelfAuthoredExclusionPolicy("wrighty-bot");
        var forged = Comment("c2", HandoverBody(), "maintainer", createdAt: Cutoff.AddMinutes(5));

        var result = Resolve(
            Conversation(comments: [Comment("c1"), forged]),
            canExclude: policy.CanExcludeContent);

        Assert.False(result.IsApproved);
        Assert.Equal(ExecutionContextResult.Codes.CommentPending, result.Code);
    }

    [Fact]
    public void AnUnresolvedIdentityLeavesOurOwnHandoverBlocking()
    {
        // Failing closed. An unnecessary re-approval is the cost; hiding content from review is not
        // an acceptable alternative.
        var policy = new SelfAuthoredExclusionPolicy(null);
        var handover = Comment("c2", HandoverBody(), "wrighty-bot", createdAt: Cutoff.AddMinutes(5));

        var result = Resolve(
            Conversation(comments: [Comment("c1"), handover]),
            canExclude: policy.CanExcludeContent);

        Assert.False(result.IsApproved);
    }

    [Fact]
    public void AnEmptyCommentIsNotTreatedAsARequirement()
    {
        var result = Resolve(Conversation(comments: [Comment("c1"), Comment("c2", body: "   ")]));

        Assert.True(result.IsApproved);
        Assert.Single(result.Snapshot!.Decisions);
    }

    private static string HandoverBody() =>
        Highbyte.Wrighty.Workers.HandoverRenderer.Marker + "\n\n### Wrighty handover\n\nnext actions";

    // --- minimized, limits, digest ---------------------------------------------------------------

    [Fact]
    public void AMinimizedCommentIsAnOrdinaryIncludedComment()
    {
        // Hiding carries no observable transition, so excluding it would create an inclusion change
        // that could never be detected afterwards.
        var result = Resolve(Conversation(comments: Comment(minimized: true)));

        Assert.True(result.IsApproved);
        Assert.Single(result.Snapshot!.Discussion);
        Assert.True(result.Snapshot.Discussion[0].Minimized);
    }

    [Fact]
    public void AnExcludedCommentStillCountsTowardTheEntryLimit()
    {
        var excluded = Comment("c2", reactions: Reaction("THUMBS_DOWN", at: Created.AddMinutes(30), id: "R2"));
        var result = Resolve(
            Conversation(comments: [Comment("c1"), excluded]),
            limits: new ContextLimits(MaxDiscussionEntries: 1));

        Assert.False(result.IsApproved);
        Assert.Equal(ExecutionContextResult.Codes.TooLarge, result.Code);
    }

    [Fact]
    public void AnExcludedCommentDoesNotCountTowardTheCharacterLimit()
    {
        var huge = Comment("c2", body: new string('x', 400),
            reactions: Reaction("THUMBS_DOWN", at: Created.AddMinutes(30), id: "R2"));
        var result = Resolve(
            Conversation(comments: [Comment("c1", body: "short"), huge]),
            limits: new ContextLimits(MaxTotalCharacters: 200));

        Assert.True(result.IsApproved);
    }

    [Fact]
    public void IncludedEntriesAreOrderedChronologically()
    {
        var result = Resolve(Conversation(comments:
        [
            Comment("c2", createdAt: Created.AddMinutes(10)),
            Comment("c1", createdAt: Created)
        ]));

        Assert.Equal(["c1", "c2"], result.Snapshot!.Discussion.Select(e => e.StableId));
    }

    [Fact]
    public void ChangingOnlyTheDecidingActorChangesTheRevision()
    {
        // Same included text, different approval evidence: deliberately a different revision.
        var late = Cutoff.AddMinutes(5);
        var byOne = Resolve(Conversation(comments:
            Comment(createdAt: late, reactions: Reaction("THUMBS_UP", "maintainer", late.AddMinutes(1)))));
        var byOther = Resolve(
            Conversation(comments:
                Comment(createdAt: late, reactions: Reaction("THUMBS_UP", "lead", late.AddMinutes(1)))),
            approver: actor => actor is "maintainer" or "lead");

        Assert.NotEqual(byOne.Snapshot!.Revision.Digest, byOther.Snapshot!.Revision.Digest);
    }

    [Fact]
    public void RefusalMessagesNeverQuoteCommentContent()
    {
        const string secret = "correct-horse-battery-staple";
        var result = Resolve(Conversation(comments: Comment(body: secret, createdAt: Cutoff.AddMinutes(5))));

        Assert.False(result.IsApproved);
        Assert.DoesNotContain(secret, result.Message!, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, string.Join(" ", result.PendingUrls!), StringComparison.Ordinal);
    }
}
