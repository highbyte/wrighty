using Highbyte.Wrighty.AgentContext;
using Highbyte.Wrighty.Addressing;
using Highbyte.Wrighty.Backends;
using Highbyte.Wrighty.Claims;
using Highbyte.Wrighty.Configuration;
using Highbyte.Wrighty.Errors;
using Highbyte.Wrighty.Models;

namespace Highbyte.Wrighty.UnitTests.Backends;

/// <summary>
/// Covers the default ITrackerBackend operational-snapshot composition used by backends that
/// cannot produce item, claim, and session from one underlying snapshot.
/// </summary>
public sealed class OperationalSnapshotDefaultsTests
{
    private static readonly TrackerConfig Config = new()
    {
        Backend = "fake"
    };

    [Fact]
    public async Task Default_single_read_composes_item_claim_and_session()
    {
        ITrackerBackend backend = new ComposingFakeBackend();

        var snapshot = await backend.GetOperationalAsync(
            Config, new WorkItemId("fake:1"), CancellationToken.None);

        Assert.NotNull(snapshot);
        Assert.Equal("First", snapshot!.Item.Title);
        Assert.Equal(ClaimOwnershipState.OwnedByCurrent, snapshot.Claim.State);
        Assert.Equal("agent:one", snapshot.Claim.ClaimantId);
        Assert.Equal("session-one", snapshot.Session?.SessionId);

        Assert.Null(await backend.GetOperationalAsync(
            Config, new WorkItemId("fake:9"), CancellationToken.None));
    }

    [Fact]
    public async Task Default_list_read_prefers_the_listed_summary_fields()
    {
        ITrackerBackend backend = new ComposingFakeBackend();

        var snapshots = await backend.ListOperationalAsync(
            Config, new ListWorkItemsRequest(null, null), CancellationToken.None);

        var snapshot = Assert.Single(snapshots);
        Assert.Equal("First from listing", snapshot.Item.Title);
        Assert.Equal("In Progress", snapshot.Item.Status);
        Assert.Equal("P0", snapshot.Item.Priority);
        Assert.Equal("Body", snapshot.Item.Body);
        Assert.Equal("agent:one", snapshot.Claim.ClaimantId);
    }

    private sealed class ComposingFakeBackend : ITrackerBackend
    {
        public string Name => "fake";

        public IWorkItemAddressResolver AddressResolver => throw new NotSupportedException();

        public Task<IReadOnlyList<WorkItemSummary>> ListAsync(
            TrackerConfig config, ListWorkItemsRequest request, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<WorkItemSummary>>(
            [
                new WorkItemSummary(new WorkItemId("fake:1"), "First from listing", null, "In Progress", "P0"),
                new WorkItemSummary(new WorkItemId("fake:9"), "Deleted between list and get", null, "Todo", null)
            ]);

        public Task<WorkItemDetail?> GetAsync(
            TrackerConfig config, WorkItemId id, CancellationToken cancellationToken) =>
            Task.FromResult(id.Value == "fake:1"
                ? new WorkItemDetail(id, "First", "Body", null, "Todo", null)
                : null);

        public Task<ClaimOwnershipResult> GetClaimOwnershipAsync(
            TrackerConfig config, WorkItemId id, CancellationToken cancellationToken) =>
            Task.FromResult(new ClaimOwnershipResult(
                ClaimOwnershipState.OwnedByCurrent,
                "worker-a",
                DateTimeOffset.UnixEpoch,
                "agent:one",
                "codex",
                "session-one",
                "agent",
                true,
                "/tmp/ws"));

        public Task<AgentSessionRecord?> GetAgentSessionAsync(
            TrackerConfig config, WorkItemId id, CancellationToken cancellationToken) =>
            Task.FromResult<AgentSessionRecord?>(new AgentSessionRecord(
                "codex", "session-one", "/tmp/ws", DateTimeOffset.UnixEpoch, true));

        public Task<BackendInitializationResult> InitializeAsync(
            TrackerConfig config, bool checkOnly, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<CreateWorkItemResult> CreateAsync(
            TrackerConfig config, CreateWorkItemOperation operation, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<UpdateWorkItemResult> UpdateAsync(
            TrackerConfig config, WorkItemId id, UpdateWorkItemOperation operation,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ClaimResult> TryClaimAsync(
            TrackerConfig config, WorkItemId id, AgentExecutionContext agentContext,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ClaimResult> TryClaimAsync(
            TrackerConfig config, WorkItemId id, AgentExecutionContext agentExecutionContext,
            CancellationToken cancellationToken, string? expectedClaimToken) =>
            throw new NotSupportedException();

        public Task<ClaimResult> TakeoverAsync(
            TrackerConfig config, WorkItemId id, AgentExecutionContext claimantContext,
            string? currentClaimToken, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task ReleaseAsync(
            TrackerConfig config, WorkItemId id, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task ReleaseAsync(
            TrackerConfig config, WorkItemId id, ClaimHandle claimHandle, bool overrideClaimant,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ArchiveWorkItemResult> ArchiveAsync(
            TrackerConfig config, WorkItemId id, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ArchiveWorkItemResult> ArchiveAsync(
            TrackerConfig config, WorkItemId id, ClaimHandle claimHandle,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ArchiveWorkItemResult> UnarchiveAsync(
            TrackerConfig config, WorkItemId id, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
