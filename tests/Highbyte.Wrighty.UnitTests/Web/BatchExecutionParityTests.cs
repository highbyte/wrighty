using Highbyte.Wrighty.Actions;
using Highbyte.Wrighty.Configuration;
using Highbyte.Wrighty.Errors;
using Highbyte.Wrighty.Web;

namespace Highbyte.Wrighty.UnitTests.Web;

public sealed class BatchExecutionParityTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "wrighty-batch-parity-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("CLAIM_HELD", "skipped", 3, false)]
    [InlineData("ACTION_STATE_CHANGED", "skipped", 3, false)]
    [InlineData("BATCH_ITEM_INELIGIBLE", "skipped", 3, false)]
    [InlineData("BATCH_CONFIG_CHANGED", "failed", 2, false)]
    [InlineData("STORE_BROKEN", "failed", 2, true)]
    [InlineData("io", "failed", 2, true)]
    [InlineData("cancel", "failed", 2, true)]
    public async Task Web_and_persisted_batches_share_partial_outcomes_and_do_not_replay(
        string scenario, string secondOutcome, int callCount, bool uncertain)
    {
        var config = new TrackerConfig { SourcePath = Path.Combine(root, ".wrighty.json") };
        var cli = new WorkflowBatchStore(root);
        var candidates = Enumerable.Range(1, 3).Select(i => new WorkflowBatchCandidate($"local:{i}", $"Item {i}",
            new string('A', 64), new("Todo", "ready", false, null), "Queue")).ToArray();
        var preview = await cli.CreateAsync(config, "queue", candidates, 3, 3, default);
        var web = new BoardBatchStore();
        var intent = web.Create(BoardBatchAction.Queue, "revision",
            candidates.Select(c => new BoardBatchCandidate(c.Id, c.Id, c.Title)).ToArray(), 3, 3);
        var cliCalls = new List<string>();
        var webCalls = new List<string>();
        var logged = new List<Exception>();
        var cliResult = await cli.ExecuteAsync(config, preview.Id, (_, c, _) =>
        {
            cliCalls.Add(c.Id);
            return Apply(c.Id, scenario);
        }, default);
        var webResult = await web.ExecuteAsync(intent.Id, "revision", i => BoardBatchExecution.ExecuteAsync(i, async (c, _) =>
        {
            webCalls.Add(c.Id);
            return await Apply(c.Id, scenario);
        }, logged.Add));
        Assert.Equal(cliCalls, webCalls);
        Assert.Equal(callCount, cliCalls.Count);
        Assert.Equal(secondOutcome, cliResult.Items[1].Outcome);
        Assert.Equal(cliResult.Items.Select(i => i.Outcome), webResult.Items.Select(Outcome));
        Assert.Equal(cliResult.Items.Select(i => i.MutationMayHaveApplied), webResult.Items.Select(i => i.MutationMayHaveApplied));
        Assert.Equal(uncertain, webResult.Items[1].MutationMayHaveApplied);
        Assert.Equal(secondOutcome == "failed" ? 1 : 0, logged.Count);
        if (uncertain) Assert.Contains("may have applied", webResult.Items[1].Reason);
        if (callCount == 2)
        {
            Assert.Equal("unprocessed", cliResult.Items[2].Outcome);
            Assert.Contains(cliResult.StopCode!, webResult.AbortReason);
        }
        // Both surface stores retain partial results, including ambiguous failures.
        var replay = await cli.ExecuteAsync(config, preview.Id,
            (_, _, _) => throw new InvalidOperationException("must not replay"), default);
        Assert.Equal(cliResult.Items, replay.Items);
        var webReplay = await web.ExecuteAsync(intent.Id, "revision",
            _ => throw new InvalidOperationException("must not replay"));
        Assert.Same(webResult, webReplay);
    }

    private static string Outcome(BoardBatchItemResult item)
    {
        if (item.Succeeded) return "applied";
        if (item.Skipped) return "skipped";
        return item.Aborted ? "unprocessed" : "failed";
    }

    private static Task<WorkflowActionResult> Apply(string id, string scenario)
    {
        if (id == "local:2")
        {
            if (scenario == "io") throw new IOException("private backend details");
            if (scenario == "cancel") throw new OperationCanceledException();
            throw new TrackerException(scenario, "private backend details");
        }
        return Task.FromResult(new WorkflowActionResult(id, "queue", "applied", DateTimeOffset.UtcNow,
            "version", new("Todo", "ready", false, null), new("Queue", "queued", true, null)));
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
