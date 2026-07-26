using Highbyte.Wrighty.ApprovedContext;
using Highbyte.Wrighty.Claims;
using Highbyte.Wrighty.Models;
using Highbyte.Wrighty.Workers;

namespace Highbyte.Wrighty.UnitTests.ApprovedContext;

public class DiscussionEntryTests
{
    private static readonly DateTimeOffset Created = new(2026, 7, 26, 10, 0, 0, TimeSpan.Zero);

    private static DiscussionEntry Entry(DateTimeOffset? edited = null, bool minimized = false) =>
        new("c1", "octocat", Created, "body", LastEditedAt: edited, Minimized: minimized);

    [Fact]
    public void AnUneditedEntrysRevisionIsItsCreation() =>
        Assert.Equal(Created, Entry().RevisionAt);

    [Fact]
    public void AnEditMovesTheRevisionForward()
    {
        var edited = Created.AddMinutes(5);
        Assert.Equal(edited, Entry(edited).RevisionAt);
    }

    [Fact]
    public void ADecisionStrictlyAfterTheRevisionCoversIt() =>
        Assert.True(Entry().IsCoveredBy(Created.AddSeconds(1)));

    [Fact]
    public void ADecisionBeforeTheRevisionDoesNotCoverIt() =>
        Assert.False(Entry().IsCoveredBy(Created.AddSeconds(-1)));

    [Fact]
    public void ADecisionAtExactlyTheRevisionInstantDoesNotCoverIt()
    {
        // Finding F5: the GitHub timestamps being compared here carry whole-second precision, so a
        // maintainer approving in the same second as a comment lands is an ordinary occurrence
        // rather than an exotic race. Ambiguity resolves against approval.
        Assert.False(Entry().IsCoveredBy(Created));
    }

    [Fact]
    public void AnEditInvalidatesADecisionThatCoveredTheOlderRevision()
    {
        var decidedAt = Created.AddSeconds(30);
        var editedAfterDecision = Entry(decidedAt.AddSeconds(30));

        Assert.True(Entry().IsCoveredBy(decidedAt));
        Assert.False(editedAfterDecision.IsCoveredBy(decidedAt));
    }

    [Fact]
    public void MinimizedEntriesAreOrdinaryEntries()
    {
        // Finding F4: minimizing carries no observable timestamp transition and no timeline event,
        // so excluding a minimized entry would create an inclusion change Wrighty could never
        // detect. Decision 16's fallback applies — minimized entries are included.
        var minimized = Entry(minimized: true);
        Assert.Equal(Created, minimized.RevisionAt);
        Assert.True(minimized.IsCoveredBy(Created.AddSeconds(1)));
    }
}

public class BaseContentRevisionTests
{
    private static readonly DateTimeOffset Approved = new(2026, 7, 26, 12, 0, 0, TimeSpan.Zero);

    private static BaseContentRevision Revision(
        DateTimeOffset? bodyEdited = null,
        DateTimeOffset? titleRenamed = null) =>
        new("title-hash", "body-hash", bodyEdited, bodyEdited is null ? 0 : 1,
            titleRenamed, titleRenamed is null ? 0 : 1);

    [Fact]
    public void NeverEditedContentIsAlwaysCovered()
    {
        Assert.Null(Revision().LastChangedAt);
        Assert.True(Revision().IsCoveredBy(Approved));
    }

    [Fact]
    public void ABodyEditBeforeApprovalIsCovered() =>
        Assert.True(Revision(bodyEdited: Approved.AddMinutes(-5)).IsCoveredBy(Approved));

    [Fact]
    public void ABodyEditAfterApprovalIsNotCovered() =>
        Assert.False(Revision(bodyEdited: Approved.AddMinutes(5)).IsCoveredBy(Approved));

    [Fact]
    public void ATitleRenameAfterApprovalIsNotCovered()
    {
        // Finding F3: a title change advances neither lastEditedAt nor the user-content edit
        // history — it is visible only as a rename event, tracked separately here. Reading only the
        // body's edit metadata would miss every title change.
        Assert.False(Revision(titleRenamed: Approved.AddMinutes(5)).IsCoveredBy(Approved));
    }

    [Fact]
    public void TheLatestOfTitleAndBodyDecidesCoverage()
    {
        var revision = Revision(bodyEdited: Approved.AddMinutes(-10), titleRenamed: Approved.AddMinutes(5));
        Assert.Equal(Approved.AddMinutes(5), revision.LastChangedAt);
        Assert.False(revision.IsCoveredBy(Approved));
    }

    [Fact]
    public void AnEditAtExactlyTheApprovalInstantIsNotCovered() =>
        Assert.False(Revision(bodyEdited: Approved).IsCoveredBy(Approved));
}

public class ContextApprovalTests
{
    [Fact]
    public void TheDefaultIsNotApproved()
    {
        Assert.False(ContextApproval.NotApproved.IsApproved);
        Assert.Equal(ContextApprovalSource.None, ContextApproval.NotApproved.Source);
    }

    [Fact]
    public void ASourceWithoutATimestampIsNotApproved() =>
        Assert.False(new ContextApproval(ContextApprovalSource.ProjectField).IsApproved);

    [Fact]
    public void AResolvedFieldWithATimestampIsApproved() =>
        Assert.True(new ContextApproval(
            ContextApprovalSource.ProjectField, DateTimeOffset.UnixEpoch).IsApproved);
}

public class ExecutionContextSnapshotTests
{
    private static readonly WorkItemId Item = new("github:owner/repo#42");
    private static readonly DateTimeOffset Now = new(2026, 7, 26, 12, 0, 0, TimeSpan.Zero);

    private static ExecutionContextSnapshot Snapshot(params DiscussionDecision[] decisions)
    {
        var included = decisions
            .Where(d => d.Decision == DiscussionDecisionKind.Include)
            .Select(d => new DiscussionEntry(d.CommentId, "octocat", Now, "body"))
            .ToArray();
        return new ExecutionContextSnapshot(Item, "t", "b",
            new ContextApproval(ContextApprovalSource.ProjectField, Now, Now),
            new BaseContentRevision("title-hash", "body-hash"),
            ContextRevisionSerializer.Compute(Item, "t", "b", null, included, decisions, Now),
            included, decisions);
    }

    private static DiscussionDecision Decided(string id, DiscussionDecisionKind kind) =>
        new(id, kind, DiscussionDecisionSource.Batch);

    [Fact]
    public void EveryDecisionResolvedMakesTheSnapshotLaunchable()
    {
        var snapshot = Snapshot(
            Decided("c1", DiscussionDecisionKind.Include),
            Decided("c2", DiscussionDecisionKind.Exclude));

        Assert.True(snapshot.IsFullyResolved);
        Assert.Empty(snapshot.Pending);
        Assert.Equal(1, snapshot.IncludedCount);
        Assert.Equal(1, snapshot.ExcludedCount);
        Assert.Equal(0, snapshot.PendingCount);
    }

    [Fact]
    public void ASinglePendingEntryBlocksTheSnapshot()
    {
        // Silently omitting an undecided comment would narrow the approved task without anyone
        // choosing to, so one Pending entry is enough to stop the launch.
        var snapshot = Snapshot(
            Decided("c1", DiscussionDecisionKind.Include),
            DiscussionDecision.Pending("c2"));

        Assert.False(snapshot.IsFullyResolved);
        Assert.Equal(1, snapshot.PendingCount);
        Assert.Equal(["c2"], snapshot.Pending.Select(d => d.CommentId));
    }

    [Fact]
    public void PendingIdentifiesEveryUndecidedEntryNotJustTheFirst()
    {
        var snapshot = Snapshot(
            DiscussionDecision.Pending("c1"),
            Decided("c2", DiscussionDecisionKind.Include),
            DiscussionDecision.Pending("c3"));

        Assert.Equal(["c1", "c3"], snapshot.Pending.Select(d => d.CommentId));
    }

    [Fact]
    public void ADiscussionlessBackendProducesAResolvedSnapshot()
    {
        // The Local Markdown backend returns exactly this: title and body, no entries, and
        // therefore nothing to decide.
        var snapshot = Snapshot();

        Assert.True(snapshot.IsFullyResolved);
        Assert.Empty(snapshot.Discussion);
        Assert.Empty(ExecutionContextSnapshot.NoDiscussion);
        Assert.Empty(ExecutionContextSnapshot.NoDecisions);
    }

    [Fact]
    public void APendingDecisionCarriesNoApprovalEvidence()
    {
        var pending = DiscussionDecision.Pending("c1");

        Assert.Equal(DiscussionDecisionKind.Pending, pending.Decision);
        Assert.Equal(DiscussionDecisionSource.None, pending.Source);
        Assert.Null(pending.DecidedBy);
        Assert.Null(pending.DecidedAt);
        Assert.Null(pending.ReactionId);
    }
}

public class ContextLimitResultTests
{
    private static DiscussionEntry Entry(string id = "c1", int bodyLength = 10) =>
        new(id, "octocat", DateTimeOffset.UnixEpoch, new string('x', bodyLength));

    [Fact]
    public void AContextWithinEveryLimitPasses() =>
        Assert.True(ContextLimitResult.Check("t", "b", [Entry()], [Entry()], ContextLimits.Default).Within);

    [Fact]
    public void ExactlyAtTheEntryLimitPasses()
    {
        var limits = new ContextLimits(MaxDiscussionEntries: 3);
        var entries = new[] { Entry("c1"), Entry("c2"), Entry("c3") };
        Assert.True(ContextLimitResult.Check("t", "b", entries, entries, limits).Within);
    }

    [Fact]
    public void OneOverTheEntryLimitFails()
    {
        var limits = new ContextLimits(MaxDiscussionEntries: 3);
        var entries = new[] { Entry("c1"), Entry("c2"), Entry("c3"), Entry("c4") };
        var result = ContextLimitResult.Check("t", "b", entries, entries, limits);

        Assert.False(result.Within);
        Assert.Equal(ContextLimitResult.TooLargeCode, result.Code);
    }

    [Fact]
    public void ExcludedEntriesStillCountTowardTheEntryLimit()
    {
        // Wrighty must retrieve and classify every relevant entry, so the cost is incurred whether
        // or not the entry reaches the agent.
        var limits = new ContextLimits(MaxDiscussionEntries: 1);
        var relevant = new[] { Entry("c1"), Entry("c2") };
        Assert.False(ContextLimitResult.Check("t", "b", relevant, [Entry("c1")], limits).Within);
    }

    [Fact]
    public void OneOversizedEntryFailsAndNamesItWithoutQuotingIt()
    {
        var limits = new ContextLimits(MaxEntryCharacters: 50);
        var oversized = Entry("c9", 51);
        var result = ContextLimitResult.Check("t", "b", [oversized], [oversized], limits);

        Assert.False(result.Within);
        Assert.Contains("c9", result.Message!, StringComparison.Ordinal);
        Assert.DoesNotContain(oversized.Body, result.Message!, StringComparison.Ordinal);
    }

    [Fact]
    public void OnlyIncludedBodiesCountTowardTheCharacterLimit()
    {
        // An excluded entry never reaches the agent, so it cannot push the prompt over the limit.
        var limits = new ContextLimits(MaxTotalCharacters: 100);
        var relevant = new[] { Entry("c1", 40), Entry("c2", 400) };
        Assert.True(ContextLimitResult.Check("t", "b", relevant, [relevant[0]], limits).Within);
    }

    [Fact]
    public void AggregateOverflowFails()
    {
        var limits = new ContextLimits(MaxTotalCharacters: 100);
        var entries = new[] { Entry("c1", 60), Entry("c2", 60) };
        var result = ContextLimitResult.Check("t", "b", entries, entries, limits);

        Assert.False(result.Within);
        Assert.Equal(ContextLimitResult.TooLargeCode, result.Code);
    }

    [Fact]
    public void TitleAndBodyCountTowardTheAggregate()
    {
        var limits = new ContextLimits(MaxTotalCharacters: 20);
        Assert.False(ContextLimitResult.Check(new string('t', 15), new string('b', 15), [], [], limits).Within);
    }

    [Theory]
    [InlineData(0, 10, 10)]
    [InlineData(10, 0, 10)]
    [InlineData(10, 10, 0)]
    [InlineData(-1, 10, 10)]
    public void NonPositiveLimitsAreRejected(int entries, int perEntry, int total) =>
        Assert.False(ContextLimitResult.Validate(
            new ContextLimits(entries, perEntry, total)).Within);

    [Fact]
    public void APerEntryLimitAboveTheAggregateIsRejected() =>
        Assert.False(ContextLimitResult.Validate(new ContextLimits(10, 500, 100)).Within);

    [Fact]
    public void ImplementationMaximumsBoundConfiguredLimits()
    {
        // A mistyped configuration must not authorize unbounded allocation from issue-controlled
        // input.
        Assert.False(ContextLimitResult.Validate(new ContextLimits(MaxDiscussionEntries: 100_000)).Within);
        Assert.False(ContextLimitResult.Validate(
            new ContextLimits(MaxEntryCharacters: 50_000_000, MaxTotalCharacters: 50_000_000)).Within);
    }

    [Fact]
    public void TheDefaultsValidate() =>
        Assert.True(ContextLimitResult.Validate(ContextLimits.Default).Within);
}

public class AgentRunReportTests
{
    private static readonly WorkItemId Item = new("github:owner/repo#42");

    [Fact]
    public void TheReportIdIsStableForTheSameItemAndRun() =>
        Assert.Equal(
            AgentRunReport.DeriveReportId(Item, "run-abc"),
            AgentRunReport.DeriveReportId(Item, "run-abc"));

    [Fact]
    public void ADifferentRunProducesADifferentReportId() =>
        Assert.NotEqual(
            AgentRunReport.DeriveReportId(Item, "run-abc"),
            AgentRunReport.DeriveReportId(Item, "run-def"));

    [Fact]
    public void ADifferentItemProducesADifferentReportId() =>
        Assert.NotEqual(
            AgentRunReport.DeriveReportId(Item, "run-abc"),
            AgentRunReport.DeriveReportId(new WorkItemId("github:owner/repo#43"), "run-abc"));

    [Fact]
    public void TheReportIdDoesNotDependOnFieldConcatenationOrder()
    {
        // "a" + "bc" and "ab" + "c" must not collide, or a retry could update another run's report.
        Assert.NotEqual(
            AgentRunReport.DeriveReportId(new WorkItemId("local:1"), "23"),
            AgentRunReport.DeriveReportId(new WorkItemId("local:12"), "3"));
    }

    [Fact]
    public void AReportWithNoAgentNarrativeIsObservedOnly()
    {
        var report = new AgentRunReport("run-1", "report-1", "claude",
            RunReportDisposition.NeedsAttention, AgentOutcome.Succeeded, DateTimeOffset.UnixEpoch);
        Assert.True(report.IsObservedOnly);
    }

    [Fact]
    public void AnySuppliedNarrativeMakesItMoreThanObserved()
    {
        var report = new AgentRunReport("run-1", "report-1", "claude",
            RunReportDisposition.NeedsAttention, AgentOutcome.Succeeded, DateTimeOffset.UnixEpoch,
            Summary: "Did the thing.");
        Assert.False(report.IsObservedOnly);
    }
}

public class TrustedContinuationTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 26, 12, 0, 0, TimeSpan.Zero);

    private static TrustedContinuationEvent Comment(string revision = "rev-1") =>
        new("c1", TrustedContinuationSource.Comment, "solo-developer", Now,
            TrustedContinuationKind.Continue, revision);

    [Fact]
    public void ACommentsConsumptionKeyIncludesItsRevision()
    {
        // An edited comment is a new candidate; the same revision seen again is not, so a poll
        // cannot spend a second agent turn on content already acted upon.
        Assert.NotEqual(Comment("rev-1").ConsumptionKey, Comment("rev-2").ConsumptionKey);
        Assert.Equal(Comment().ConsumptionKey, Comment().ConsumptionKey);
    }

    [Fact]
    public void AReactionsConsumptionKeyIsItsIdAlone()
    {
        var reaction = new TrustedContinuationEvent("r7", TrustedContinuationSource.Reaction,
            "solo-developer", Now, TrustedContinuationKind.CompletionRequested);
        Assert.Equal("reaction:r7", reaction.ConsumptionKey);
    }

    [Fact]
    public void ACommentAndAReactionSharingAnIdDoNotShareAKey()
    {
        var reaction = new TrustedContinuationEvent("c1", TrustedContinuationSource.Reaction,
            "solo-developer", Now, TrustedContinuationKind.Continue);
        Assert.NotEqual(Comment().ConsumptionKey, reaction.ConsumptionKey);
    }

    [Fact]
    public void AFreshBudgetPermitsQueueing() =>
        Assert.True(new TrustedContinuationBudget().CanQueueAt(Now));

    [Fact]
    public void AnExhaustedBudgetRefusesRegardlessOfCooldown()
    {
        var budget = new TrustedContinuationBudget(MaxAutomaticContinuations: 2, Used: 2);
        Assert.True(budget.IsExhausted);
        Assert.Equal(0, budget.Remaining);
        Assert.False(budget.CanQueueAt(Now.AddDays(1)));
    }

    [Fact]
    public void TheCooldownBlocksAnImmediateSecondQueue()
    {
        var budget = new TrustedContinuationBudget(Used: 1, LastQueuedAt: Now,
            Cooldown: TimeSpan.FromSeconds(30));
        Assert.False(budget.CanQueueAt(Now.AddSeconds(29)));
        Assert.True(budget.CanQueueAt(Now.AddSeconds(30)));
    }
}

public class SessionContextMetadataTests
{
    private static readonly WorkItemId Item = new("local:1");
    private static readonly DateTimeOffset Now = new(2026, 7, 26, 12, 0, 0, TimeSpan.Zero);

    private static ExecutionContextSnapshot Snapshot(string body = "b")
    {
        var decisions = Array.Empty<DiscussionDecision>();
        return new ExecutionContextSnapshot(Item, "t", body,
            new ContextApproval(ContextApprovalSource.ProjectField, Now, Now),
            new BaseContentRevision(
                ContextRevisionSerializer.HashContent("t"),
                ContextRevisionSerializer.HashContent(body)),
            ContextRevisionSerializer.Compute(Item, "t", body, null, [], decisions, Now),
            [], decisions);
    }

    [Fact]
    public void ConsumptionIsIdempotent()
    {
        var candidate = new TrustedContinuationEvent("c1", TrustedContinuationSource.Comment,
            "solo", Now, TrustedContinuationKind.Continue, "rev-1");
        var metadata = SessionContextMetadata.For(Snapshot());

        Assert.False(metadata.HasConsumed(candidate));
        var once = metadata.WithConsumed(candidate, Now);
        Assert.True(once.HasConsumed(candidate));
        Assert.Equal(1, once.AutomaticContinuations);

        // A repeated poll of the same revision must not spend another turn.
        var twice = once.WithConsumed(candidate, Now.AddMinutes(1));
        Assert.Equal(1, twice.AutomaticContinuations);
        Assert.Equal(Now, twice.LastAutomaticQueueAt);
    }

    [Fact]
    public void SupersedingCarriesContinuationStateOntoTheNewContext()
    {
        var candidate = new TrustedContinuationEvent("c1", TrustedContinuationSource.Comment,
            "solo", Now, TrustedContinuationKind.Continue, "rev-1");
        var consumed = SessionContextMetadata.For(Snapshot()).WithConsumed(candidate, Now);

        var next = consumed.Supersede(Snapshot("changed body"));

        // The new context is recorded, but the spend record survives — otherwise a new snapshot
        // would silently reset the continuation budget and allow a runaway loop.
        Assert.NotEqual(consumed.SuppliedDigest, next.SuppliedDigest);
        Assert.Equal(1, next.AutomaticContinuations);
        Assert.True(next.HasConsumed(candidate));
    }

    [Fact]
    public void AnOlderSessionRecordWithoutContextRemainsReadable()
    {
        // Records written before approved-context support deserialize with a null Context, which
        // classifies as ManifestUnavailable rather than being mistaken for "nothing changed".
        var session = new AgentSessionRecord("claude", "session-1", "/tmp/ws",
            Now.AddHours(1), FromCurrentInstallation: true);

        Assert.Null(session.Context);
        Assert.Equal(ContextChangeKind.ManifestUnavailable,
            ContextChangeClassifier.Compare(session.Context?.Manifest, Snapshot()).Kind);
    }
}
