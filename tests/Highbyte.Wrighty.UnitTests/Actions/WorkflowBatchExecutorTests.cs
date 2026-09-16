using Highbyte.Wrighty.Actions;
using Highbyte.Wrighty.Errors;

namespace Highbyte.Wrighty.UnitTests.Actions;

public sealed class WorkflowBatchExecutorTests
{
    [Fact]
    public async Task Cancellation_before_first_item_leaves_everything_unprocessed()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var result = await WorkflowBatchExecutor.ExecuteAsync(["1", "2"],
            (_, _) => throw new InvalidOperationException("must not execute"), cancellation.Token);
        Assert.Equal("BATCH_CANCELLED", result.StopCode);
        Assert.All(result.Items, item => Assert.Equal("unprocessed", item.Outcome));
    }

    [Fact]
    public async Task Sequence_is_frozen_before_callbacks_and_progress_surrounds_each_mutation()
    {
        List<string> selection = ["1", "2"];
        List<string> events = [];
        var result = await WorkflowBatchExecutor.ExecuteAsync(selection, (id, _) =>
        {
            selection.Clear();
            selection.Add("unapproved");
            events.Add("apply " + id);
            return Task.FromResult<WorkflowActionResult?>(null);
        }, default, new Progress(events));
        Assert.Equal(["1", "2"], result.Items.Select(item => item.Id));
        Assert.Equal(["before 1", "apply 1", "after 1", "before 2", "apply 2", "after 2"], events);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Persistence_failure_stops_before_any_more_mutations(bool beforeMutation)
    {
        var calls = 0;
        await Assert.ThrowsAsync<IOException>(() => WorkflowBatchExecutor.ExecuteAsync(["1", "2"], (_, _) =>
        {
            calls++;
            return Task.FromResult<WorkflowActionResult?>(null);
        }, default, new FailedProgress(beforeMutation)));
        Assert.Equal(beforeMutation ? 0 : 1, calls);
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("limit")]
    [InlineData("empty-id")]
    public async Task Invalid_selection_is_rejected_before_any_work(string scenario)
    {
        string[] ids = scenario switch
        {
            "duplicate" => ["1", "1"],
            "limit" => Enumerable.Range(1, 101).Select(i => i.ToString()).ToArray(),
            _ => [" "]
        };
        var error = await Assert.ThrowsAsync<TrackerException>(() => WorkflowBatchExecutor.ExecuteAsync(ids,
            (_, _) => throw new InvalidOperationException("must not execute"), default));
        Assert.Equal("BATCH_SELECTION_INVALID", error.Code);
    }

    private sealed class Progress(List<string> events) : IWorkflowBatchProgress
    {
        public void Starting(string id) => events.Add("before " + id);
        public void Completed(WorkflowBatchItemResult result) => events.Add("after " + result.Id);
    }

    private sealed class FailedProgress(bool before) : IWorkflowBatchProgress
    {
        public void Starting(string id) { if (before) throw new IOException("Journal unavailable"); }
        public void Completed(WorkflowBatchItemResult result) { if (!before) throw new IOException("Journal unavailable"); }
    }
}
