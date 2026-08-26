using Highbyte.Wrighty.Errors;
using Highbyte.Wrighty.Web;

namespace Highbyte.Wrighty.UnitTests.Web;

public sealed class BoardBatchStoreTests
{
    [Fact]
    public void Intent_freezes_canonical_candidates_and_caps_the_batch_at_one_hundred()
    {
        var store = new BoardBatchStore();
        var candidates = Enumerable.Range(1, 125)
            .Reverse()
            .Select(index => new BoardBatchCandidate(
                $"local:{index:D3}",
                $"#{index}",
                $"Item {index}"))
            .ToArray();

        var intent = store.Create(
            BoardBatchAction.Queue,
            "revision-a",
            candidates,
            candidates.Length,
            shownCount: 130);

        Assert.Equal(100, intent.Candidates.Count);
        Assert.Equal("local:001", intent.Candidates[0].Id);
        Assert.Equal("local:100", intent.Candidates[^1].Id);
        Assert.Equal(125, intent.EligibleCount);
        Assert.Equal(130, intent.ShownCount);
        Assert.Matches("^[0-9a-f]{32}$", intent.Id);
    }

    [Fact]
    public async Task Duplicate_confirmation_returns_the_recorded_result_without_executing_twice()
    {
        var store = new BoardBatchStore();
        var intent = store.Create(
            BoardBatchAction.Resume,
            "revision-a",
            [new BoardBatchCandidate("local:1", "#1", "One")],
            1,
            1);
        var calls = 0;
        Task<BoardBatchResult> Execute(BoardBatchIntent value)
        {
            calls++;
            return Task.FromResult(new BoardBatchResult(
                value.Id,
                value.Action,
                DateTimeOffset.UtcNow,
                [new BoardBatchItemResult("local:1", "#1", true, false)]));
        }

        var first = await store.ExecuteAsync(intent.Id, "revision-a", Execute);
        var duplicate = await store.ExecuteAsync(intent.Id, "revision-a", Execute);

        Assert.Same(first, duplicate);
        Assert.Equal(1, calls);
        Assert.Null(store.LatestResult);
    }

    [Fact]
    public async Task Intent_rejects_expiry_tampering_and_configuration_drift()
    {
        var clock = new AdjustableTimeProvider(
            new DateTimeOffset(2026, 8, 26, 10, 0, 0, TimeSpan.Zero));
        var store = new BoardBatchStore(clock);
        var expired = store.Create(
            BoardBatchAction.Queue,
            "revision-a",
            [new BoardBatchCandidate("local:1", "#1", "One")],
            1,
            1);
        clock.Advance(TimeSpan.FromMinutes(6));

        var expiry = await Assert.ThrowsAsync<TrackerException>(() =>
            store.ExecuteAsync(expired.Id, "revision-a", UnexpectedExecution));
        Assert.Equal("BOARD_BATCH_EXPIRED", expiry.Code);

        var current = store.Create(
            BoardBatchAction.Queue,
            "revision-a",
            [new BoardBatchCandidate("local:1", "#1", "One")],
            1,
            1);
        var drift = await Assert.ThrowsAsync<TrackerException>(() =>
            store.ExecuteAsync(current.Id, "revision-b", UnexpectedExecution));
        Assert.Equal("BOARD_BATCH_CONFIG_CHANGED", drift.Code);

        var unknown = await Assert.ThrowsAsync<TrackerException>(() =>
            store.ExecuteAsync("not-an-intent", "revision-a", UnexpectedExecution));
        Assert.Equal("BOARD_BATCH_UNKNOWN", unknown.Code);
    }

    [Fact]
    public async Task Dismiss_only_removes_the_matching_latest_result()
    {
        var store = new BoardBatchStore();
        var intent = store.Create(
            BoardBatchAction.Dequeue,
            "revision-a",
            [new BoardBatchCandidate("local:1", "#1", "One")],
            1,
            1);
        await store.ExecuteAsync(intent.Id, "revision-a", value => Task.FromResult(
            new BoardBatchResult(
                value.Id,
                value.Action,
                DateTimeOffset.UtcNow,
                [new BoardBatchItemResult("local:1", "#1", false, true, "Changed")])));

        Assert.False(store.Dismiss("another-intent"));
        Assert.NotNull(store.LatestResult);
        Assert.True(store.Dismiss(intent.Id));
        Assert.Null(store.LatestResult);
    }

    [Fact]
    public async Task Successful_completion_clears_a_previous_warning()
    {
        var store = new BoardBatchStore();
        var warningIntent = store.Create(
            BoardBatchAction.Queue,
            "revision-a",
            [new BoardBatchCandidate("local:1", "#1", "One")],
            1,
            1);
        await store.ExecuteAsync(warningIntent.Id, "revision-a", value => Task.FromResult(
            new BoardBatchResult(
                value.Id,
                value.Action,
                DateTimeOffset.UtcNow,
                [new BoardBatchItemResult("local:1", "#1", false, true, "Changed")])));
        Assert.NotNull(store.LatestResult);

        var successfulIntent = store.Create(
            BoardBatchAction.Queue,
            "revision-a",
            [new BoardBatchCandidate("local:2", "#2", "Two")],
            1,
            1);
        await store.ExecuteAsync(successfulIntent.Id, "revision-a", value => Task.FromResult(
            new BoardBatchResult(
                value.Id,
                value.Action,
                DateTimeOffset.UtcNow,
                [new BoardBatchItemResult("local:2", "#2", true, false)])));

        Assert.Null(store.LatestResult);
    }

    [Fact]
    public void Result_counts_partial_outcomes_without_mislabeling_aborted_items()
    {
        var result = new BoardBatchResult(
            "intent",
            BoardBatchAction.Queue,
            DateTimeOffset.UtcNow,
            [
                new BoardBatchItemResult("1", "#1", true, false),
                new BoardBatchItemResult("2", "#2", false, true, "Changed"),
                new BoardBatchItemResult("3", "#3", false, false, "Backend error"),
                new BoardBatchItemResult("4", "#4", false, false, "Not processed", true)
            ],
            "Stopped");

        Assert.Equal(1, result.SucceededCount);
        Assert.Equal(1, result.SkippedCount);
        Assert.Equal(1, result.FailedCount);
        Assert.Equal(1, result.AbortedCount);
        Assert.Equal(3, result.NotProcessedCount);
        Assert.True(result.HasIssues);
    }

    private static Task<BoardBatchResult> UnexpectedExecution(BoardBatchIntent _) =>
        throw new Xunit.Sdk.XunitException("The rejected intent must not execute.");

    private sealed class AdjustableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;

        public void Advance(TimeSpan value) => now += value;
    }
}
