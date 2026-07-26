using Highbyte.Wrighty.ApprovedContext;
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

    private static ExecutionContextResult Approved(string body = "The worker should retry once.")
    {
        var decisions = Array.Empty<DiscussionDecision>();
        return ExecutionContextResult.Approved(new ExecutionContextSnapshot(
            Id, "Add retry handling", body,
            new ContextApproval(ContextApprovalSource.ProjectField, Now, Now),
            new BaseContentRevision(
                ContextRevisionSerializer.HashContent("Add retry handling"),
                ContextRevisionSerializer.HashContent(body)),
            ContextRevisionSerializer.Compute(Id, "Add retry handling", body, null, [], decisions, Now),
            [], decisions));
    }

    private static WorkItemDetail Detail() =>
        new(Id, "Add retry handling", "body", null, "Todo", "P1", AutomaticExecutionAllowed: true);

    private static LaunchPreflightRequest Request(LaunchStage stage) =>
        new(Config,
            new WorkerOptions(null, true, null, WorkspaceMode.Current, new Dictionary<string, string>(),
                null, TimeSpan.FromMinutes(30), FencedAction.Kill, null, "agent", false, false),
            Detail(), "claude", LaunchKind.Fresh, stage);

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
    public async Task ASpawnWithNoRecordedPostClaimContextRefuses()
    {
        // Admitting here would start an agent on a context this launch never validated.
        var check = Check(new StubProvider(Approved()));

        var preSpawn = await check.EvaluateAsync(Request(LaunchStage.PreSpawn), default);

        Assert.False(preSpawn.Admitted);
        Assert.Equal(ExecutionContextResult.Codes.RevisionChanged, preSpawn.Code);
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
