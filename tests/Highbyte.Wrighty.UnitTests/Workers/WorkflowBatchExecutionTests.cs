using Highbyte.Wrighty.Actions;
using Highbyte.Wrighty.AgentContext;
using Highbyte.Wrighty.Backends;
using Highbyte.Wrighty.Claims;
using Highbyte.Wrighty.Errors;
using Highbyte.Wrighty.Models;

namespace Highbyte.Wrighty.UnitTests.Workers;

public sealed partial class LocalDispatchStateTests
{
    [Fact]
    public async Task Batch_resume_uses_shared_session_fencing_and_preserves_recorded_session()
    {
        var (backend, config, id, oldHandle) = await CreatePausedItemAsync();
        var tracker = new TrackerService(new TrackerBackendRegistry([backend]));
        var before = await tracker.GetOperationalAsync(config, id, default);
        var store = new WorkflowBatchStore(Path.Combine(Path.GetDirectoryName(config.SourcePath)!, "batches"));
        var service = new WorkflowBatchService(tracker, store);
        var preview = await service.PreviewAsync(config, "resume", [id], default);
        Assert.Single(preview.Candidates);
        var result = await service.ExecuteAsync(config, preview.Id, _ => Task.FromResult(config), default);
        Assert.Equal("applied", Assert.Single(result.Items).Outcome);
        var after = await tracker.GetOperationalAsync(config, id, default);
        Assert.Equal(DispatchStates.Queued, after.Item.DispatchState);
        Assert.Equal(before.Session!.SessionId, after.Session!.SessionId);
        await Assert.ThrowsAsync<TrackerException>(() => backend.RenewClaimAsync(config, id, oldHandle, null, null, default));
    }

    [Fact]
    public async Task Batch_revalidates_reviewed_content_and_configuration_between_items()
    {
        var f = await WorkflowFixture();
        var store = new WorkflowBatchStore(Path.Combine(Path.GetDirectoryName(f.Config.SourcePath)!, "batches"));
        var service = new WorkflowBatchService(f.Tracker, store);
        var preview = await service.PreviewAsync(f.Config, "queue", [f.Id], default);
        var claim = await f.Backend.TryClaimAsync(f.Config, f.Id, AgentExecutionContext.Human, default);
        var handle = new ClaimHandle(AgentExecutionContext.Human, claim.ClaimToken);
        await f.Backend.UpdateAsync(f.Config, f.Id, new(new(Title: OptionalValue<string>.From("Edited since review"),
            Body: default, Status: default, Priority: default), false, ClaimHandle: handle), default);
        await f.Backend.ReleaseAsync(f.Config, f.Id, handle, false, DispatchStateOnRelease.Preserve, default);
        var result = await service.ExecuteAsync(f.Config, preview.Id, _ => Task.FromResult(f.Config), default);
        Assert.Equal("ACTION_STATE_CHANGED", Assert.Single(result.Items).Code);
        Assert.Equal("Ideas", (await f.Tracker.GetAsync(f.Config, f.Id, default)).Status);
        preview = await service.PreviewAsync(f.Config, "queue", [f.Id], default);
        result = await service.ExecuteAsync(f.Config, preview.Id,
            _ => Task.FromResult(f.Config with { DefaultPickFrom = "Other queue" }), default);
        Assert.Equal("BATCH_CONFIG_CHANGED", result.StopCode);
        Assert.False(Assert.Single(result.Items).MutationMayHaveApplied);
    }

    [Fact]
    public async Task Batch_preview_counts_eligible_items_and_caps_the_frozen_set()
    {
        var f = await WorkflowFixture();
        var ids = new List<WorkItemId> { f.Id };
        for (var i = 0; i < 101; i++)
            ids.Add((await f.Backend.CreateAsync(f.Config,
                new CreateWorkItemOperation(new($"Batch {i}", "Body", "Ideas", "P1"), false), default)).Id);
        await f.Actions.ExecuteAsync(f.Config, ids[1], "queue", null, default);
        var store = new WorkflowBatchStore(Path.Combine(Path.GetDirectoryName(f.Config.SourcePath)!, "batches"));
        var preview = await new WorkflowBatchService(f.Tracker, store).PreviewAsync(f.Config, "queue", ids, default);
        Assert.Equal(102, preview.SelectedCount);
        Assert.Equal(101, preview.EligibleCount);
        Assert.Equal(100, preview.Candidates.Count);
        Assert.True(preview.Limited);
        Assert.DoesNotContain(preview.Candidates, candidate => candidate.Id == ids[1].Value);
        Assert.Equal(preview.Candidates.OrderBy(item => item.Id, StringComparer.Ordinal), preview.Candidates);
    }
}
