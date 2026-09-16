using System.Text.Json;
using Highbyte.Wrighty.Actions;
using Highbyte.Wrighty.Configuration;
using Highbyte.Wrighty.Errors;

namespace Highbyte.Wrighty.UnitTests.Actions;

public sealed class WorkflowBatchStoreTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "wrighty-batch-" + Guid.NewGuid().ToString("N"));
    private readonly BatchClock clock = new();
    private TrackerConfig Config => new() { SourcePath = Path.Combine(root, ".wrighty.json"), Backend = "local-markdown" };
    private WorkflowBatchStore Store => new(root, clock);
    private Task<WorkflowBatchPreview> Preview(int count = 3) => Store.CreateAsync(Config, "queue",
        Enumerable.Range(1, count).Select(i => new WorkflowBatchCandidate($"local:{i}", $"Item {i}", new string('A', 64),
            new("Todo", "ready", false, null), "Authorizes automatic execution")).ToArray(), count, count, default);
    private static Task<WorkflowActionResult> Applied(WorkflowBatchPreview preview, WorkflowBatchCandidate candidate) =>
        Task.FromResult(new WorkflowActionResult(candidate.Id, preview.Action, "applied", DateTimeOffset.UtcNow,
            candidate.StateVersion, candidate.Before, candidate.Before with { Status = "Worker queue", AutomaticExecutionAllowed = true }));

    [Fact]
    public async Task Persisted_preview_survives_new_store_and_replay_does_not_execute_again()
    {
        var preview = await Preview();
        var calls = 0;
        var first = await Store.ExecuteAsync(Config, preview.Id, (p, c, ct) => { calls++; return Applied(p,c); }, default);
        Assert.Equal(3, calls);
        Assert.All(first.Items, item => Assert.Equal("applied", item.Outcome));
        clock.Now += TimeSpan.FromMinutes(6);
        var replay = await Store.ExecuteAsync(Config, preview.Id, (_, _, _) => throw new InvalidOperationException("replayed"), default);
        Assert.Equal(JsonSerializer.Serialize(first), JsonSerializer.Serialize(replay));
        Assert.DoesNotContain("claimToken", await File.ReadAllTextAsync(RecordPath()));
    }

    [Fact]
    public async Task Concurrent_executors_apply_the_frozen_set_only_once()
    {
        var preview = await Preview();
        var calls = 0;
        async Task<WorkflowActionResult> Execute(WorkflowBatchPreview p, WorkflowBatchCandidate c, CancellationToken ct)
        {
            Interlocked.Increment(ref calls);
            await Task.Delay(30, ct);
            return await Applied(p, c);
        }
        var results = await Task.WhenAll(Store.ExecuteAsync(Config, preview.Id, Execute, default),
            Store.ExecuteAsync(Config, preview.Id, Execute, default));
        Assert.Equal(3, calls);
        Assert.All(results, result => Assert.Equal(3, result.Items.Count));
    }

    [Theory]
    [InlineData("expired", "BATCH_EXPIRED")]
    [InlineData("config", "BATCH_CONFIG_CHANGED")]
    [InlineData("scope", "BATCH_UNKNOWN")]
    [InlineData("id", "BATCH_UNKNOWN")]
    public async Task Invalid_execution_refuses_before_any_item(string scenario, string code)
    {
        var preview = await Preview();
        var config = Config;
        var id = preview.Id;
        if (scenario == "expired") clock.Now += TimeSpan.FromMinutes(5);
        if (scenario == "config") config = config with { DefaultPickFrom = "Other queue" };
        if (scenario == "scope") config = config with { SourcePath = Path.Combine(root, "other.json") };
        if (scenario == "id") id = "../../not-a-preview";
        var error = await Assert.ThrowsAsync<TrackerException>(() => Store.ExecuteAsync(config, id,
            (_, _, _) => throw new InvalidOperationException("must not execute"), default));
        Assert.Equal(code, error.Code);
    }

    [Theory]
    [InlineData("ACTION_STATE_CHANGED", "skipped", 3, false)]
    [InlineData("CLAIM_HELD", "skipped", 3, false)]
    [InlineData("BATCH_CONFIG_CHANGED", "failed", 2, false)]
    [InlineData("STORE_BROKEN", "failed", 2, true)]
    public async Task Conflict_continues_but_systemic_failure_stops_with_partial_results(
        string code, string outcome, int callsExpected, bool ambiguous)
    {
        var preview = await Preview();
        var calls = 0;
        var result = await Store.ExecuteAsync(Config, preview.Id, (p, c, ct) =>
        {
            if (++calls == 2) throw new TrackerException(code, "private diagnostics");
            return Applied(p,c);
        }, default);
        Assert.Equal(callsExpected, calls);
        Assert.Equal("applied", result.Items[0].Outcome);
        Assert.Equal(outcome, result.Items[1].Outcome);
        Assert.Equal(ambiguous, result.Items[1].MutationMayHaveApplied);
        Assert.Equal(callsExpected == 2 ? "unprocessed" : "applied", result.Items[2].Outcome);
        Assert.DoesNotContain("private diagnostics", JsonSerializer.Serialize(result));
    }

    [Fact]
    public async Task Cancellation_between_items_preserves_applied_work_and_leaves_rest_unprocessed()
    {
        var preview = await Preview();
        using var cancel = new CancellationTokenSource();
        var result = await Store.ExecuteAsync(Config, preview.Id, (p, c, ct) =>
        { cancel.Cancel(); return Applied(p,c); }, cancel.Token);
        Assert.Equal("BATCH_CANCELLED", result.StopCode);
        Assert.Equal(["applied", "unprocessed", "unprocessed"], result.Items.Select(item => item.Outcome));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Interrupted_run_is_never_restarted_and_marks_only_inflight_item_uncertain(bool inFlight)
    {
        var preview = await Preview();
        var applied = new WorkflowBatchItemResult("local:1", "applied", Applied: await Applied(preview, preview.Candidates[0]));
        var interrupted = new WorkflowBatchRecord(preview, "running", [applied], inFlight ? "local:2" : null);
        await File.WriteAllTextAsync(RecordPath(), JsonSerializer.Serialize(interrupted));
        var result = await Store.ExecuteAsync(Config, preview.Id, (_, _, _) => throw new InvalidOperationException("must not resume"), default);
        Assert.Equal("BATCH_INTERRUPTED", result.StopCode);
        Assert.Equal("applied", result.Items[0].Outcome);
        Assert.Equal(inFlight ? "failed" : "unprocessed", result.Items[1].Outcome);
        Assert.Equal(inFlight, result.Items[1].MutationMayHaveApplied);
        Assert.Equal("unprocessed", result.Items[2].Outcome);
    }

    [Fact]
    public async Task Cancellation_during_mutation_is_reported_as_uncertain()
    {
        var preview = await Preview();
        var result = await Store.ExecuteAsync(Config, preview.Id, (_, _, _) => throw new OperationCanceledException(), default);
        Assert.Equal("BATCH_CANCELLED", result.StopCode);
        Assert.True(result.Items[0].MutationMayHaveApplied);
        Assert.All(result.Items.Skip(1), item => Assert.Equal("unprocessed", item.Outcome));
    }

    [Theory]
    [InlineData("null-version")]
    [InlineData("duplicate")]
    [InlineData("invalid-json")]
    public async Task Corrupt_records_fail_closed(string scenario)
    {
        var preview = await Preview();
        if (scenario == "invalid-json") await File.WriteAllTextAsync(RecordPath(), "{");
        else
        {
            var candidates = preview.Candidates.ToArray();
            candidates[1] = scenario == "duplicate" ? candidates[0] : candidates[1] with { StateVersion = null! };
            await File.WriteAllTextAsync(RecordPath(), JsonSerializer.Serialize(new WorkflowBatchRecord(preview with { Candidates = candidates }, "preview", [])));
        }
        var error = await Assert.ThrowsAsync<TrackerException>(() => Store.ReadAsync(Config, preview.Id, default));
        Assert.Equal("BATCH_RECORD_INVALID", error.Code);
    }

    [Fact]
    public async Task Preview_limit_scope_requirement_and_retention_are_enforced()
    {
        var error = await Assert.ThrowsAsync<TrackerException>(() => Preview(101));
        Assert.Equal("BATCH_TOO_LARGE", error.Code);
        error = await Assert.ThrowsAsync<TrackerException>(() => Store.CreateAsync(Config with { SourcePath = null }, "queue", [], 0, 0, default));
        Assert.Equal("BATCH_SCOPE_REQUIRED", error.Code);
        await Preview();
        var old = RecordPath();
        File.SetLastWriteTimeUtc(old, clock.Now.AddDays(-2).UtcDateTime);
        await Preview();
        Assert.False(File.Exists(old));
    }

    [Fact]
    public async Task Failed_result_persistence_retains_uncertain_marker_and_prevents_replay()
    {
        var preview = await Preview();
        var calls = 0;
        var path = RecordPath();
        var error = await Record.ExceptionAsync(() => Store.ExecuteAsync(Config, preview.Id, (p, c, ct) =>
        {
            calls++;
            Directory.CreateDirectory(path + ".tmp"); // Block the post-mutation journal replacement.
            return Applied(p, c);
        }, default));
        Assert.True(error is IOException or UnauthorizedAccessException);
        Directory.Delete(path + ".tmp");
        var recovered = await Store.ExecuteAsync(Config, preview.Id,
            (_, _, _) => throw new InvalidOperationException("must not replay"), default);
        Assert.Equal(1, calls);
        Assert.True(recovered.Items[0].MutationMayHaveApplied);
        Assert.All(recovered.Items.Skip(1), item => Assert.Equal("unprocessed", item.Outcome));
    }

    private string RecordPath() => Directory.GetFiles(root, "*.json", SearchOption.AllDirectories).Single();
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
    private sealed class BatchClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
