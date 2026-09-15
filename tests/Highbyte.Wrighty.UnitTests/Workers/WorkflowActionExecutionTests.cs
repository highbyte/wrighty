using Highbyte.Wrighty.Actions;
using Highbyte.Wrighty.AgentContext;
using Highbyte.Wrighty.Backends;
using Highbyte.Wrighty.Claims;
using Highbyte.Wrighty.Configuration;
using Highbyte.Wrighty.Errors;
using Highbyte.Wrighty.LocalMarkdown;
using Highbyte.Wrighty.Models;

namespace Highbyte.Wrighty.UnitTests.Workers;

public sealed partial class LocalDispatchStateTests
{
    private async Task<(LocalMarkdownTrackerBackend Backend, TrackerConfig Config, WorkItemId Id,
        TrackerService Tracker, WorkflowActionService Actions)> WorkflowFixture(bool useQueue = true)
    {
        var config = WorkerConfig() with
        {
            DefaultPickFrom = "Automation",
            LocalMarkdown = new() { Statuses = ["Ideas", "Automation", "In Progress", "Done"] },
            Worker = new() { UseWorkerQueue = useQueue }
        };
        var backend = new LocalMarkdownTrackerBackend(new FakeIdentity(), clock);
        await backend.InitializeAsync(config, false, default);
        var item = await backend.CreateAsync(config, new CreateWorkItemOperation(new("Workflow", "Body", "Ideas", "P1"), false), default);
        var tracker = new TrackerService(new TrackerBackendRegistry([backend]));
        return (backend, config, item.Id, tracker, new(tracker));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Workflow_queue_and_send_back_share_policy_without_leaving_claims(bool useQueue)
    {
        var f = await WorkflowFixture(useQueue);
        var before = await f.Tracker.GetOperationalAsync(f.Config, f.Id, default);
        var queued = await f.Actions.ExecuteAsync(f.Config, f.Id, "queue", WorkflowActionService.Version(f.Config, before), default);
        Assert.Equal("applied", queued.Outcome);
        Assert.False(queued.StartsWorker);
        Assert.Equal("Automation", queued.After.Status);
        Assert.Equal(useQueue, queued.After.AutomaticExecutionAllowed);
        Assert.Equal(ClaimOwnershipState.Unclaimed, (await f.Backend.GetClaimOwnershipAsync(f.Config, f.Id, default)).State);
        var sentBack = await f.Actions.ExecuteAsync(f.Config, f.Id, "send-back", queued.StateVersion, default);
        Assert.Equal("Ideas", sentBack.After.Status);
        Assert.False(sentBack.After.AutomaticExecutionAllowed);
        Assert.Equal(ClaimOwnershipState.Unclaimed, (await f.Backend.GetClaimOwnershipAsync(f.Config, f.Id, default)).State);
    }

    [Fact]
    public async Task Workflow_rejects_stale_content_and_configuration_without_mutation()
    {
        var f = await WorkflowFixture();
        var observed = await f.Tracker.GetOperationalAsync(f.Config, f.Id, default);
        var version = WorkflowActionService.Version(f.Config, observed);
        var claim = await f.Backend.TryClaimAsync(f.Config, f.Id, AgentExecutionContext.Human, default);
        var handle = new ClaimHandle(AgentExecutionContext.Human, claim.ClaimToken);
        await f.Backend.UpdateAsync(f.Config, f.Id, new(new(Title: OptionalValue<string>.From("Changed"),
            Body: default, Status: default, Priority: default), false, ClaimHandle: handle), default);
        await f.Backend.ReleaseAsync(f.Config, f.Id, handle, false, DispatchStateOnRelease.Preserve, default);
        var before = TrackerContents();
        var error = await Assert.ThrowsAsync<TrackerException>(() => f.Actions.ExecuteAsync(f.Config, f.Id, "queue", version, default));
        Assert.Equal("ACTION_STATE_CHANGED", error.Code);
        observed = await f.Tracker.GetOperationalAsync(f.Config, f.Id, default);
        version = WorkflowActionService.Version(f.Config, observed);
        error = await Assert.ThrowsAsync<TrackerException>(() => f.Actions.ExecuteAsync(f.Config with { Worker = new() { UseWorkerQueue = false } },
            f.Id, "queue", version, default));
        Assert.Equal("ACTION_STATE_CHANGED", error.Code);
        Assert.Equal(before, TrackerContents());
    }

    [Fact]
    public async Task Workflow_rejects_claim_contention_wrong_action_and_invalid_transition()
    {
        var f = await WorkflowFixture();
        var claim = await f.Backend.TryClaimAsync(f.Config, f.Id, AgentExecutionContext.Human, default);
        var before = TrackerContents();
        var error = await Assert.ThrowsAsync<TrackerException>(() => f.Actions.ExecuteAsync(f.Config, f.Id, "queue", null, default));
        Assert.Equal("CLAIM_HELD", error.Code);
        Assert.Equal(before, TrackerContents());
        Assert.Equal(claim.ClaimantId, (await f.Backend.GetClaimOwnershipAsync(f.Config, f.Id, default)).ClaimantId);
        error = await Assert.ThrowsAsync<TrackerException>(() => f.Actions.ExecuteAsync(f.Config, f.Id, "queue; echo unsafe", null, default));
        Assert.Equal("ACTION_EXECUTION_UNSUPPORTED", error.Code);
        await f.Backend.ReleaseAsync(f.Config, f.Id, new(AgentExecutionContext.Human, claim.ClaimToken), false, DispatchStateOnRelease.Preserve, default);
        error = await Assert.ThrowsAsync<TrackerException>(() => f.Actions.ExecuteAsync(f.Config, f.Id, "send-back", null, default));
        Assert.Equal("WORKFLOW_STATE_INVALID", error.Code);
    }

    [Fact]
    public async Task Workflow_concurrent_queue_applies_once()
    {
        var f = await WorkflowFixture();
        var version = WorkflowActionService.Version(f.Config, await f.Tracker.GetOperationalAsync(f.Config, f.Id, default));
        async Task<string> Attempt()
        {
            try { return (await f.Actions.ExecuteAsync(f.Config, f.Id, "queue", version, default)).Outcome; }
            catch (TrackerException error) { return error.Code; }
        }
        var outcomes = await Task.WhenAll(Attempt(), Attempt());
        Assert.Single(outcomes, outcome => outcome == "applied");
        Assert.Single(outcomes, outcome => outcome == "WORKFLOW_STATE_INVALID");
        Assert.Equal(ClaimOwnershipState.Unclaimed, (await f.Backend.GetClaimOwnershipAsync(f.Config, f.Id, default)).State);
    }

    [Fact]
    public async Task Workflow_resume_preserves_session_and_fences_previous_claim()
    {
        var (backend, config, id, oldHandle) = await CreatePausedItemAsync();
        var tracker = new TrackerService(new TrackerBackendRegistry([backend]));
        var before = await tracker.GetOperationalAsync(config, id, default);
        var result = await new WorkflowActionService(tracker).ExecuteAsync(config, id, "resume",
            WorkflowActionService.Version(config, before), default);
        Assert.Equal(DispatchStates.Queued, result.After.DispatchState);
        Assert.Equal(before.Item.Status, result.After.Status);
        var after = await tracker.GetOperationalAsync(config, id, default);
        Assert.Equal(before.Session!.SessionId, after.Session!.SessionId);
        Assert.Equal(before.Session.WorkspacePath, after.Session.WorkspacePath);
        Assert.Equal(before.Session.Context, after.Session.Context);
        Assert.Equal(ClaimOwnershipState.Unclaimed, after.Claim.State);
        await Assert.ThrowsAsync<TrackerException>(() => backend.RenewClaimAsync(config, id, oldHandle, null, null, default));
        var error = await Assert.ThrowsAsync<TrackerException>(() => new WorkflowActionService(tracker).ExecuteAsync(config, id, "resume", null, default));
        Assert.Equal("WORKER_ITEM_NOT_PAUSED", error.Code);
    }
}
