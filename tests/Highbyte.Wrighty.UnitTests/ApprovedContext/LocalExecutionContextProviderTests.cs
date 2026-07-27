using Highbyte.Wrighty.ApprovedContext;
using Highbyte.Wrighty.Backends;
using Highbyte.Wrighty.Configuration;
using Highbyte.Wrighty.Models;
using Highbyte.Wrighty.Time;

namespace Highbyte.Wrighty.UnitTests.ApprovedContext;

/// <summary>
/// A backend with no discussion surface. The behaviour that matters is what it does NOT do: it
/// never fabricates discussion entries, and it still refuses an oversized item rather than
/// truncating one.
/// </summary>
public class LocalExecutionContextProviderTests
{
    private static readonly WorkItemId Id = new("local:1");
    private static readonly DateTimeOffset Now = new(2026, 7, 26, 12, 0, 0, TimeSpan.Zero);

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; } = now;
    }

    // Only the one method the provider actually uses. Before IWorkItemContentReader was split out
    // this had to stub claiming, mutation, and initialization as well, none of which it calls.
    private sealed class StubItems(WorkItemDetail? detail) : IWorkItemContentReader
    {
        public Task<WorkItemDetail?> GetAsync(TrackerConfig config, WorkItemId id, CancellationToken ct) =>
            Task.FromResult(detail);
    }

    private static WorkItemDetail Item(string title = "Add retry handling", string? body = null) =>
        new(Id, title, body ?? "The worker should retry once.", "file:///items/1.md", "Todo", "P1");

    private static Task<ExecutionContextResult> Read(
        WorkItemDetail? detail, ContextLimits? limits = null) =>
        new LocalExecutionContextProvider(new StubItems(detail), new FixedClock(Now))
            .GetAsync(new TrackerConfig(), Id, ContextReadPurpose.Diagnostics,
                limits ?? ContextLimits.Default, CancellationToken.None);

    [Fact]
    public async Task AnItemProducesAnApprovedSnapshotWithNoDiscussion()
    {
        var result = await Read(Item());

        Assert.True(result.IsApproved);
        var snapshot = result.Snapshot!;
        Assert.Empty(snapshot.Discussion);
        Assert.Empty(snapshot.Decisions);
        Assert.True(snapshot.IsFullyResolved);
        Assert.Equal(0, snapshot.PendingCount);
    }

    [Fact]
    public async Task TheApprovalIsMarkedAsComingFromTheBackendItself()
    {
        // Distinguishable from a maintainer approving a revision on a tracker, so a reader is never
        // misled into thinking a human reviewed this content.
        var snapshot = (await Read(Item())).Snapshot!;

        Assert.Equal(ContextApprovalSource.BackendLocal, snapshot.Approval.Source);
        Assert.True(snapshot.Approval.IsApproved);
    }

    [Fact]
    public async Task TheSnapshotCarriesTheItemsOwnTitleBodyAndUrl()
    {
        var snapshot = (await Read(Item())).Snapshot!;

        Assert.Equal("Add retry handling", snapshot.Title);
        Assert.Equal("The worker should retry once.", snapshot.Body);
        Assert.Equal("file:///items/1.md", snapshot.SourceUrl);
    }

    [Fact]
    public async Task UnchangedContentReadTwiceProducesTheSameRevision()
    {
        var first = (await Read(Item())).Snapshot!.Revision;
        var second = (await Read(Item())).Snapshot!.Revision;

        Assert.Equal(first.Digest, second.Digest);
        Assert.True(first.Matches(second));
    }

    [Theory]
    [InlineData("Renamed", null)]
    [InlineData(null, "Different requirements now.")]
    public async Task EditingTheItemChangesTheRevision(string? title, string? body)
    {
        var before = (await Read(Item())).Snapshot!.Revision;
        var after = (await Read(Item(title ?? "Add retry handling", body))).Snapshot!.Revision;

        Assert.NotEqual(before.Digest, after.Digest);
    }

    [Fact]
    public async Task AnEditBlocksAnUnattendedResumeJustAsAnEditedIssueWould()
    {
        // There is no edit history to bind to here, so the content hashes carry the whole base
        // revision. An edit must still be caught by the ordinary comparison.
        var before = (await Read(Item())).Snapshot!;
        var after = (await Read(Item(body: "Different requirements now."))).Snapshot!;

        var comparison = ContextChangeClassifier.Compare(ContextManifest.From(before), after);

        Assert.Equal(ContextChangeKind.BaseChanged, comparison.Kind);
        Assert.False(comparison.AllowsUnattendedResume);
    }

    [Fact]
    public async Task AnUnreadableItemIsRefusedRatherThanApprovedEmpty()
    {
        var result = await Read(null);

        Assert.False(result.IsApproved);
        Assert.Null(result.Snapshot);
        Assert.Equal(ExecutionContextResult.Codes.ReadFailed, result.Code);
    }

    [Fact]
    public async Task AnOversizedItemIsRefusedWithoutQuotingIt()
    {
        var secret = new string('x', 500);
        var result = await Read(Item(body: secret), new ContextLimits(MaxTotalCharacters: 100));

        Assert.False(result.IsApproved);
        Assert.Equal(ExecutionContextResult.Codes.TooLarge, result.Code);
        Assert.DoesNotContain(secret, result.Message!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheLimitRefusalNamesTheLimitItExceeded()
    {
        var result = await Read(Item(body: new string('x', 500)), new ContextLimits(MaxTotalCharacters: 100));

        Assert.Contains("100", result.Message!, StringComparison.Ordinal);
        Assert.Contains("maxTotalCharacters", result.Message!, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(ContextReadPurpose.PreClaim)]
    [InlineData(ContextReadPurpose.PreLaunch)]
    [InlineData(ContextReadPurpose.ResumeComparison)]
    [InlineData(ContextReadPurpose.Diagnostics)]
    public async Task ThePurposeDoesNotChangeTheResultingRevision(ContextReadPurpose purpose)
    {
        // Two reads of unchanged content are the same approved context regardless of why they were
        // taken; a pre-claim read and the pre-launch read that confirms it must agree.
        var baseline = (await Read(Item())).Snapshot!.Revision;
        var result = await new LocalExecutionContextProvider(new StubItems(Item()), new FixedClock(Now))
            .GetAsync(new TrackerConfig(), Id, purpose, ContextLimits.Default, CancellationToken.None);

        Assert.Equal(baseline.Digest, result.Snapshot!.Revision.Digest);
    }
}
