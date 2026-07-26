using Highbyte.Wrighty.ApprovedContext;
using Highbyte.Wrighty.Claims;
using Highbyte.Wrighty.Configuration;
using Highbyte.Wrighty.Models;
using Highbyte.Wrighty.Workers;

namespace Highbyte.Wrighty.UnitTests.ApprovedContext;

/// <summary>
/// The check that stops an agent being handed content nobody approved. Its post-claim role is to
/// establish there is an approved context; its pre-spawn role is to establish it is still the same
/// one, because workspace preparation takes long enough for a maintainer to change the issue.
/// </summary>
public class ExecutionContextLaunchCheckTests
{
    private static readonly WorkItemId Id = new("github:owner/repo#42");
    private static readonly DateTimeOffset Now = new(2026, 7, 26, 12, 0, 0, TimeSpan.Zero);
    private static readonly TrackerConfig Config = new() { Repository = "owner/repo", ProjectNumber = 1 };

    private sealed class StubProvider(params ExecutionContextResult[] results) : IExecutionContextProvider
    {
        private readonly Queue<ExecutionContextResult> results = new(results);
        public int Calls { get; private set; }
        public List<ContextReadPurpose> Purposes { get; } = [];

        public Task<ExecutionContextResult> GetAsync(
            TrackerConfig config, WorkItemId id, ContextReadPurpose purpose,
            ContextLimits limits, CancellationToken ct)
        {
            Calls++;
            Purposes.Add(purpose);
            // The last result repeats, so a test only supplies what it varies.
            return Task.FromResult(results.Count > 1 ? results.Dequeue() : results.Peek());
        }
    }

    private static ExecutionContextResult Approved(
        string body = "The worker should retry once.",
        params DiscussionEntry[] discussion) =>
        ExecutionContextResult.Approved(Snapshot(body, discussion));

    private static ExecutionContextSnapshot Snapshot(
        string body = "The worker should retry once.",
        params DiscussionEntry[] discussion)
    {
        var decisions = discussion
            .Select(entry => new DiscussionDecision(
                entry.StableId, DiscussionDecisionKind.Include,
                DiscussionDecisionSource.Batch, DecidedAt: Now))
            .ToArray();
        return new ExecutionContextSnapshot(
            Id, "Add retry handling", body,
            new ContextApproval(ContextApprovalSource.ProjectField, Now, Now),
            new BaseContentRevision(
                ContextRevisionSerializer.HashContent("Add retry handling"),
                ContextRevisionSerializer.HashContent(body)),
            ContextRevisionSerializer.Compute(
                Id, "Add retry handling", body, null, discussion, decisions, Now),
            discussion, decisions);
    }

    private static DiscussionEntry Entry(string id, string body) =>
        new(id, "maintainer", Now, body);

    private static WorkItemDetail Detail() =>
        new(Id, "Add retry handling", "body", null, "Todo", "P1", AutomaticExecutionAllowed: true);

    private static LaunchPreflightRequest Request(
        LaunchStage stage,
        LaunchKind kind = LaunchKind.Fresh,
        SessionContextMetadata? recorded = null) =>
        new(Config,
            new WorkerOptions(null, true, null, WorkspaceMode.Current, new Dictionary<string, string>(),
                null, TimeSpan.FromMinutes(30), FencedAction.Kill, null, "agent", false, false),
            Detail(), "claude", kind, stage,
            recorded is null ? null : Session(recorded));

    private static AgentSessionRecord Session(SessionContextMetadata recorded) =>
        new("claude", "session-1", "/tmp/ws", Now.AddMinutes(30), FromCurrentInstallation: true,
            Context: recorded);

    private static ExecutionContextLaunchCheck Check(IExecutionContextProvider? provider) =>
        new(_ => provider);

    [Fact]
    public void ItRunsAfterTheClaimAndBeforeTheSpawnButNotDuringSelection()
    {
        var check = Check(new StubProvider(Approved()));

        // Not at pre-claim: assembling a context pages a whole conversation, and spending that on
        // every candidate the selection scan considers would pay a full read for items about to be
        // rejected far more cheaply.
        Assert.False(check.AppliesTo(LaunchStage.PreClaim, LaunchKind.Fresh));
        Assert.True(check.AppliesTo(LaunchStage.PostClaim, LaunchKind.Fresh));
        Assert.True(check.AppliesTo(LaunchStage.PreSpawn, LaunchKind.Fresh));
    }

    [Theory]
    [InlineData(LaunchKind.Fresh)]
    [InlineData(LaunchKind.Resume)]
    [InlineData(LaunchKind.Recovery)]
    [InlineData(LaunchKind.Retry)]
    public void EveryLaunchKindIsGated(LaunchKind kind)
    {
        // A resumed or retried session receives approved content just as a fresh one does.
        var check = Check(new StubProvider(Approved()));
        Assert.True(check.AppliesTo(LaunchStage.PostClaim, kind));
        Assert.True(check.AppliesTo(LaunchStage.PreSpawn, kind));
    }

    [Fact]
    public async Task AnApprovedContextAdmitsAndIsRecorded()
    {
        var check = Check(new StubProvider(Approved()));

        var postClaim = await check.EvaluateAsync(Request(LaunchStage.PostClaim), default);

        Assert.True(postClaim.Admitted);
        Assert.Contains(postClaim.Evidence!, e => e.Contains("sha256:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ARefusedContextRefusesTheLaunchAndCarriesItsCode()
    {
        var check = Check(new StubProvider(ExecutionContextResult.Refused(
            ExecutionContextResult.Codes.CommentPending,
            "One comment has no decision.",
            ["https://github.com/owner/repo/issues/42#issuecomment-9"])));

        var decision = await check.EvaluateAsync(Request(LaunchStage.PostClaim), default);

        Assert.False(decision.Admitted);
        Assert.Equal(ExecutionContextResult.Codes.CommentPending, decision.Code);
        // The undecided comment travels with the refusal so an operator can act on it.
        Assert.Contains("issuecomment-9", string.Join(" ", decision.Evidence!), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUnchangedContextAdmitsAtSpawn()
    {
        var check = Check(new StubProvider(Approved()));

        Assert.True((await check.EvaluateAsync(Request(LaunchStage.PostClaim), default)).Admitted);
        var preSpawn = await check.EvaluateAsync(Request(LaunchStage.PreSpawn), default);

        Assert.True(preSpawn.Admitted);
        Assert.Contains("unchanged", string.Join(" ", preSpawn.Evidence!), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AContextThatChangedDuringWorkspacePreparationRefusesTheSpawn()
    {
        // The case the pre-spawn stage exists for: the item was approved when claimed, and edited
        // while the worktree was being created. Admitting would hand the agent content nobody
        // approved.
        var check = Check(new StubProvider(Approved(), Approved("Different requirements now.")));

        Assert.True((await check.EvaluateAsync(Request(LaunchStage.PostClaim), default)).Admitted);
        var preSpawn = await check.EvaluateAsync(Request(LaunchStage.PreSpawn), default);

        Assert.False(preSpawn.Admitted);
        Assert.Equal(ExecutionContextResult.Codes.RevisionChanged, preSpawn.Code);
        // Both revisions are named so the change is inspectable without printing any content.
        Assert.Equal(2, preSpawn.Evidence!.Count);
        Assert.DoesNotContain("Different requirements", string.Join(" ", preSpawn.Evidence),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task AContextRefusedAtSpawnRefusesEvenThoughTheClaimAdmitted()
    {
        var check = Check(new StubProvider(
            Approved(),
            ExecutionContextResult.Refused(
                ExecutionContextResult.Codes.BaseNeedsReview, "The body changed.")));

        Assert.True((await check.EvaluateAsync(Request(LaunchStage.PostClaim), default)).Admitted);
        var preSpawn = await check.EvaluateAsync(Request(LaunchStage.PreSpawn), default);

        Assert.False(preSpawn.Admitted);
        Assert.Equal(ExecutionContextResult.Codes.BaseNeedsReview, preSpawn.Code);
    }

    [Fact]
    public async Task AFreshSpawnWithNoRecordedPostClaimContextRefuses()
    {
        // Admitting here would start an agent on a context this launch never validated.
        var check = Check(new StubProvider(Approved()));

        var preSpawn = await check.EvaluateAsync(Request(LaunchStage.PreSpawn), default);

        Assert.False(preSpawn.Admitted);
        Assert.Equal(ExecutionContextResult.Codes.RevisionChanged, preSpawn.Code);
    }

    // ---------------------------------------------------------------------------------------
    // Resume, recovery and retry. None of them runs the post-claim stage — they re-enter an
    // already-claimed item — so their baseline is the context recorded with the session, and their
    // question is whether what changed may be given to a session already in progress.
    // ---------------------------------------------------------------------------------------

    [Theory]
    [InlineData(LaunchKind.Resume)]
    [InlineData(LaunchKind.Recovery)]
    [InlineData(LaunchKind.Retry)]
    public async Task AnUnchangedContextResumesFromTheRecordedSession(LaunchKind kind)
    {
        // The regression this guards: with no recorded session consulted, every resume refuses for
        // want of a post-claim resolution it never had the chance to make.
        var recorded = SessionContextMetadata.For(Snapshot());
        var check = Check(new StubProvider(Approved()));

        var decision = await check.EvaluateAsync(
            Request(LaunchStage.PreSpawn, kind, recorded), default);

        Assert.True(decision.Admitted);
        Assert.Contains("Identical", string.Join(" ", decision.Evidence!), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ANewApprovedCommentIsAdditiveAndResumes()
    {
        // What a resume usually exists for. The session keeps everything it had and gains one entry.
        var recorded = SessionContextMetadata.For(Snapshot());
        var check = Check(new StubProvider(
            Approved("The worker should retry once.", Entry("c1", "Also cover the timeout path."))));

        var decision = await check.EvaluateAsync(
            Request(LaunchStage.PreSpawn, LaunchKind.Resume, recorded), default);

        Assert.True(decision.Admitted);
        var resolved = check.TakeResolved(Id);
        Assert.Equal(ContextChangeKind.Additive, resolved!.Comparison!.Kind);
        // Phase 5 renders exactly this delta rather than re-sending the whole snapshot.
        Assert.Equal(["c1"], resolved.Comparison.NewEntryIds);
    }

    [Fact]
    public async Task AnEditedEntryBlocksTheResumeBecauseTheSessionCannotUnseeIt()
    {
        var recorded = SessionContextMetadata.For(
            Snapshot(discussion: Entry("c1", "Cover the timeout path.")));
        var check = Check(new StubProvider(
            Approved("The worker should retry once.", Entry("c1", "Actually, skip the timeout."))));

        var decision = await check.EvaluateAsync(
            Request(LaunchStage.PreSpawn, LaunchKind.Resume, recorded), default);

        Assert.False(decision.Admitted);
        Assert.Equal(ExecutionContextResult.Codes.ResumeBlocked, decision.Code);
        // The old text is not repeated back into an event, only the fact that it changed.
        Assert.DoesNotContain("skip the timeout", string.Join(" ", decision.Evidence!),
            StringComparison.Ordinal);
        Assert.Null(check.TakeResolved(Id));
    }

    [Fact]
    public async Task AChangedBodyBlocksTheResume()
    {
        var recorded = SessionContextMetadata.For(Snapshot());
        var check = Check(new StubProvider(Approved("Different requirements now.")));

        var decision = await check.EvaluateAsync(
            Request(LaunchStage.PreSpawn, LaunchKind.Resume, recorded), default);

        Assert.False(decision.Admitted);
        Assert.Equal(ExecutionContextResult.Codes.ResumeBlocked, decision.Code);
    }

    [Fact]
    public async Task ASessionWithNoRecordedContextCannotBeResumed()
    {
        // A session claimed before approved-context support, or one whose local record is gone.
        // Refusing is the safe reading: what that agent already holds cannot be established.
        var check = Check(new StubProvider(Approved()));

        var decision = await check.EvaluateAsync(
            Request(LaunchStage.PreSpawn, LaunchKind.Resume, new SessionContextMetadata()), default);

        Assert.False(decision.Admitted);
        Assert.Equal(ExecutionContextResult.Codes.ManifestUnavailable, decision.Code);
    }

    [Fact]
    public async Task AResumeCarriesContinuationSpendForwardOntoTheNewContext()
    {
        // The budget counts turns already spent on this session. A newly supplied context is not a
        // reason to hand it a fresh allowance.
        var recorded = SessionContextMetadata.For(Snapshot()) with
        {
            AutomaticContinuations = 3,
            ConsumedContinuationKeys = ["comment:c9@r1"],
            ReportRunIds = ["run-1"]
        };
        var check = Check(new StubProvider(
            Approved("The worker should retry once.", Entry("c1", "One more thing."))));

        await check.EvaluateAsync(Request(LaunchStage.PreSpawn, LaunchKind.Resume, recorded), default);
        var context = check.TakeSessionContext(Id);

        Assert.Equal(3, context!.AutomaticContinuations);
        Assert.Equal(["comment:c9@r1"], context.ConsumedContinuationKeys);
        Assert.Equal(["run-1"], context.ReportRunIds);
        // ...while the manifest is the newly supplied one, not the carried-forward one.
        Assert.Single(context.Manifest!.Included);
    }

    [Fact]
    public async Task AFreshLaunchRecordsTheContextItResolved()
    {
        var check = Check(new StubProvider(
            Approved("The worker should retry once.", Entry("c1", "Cover the timeout path."))));

        await check.EvaluateAsync(Request(LaunchStage.PostClaim), default);
        await check.EvaluateAsync(Request(LaunchStage.PreSpawn), default);
        var context = check.TakeSessionContext(Id);

        Assert.NotNull(context);
        Assert.Equal("c1", Assert.Single(context!.Manifest!.Included).CommentId);
        Assert.Equal(ContextApprovalSource.ProjectField, context.ApprovalSource);
        // A fresh session starts its continuation allowance at zero.
        Assert.Equal(0, context.AutomaticContinuations);
    }

    [Fact]
    public async Task NoSessionContextIsOfferedWhenTheLaunchWasRefused()
    {
        var recorded = SessionContextMetadata.For(Snapshot());
        var check = Check(new StubProvider(Approved("Different requirements now.")));

        await check.EvaluateAsync(Request(LaunchStage.PreSpawn, LaunchKind.Resume, recorded), default);

        Assert.Null(check.TakeSessionContext(Id));
    }

    [Fact]
    public async Task TheResolvedContextIsAvailableToTheCallerAndTakenOnlyOnce()
    {
        var check = Check(new StubProvider(Approved()));
        await check.EvaluateAsync(Request(LaunchStage.PostClaim), default);
        await check.EvaluateAsync(Request(LaunchStage.PreSpawn), default);

        var taken = check.TakeResolved(Id);
        Assert.NotNull(taken);
        Assert.Equal(Id, taken!.ItemId);
        // Removed as taken, so a later launch of the same item cannot read a stale one.
        Assert.Null(check.TakeResolved(Id));
    }

    [Fact]
    public async Task ARefusedSpawnLeavesNothingForTheCallerToUse()
    {
        var check = Check(new StubProvider(Approved(), Approved("changed")));
        await check.EvaluateAsync(Request(LaunchStage.PostClaim), default);
        await check.EvaluateAsync(Request(LaunchStage.PreSpawn), default);

        Assert.Null(check.TakeResolved(Id));
    }

    [Fact]
    public async Task ABackendWithoutTheCapabilityIsNotGatedOnIt()
    {
        // Not a refusal: a tracker with no approval surface has no approved context to check, and
        // the launch is decided by the other checks.
        var check = new ExecutionContextLaunchCheck(_ => null);

        Assert.True((await check.EvaluateAsync(Request(LaunchStage.PostClaim), default)).Admitted);
        Assert.True((await check.EvaluateAsync(Request(LaunchStage.PreSpawn), default)).Admitted);
    }

    [Fact]
    public async Task EachStageReadsWithItsOwnPurpose()
    {
        var provider = new StubProvider(Approved());
        var check = Check(provider);

        await check.EvaluateAsync(Request(LaunchStage.PostClaim), default);
        await check.EvaluateAsync(Request(LaunchStage.PreSpawn), default);

        // Two genuine reads. The pre-spawn stage must not reuse the post-claim answer, or it could
        // not detect a change at all.
        Assert.Equal(2, provider.Calls);
        Assert.Equal([ContextReadPurpose.PreClaim, ContextReadPurpose.PreLaunch], provider.Purposes);
    }

    [Fact]
    public async Task ContextsAreTrackedPerItem()
    {
        // A worker running several items must not compare one item's spawn read against another's
        // claim resolution.
        var other = new WorkItemId("github:owner/repo#43");
        var check = Check(new StubProvider(Approved()));

        await check.EvaluateAsync(Request(LaunchStage.PostClaim), default);

        Assert.NotNull(check.TakeResolved(Id));
        Assert.Null(check.TakeResolved(other));
    }
}
