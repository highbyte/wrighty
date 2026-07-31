using Highbyte.Wrighty.AgentContext;
using Highbyte.Wrighty.ApprovedContext;
using Highbyte.Wrighty.Claims;
using Highbyte.Wrighty.Configuration;
using Highbyte.Wrighty.GitHub;
using Highbyte.Wrighty.Models;
using Highbyte.Wrighty.Workers;

namespace Highbyte.Wrighty.UnitTests.ApprovedContext;

/// <summary>
/// Deciding whether a trusted author's comment continues a waiting session.
///
/// These drive the real <see cref="ApprovedContextResolver"/> rather than hand-building snapshots.
/// The self-trigger cases are the reason: what stops Wrighty's own comments from continuing a
/// session forever lives in the resolver and the classifier, so a test that fabricates decisions
/// would assert nothing about the loop it is supposed to prevent.
/// </summary>
public class TrustedContinuationEvaluatorTests
{
    private const string Trusted = "highbyte";
    private static readonly WorkItemId Item = new("github:owner/repo#42");
    private static readonly DateTimeOffset Created = new(2026, 7, 26, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Cutoff = new(2026, 7, 26, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Captured = new(2026, 7, 26, 13, 0, 0, TimeSpan.Zero);

    /// <summary>Well past any debounce, so timing never accidentally decides a test about content.</summary>
    private static readonly DateTimeOffset Now = Captured.AddHours(1);

    private static GitHubComment Comment(
        string id,
        string body,
        string author = Trusted,
        DateTimeOffset? createdAt = null,
        DateTimeOffset? editedAt = null) =>
        new(id, author, "OWNER", createdAt ?? Cutoff.AddMinutes(30), editedAt,
            $"https://github.com/owner/repo/issues/42#issuecomment-{id}", body,
            false, null, []);

    /// <summary>
    /// The solo configuration that makes the self-trigger hazard real: the trusted author and the
    /// identity Wrighty publishes its own reports under are the same person.
    /// </summary>
    private static ExecutionContextSnapshot Snapshot(params GitHubComment[] comments)
    {
        var conversation = new GitHubConversation(
            "Add retry handling", "The worker should retry once.",
            "https://github.com/owner/repo/issues/42",
            Created.AddHours(-1), null, null, comments);

        var result = new ApprovedContextResolver(
                isApprover: actor => actor == Trusted,
                canExcludeContent: actor => actor == Trusted,
                policy: null,
                isTrustedAuthor: actor => actor == Trusted)
            .Resolve(
                Item, conversation,
                new ContextApproval(ContextApprovalSource.ProjectField, Cutoff, Cutoff),
                ContextLimits.Default, Captured);

        Assert.True(result.IsApproved, $"fixture did not resolve: {result.Code} {result.Message}");
        return result.Snapshot!;
    }

    private static ContinuationVerdict Evaluate(
        ExecutionContextSnapshot snapshot,
        ContextManifest? supplied = null,
        SessionContinuationState? state = null,
        WorkerContinuationConfig? config = null,
        DateTimeOffset? now = null) =>
        new TrustedContinuationEvaluator().Evaluate(
            snapshot, supplied, state ?? new SessionContinuationState(),
            config ?? new WorkerContinuationConfig(), now ?? Now);

    private static string ReportComment(string runId = "run-abc123") =>
        $$"""
        {{WrightyCommentClassifier.SessionReportPrefix}}
        {"itemId":"github:owner/repo#42","runId":"{{runId}}","reportId":"report-def456"}
        -->
        ### Wrighty session report

        **Observed outcome:** Needs attention
        """;

    private static string HandoverComment() =>
        HandoverRenderer.Marker + "\n\n### Wrighty handover — needs attention\n\nClarify the item.";

    private static string ClaimComment() =>
        ClaimMarker.Format(new ClaimRecord(
            Version: 3,
            EventId: Guid.NewGuid().ToString("N"),
            InstallationId: "abcdef123456",
            ClaimedAt: DateTimeOffset.UnixEpoch,
            ExpiresAt: DateTimeOffset.UnixEpoch.AddHours(1),
            EventType: "acquired",
            ClaimantId: "agent:worker:1",
            ClaimToken: "token-1",
            ClaimantKind: ClaimantKinds.ToStorageValue(ClaimantKind.Agent)));

    // --- the loop that must never close ---------------------------------------------------------

    [Theory]
    [InlineData("session report")]
    [InlineData("handover")]
    [InlineData("claim marker")]
    public void WrightysOwnCommentUnderTheTrustedIdentityNeverContinues(string kind)
    {
        // The hazard: in the recommended solo setup Wrighty publishes as the trusted author, so a
        // naive author check would read its own needs-attention report as a reply asking to
        // continue — and every continuation would publish another report, forever.
        var body = kind switch
        {
            "session report" => ReportComment(),
            "handover" => HandoverComment(),
            _ => ClaimComment()
        };

        var verdict = Evaluate(Snapshot(Comment("c1", body)));

        Assert.Equal(ContinuationOutcome.NoCandidate, verdict.Outcome);
    }

    [Fact]
    public void WrightysReportDoesNotMaskARealReplyPostedAlongsideIt()
    {
        // The protocol comment is dropped, not the whole conversation with it.
        var verdict = Evaluate(Snapshot(
            Comment("c1", ReportComment()),
            Comment("c2", "Use the retry budget from the config.",
                createdAt: Cutoff.AddMinutes(40))));

        Assert.Equal(ContinuationOutcome.Queue, verdict.Outcome);
        Assert.Equal("c2", verdict.Trigger!.CommentId);
    }

    // --- ordinary triggering --------------------------------------------------------------------

    [Fact]
    public void ATrustedReplySinceTheLastRunQueues()
    {
        var verdict = Evaluate(Snapshot(Comment("c1", "Use the retry budget from the config.")));

        Assert.Equal(ContinuationOutcome.Queue, verdict.Outcome);
        Assert.Equal(Trusted, verdict.Trigger!.Actor);
        Assert.Equal(["comment:c1@" + Comment("c1", "x").RevisionAt.ToUniversalTime().ToString("O")],
            verdict.ConsumedKeys);
    }

    [Fact]
    public void AnUntrustedAuthorsReplyDoesNotQueue()
    {
        // It is still ordinary discussion under the normal approval rules; it simply is not a
        // continuation trigger. The batch cutoff covers it, so the snapshot stays resolvable.
        var verdict = Evaluate(Snapshot(
            Comment("c1", "I think this needs more detail.", author: "someone-else",
                createdAt: Cutoff.AddMinutes(-30))));

        Assert.Equal(ContinuationOutcome.NoCandidate, verdict.Outcome);
    }

    [Fact]
    public void ACommentTheLastRunAlreadyHeldDoesNotQueue()
    {
        // Present in the supplied manifest at the same revision: the agent has already read it, so
        // it is not new information and must not spend another turn.
        var snapshot = Snapshot(Comment("c1", "Use the retry budget from the config."));
        var supplied = ContextManifest.From(snapshot);

        var verdict = Evaluate(snapshot, supplied);

        Assert.Equal(ContinuationOutcome.NoCandidate, verdict.Outcome);
    }

    [Fact]
    public void EditingACommentTheLastRunHeldQueuesAgain()
    {
        var before = Snapshot(Comment("c1", "Use the retry budget."));
        var supplied = ContextManifest.From(before);

        var after = Snapshot(Comment(
            "c1", "Use the retry budget from the config.", editedAt: Cutoff.AddMinutes(45)));

        var verdict = Evaluate(after, supplied);

        Assert.Equal(ContinuationOutcome.Queue, verdict.Outcome);
    }

    // --- consumption ----------------------------------------------------------------------------

    [Fact]
    public void AConsumedRevisionDoesNotQueueTwiceAcrossARestart()
    {
        var snapshot = Snapshot(Comment("c1", "Use the retry budget from the config."));

        var first = Evaluate(snapshot);
        Assert.Equal(ContinuationOutcome.Queue, first.Outcome);

        // What a restart reloads: the persisted state, not the in-memory evaluator.
        var persisted = new SessionContinuationState()
            .WithConsumed(first.ConsumedKeys!, Now);
        var roundTripped = System.Text.Json.JsonSerializer.Deserialize<SessionContinuationState>(
            System.Text.Json.JsonSerializer.Serialize(persisted))!;

        var second = Evaluate(snapshot, state: roundTripped, now: Now.AddHours(1));

        Assert.Equal(ContinuationOutcome.AlreadyConsumed, second.Outcome);
    }

    [Fact]
    public void OneContinuationSpendsOneTurnHoweverManyCommentsItCarried()
    {
        var snapshot = Snapshot(
            Comment("c1", "Use the retry budget.", createdAt: Cutoff.AddMinutes(30)),
            Comment("c2", "And log the attempt count.", createdAt: Cutoff.AddMinutes(31)));

        var verdict = Evaluate(snapshot);
        Assert.Equal(ContinuationOutcome.Queue, verdict.Outcome);
        Assert.Equal(2, verdict.ConsumedKeys!.Count);
        // The newest names the trigger; both are consumed so neither queues a second, empty run.
        Assert.Equal("c2", verdict.Trigger!.CommentId);

        var after = new SessionContinuationState().WithConsumed(verdict.ConsumedKeys, Now);
        Assert.Equal(1, after.AutomaticContinuations);
    }

    // --- spend controls -------------------------------------------------------------------------

    [Fact]
    public void AFreshRevisionDefersRatherThanBeingRejected()
    {
        // Deferring is what lets the final text of a just-edited comment be the one acted on.
        // Rejecting would consume the candidate and need another edit to bring it back.
        var editedAt = Now.AddSeconds(-2);
        var snapshot = Snapshot(Comment("c1", "Use the retry budget.", editedAt: editedAt));

        var verdict = Evaluate(snapshot);

        Assert.Equal(ContinuationOutcome.Deferred, verdict.Outcome);
        Assert.Null(verdict.ConsumedKeys);
    }

    [Fact]
    public void AnExhaustedBudgetStaysPutInsteadOfQueueing()
    {
        var snapshot = Snapshot(Comment("c1", "Use the retry budget from the config."));
        var spent = new SessionContinuationState(AutomaticContinuations: 10);

        var verdict = Evaluate(snapshot, state: spent);

        Assert.Equal(ContinuationOutcome.LimitReached, verdict.Outcome);
    }

    [Fact]
    public void ACooldownDelaysTheNextContinuation()
    {
        var snapshot = Snapshot(Comment("c1", "Use the retry budget from the config."));
        var justQueued = new SessionContinuationState(
            AutomaticContinuations: 1, LastQueuedAt: Now.AddSeconds(-5));

        Assert.Equal(ContinuationOutcome.CoolingDown, Evaluate(snapshot, state: justQueued).Outcome);
        Assert.Equal(
            ContinuationOutcome.Queue,
            Evaluate(snapshot, state: justQueued, now: Now.AddMinutes(1)).Outcome);
    }

    // --- command-only mode ----------------------------------------------------------------------

    private static readonly WorkerContinuationConfig CommandOnly = new()
    {
        Trigger = WorkerContinuationConfig.TriggerModes.CommandOnly
    };

    [Fact]
    public void CommandOnlyIgnoresConversationalReplies()
    {
        var verdict = Evaluate(
            Snapshot(Comment("c1", "Yes, please continue with that approach.")),
            config: CommandOnly);

        Assert.Equal(ContinuationOutcome.NoCandidate, verdict.Outcome);
    }

    [Fact]
    public void CommandOnlyAcceptsTheExactFirstLineAndKeepsTheRestAsContext()
    {
        var verdict = Evaluate(
            Snapshot(Comment("c1", "/wrighty continue\n\nUse the retry budget from the config.")),
            config: CommandOnly);

        Assert.Equal(ContinuationOutcome.Queue, verdict.Outcome);
    }

    [Theory]
    [InlineData("Let's /wrighty continue from here.")]
    [InlineData("Do the wrighty continue thing.")]
    [InlineData("`/wrighty continue`")]
    public void CommandOnlyNeverParsesProseAsAControlCommand(string body)
    {
        // The command is a whole normalized first line, never a substring — otherwise discussing
        // the command would run the agent.
        var verdict = Evaluate(Snapshot(Comment("c1", body)), config: CommandOnly);

        Assert.Equal(ContinuationOutcome.NoCandidate, verdict.Outcome);
    }
}
