using Highbyte.Wrighty.Claims;
using Highbyte.Wrighty.Models;

namespace Highbyte.Wrighty.UnitTests.Models;

public sealed class WorkItemActivitiesTests
{
    private const string PickFrom = "Todo";

    private static WorkItemClaimSummary Unclaimed => new(ClaimOwnershipState.Unclaimed);

    private static WorkItemClaimSummary UnclaimedWithAddress => new(
        ClaimOwnershipState.Unclaimed,
        "worker-a",
        AgentType: "codex",
        SessionId: "session-1",
        WorkspacePath: "/tmp/ws");

    private static AgentSessionRecord CompleteSession => new(
        "codex", "session-1", "/tmp/ws", DateTimeOffset.UnixEpoch, true);

    private static string Resolve(
        WorkItemClaimSummary claim,
        AgentSessionRecord? session = null,
        string? workerState = null,
        bool automationEligible = false,
        string status = PickFrom) =>
        WorkItemActivities.Resolve(
            workerState, automationEligible, status, claim, session, PickFrom);

    [Fact]
    public void Needs_attention_takes_precedence_over_everything()
    {
        Assert.Equal(WorkItemActivities.NeedsAttention, Resolve(
            new WorkItemClaimSummary(ClaimOwnershipState.OwnedByCurrent, ClaimantKind: "agent"),
            CompleteSession,
            WorkerDispatchStates.NeedsAttention,
            automationEligible: true));
    }

    [Fact]
    public void Queued_requires_an_unclaimed_item()
    {
        Assert.Equal(WorkItemActivities.Queued,
            Resolve(Unclaimed, workerState: WorkerDispatchStates.Queued));
        Assert.Equal(WorkItemActivities.HumanEditing, Resolve(
            new WorkItemClaimSummary(ClaimOwnershipState.OwnedByCurrent, ClaimantKind: "human"),
            workerState: WorkerDispatchStates.Queued));
    }

    [Theory]
    [InlineData("agent", WorkItemActivities.AgentActive)]
    [InlineData("human", WorkItemActivities.HumanEditing)]
    [InlineData("automation", WorkItemActivities.AutomationActive)]
    [InlineData("unknown", WorkItemActivities.None)]
    public void Active_claims_resolve_by_claimant_kind(string kind, string expected)
    {
        Assert.Equal(expected, Resolve(
            new WorkItemClaimSummary(ClaimOwnershipState.HeldByOther, ClaimantKind: kind)));
    }

    [Fact]
    public void Paused_session_resolves_from_a_complete_session_record()
    {
        Assert.Equal(WorkItemActivities.PausedSession, Resolve(Unclaimed, CompleteSession));
    }

    [Fact]
    public void Paused_session_resolves_from_a_claim_summary_address_without_a_session()
    {
        // Regression: the two pre-unification resolver overloads disagreed here — the
        // detail-based overload returned "none" for exactly this state.
        Assert.Equal(WorkItemActivities.PausedSession, Resolve(UnclaimedWithAddress));
        Assert.Equal(WorkItemActivities.PausedSession, WorkItemActivities.Resolve(
            new WorkItemSummary(new WorkItemId("local:1"), "Item", null, PickFrom, null),
            UnclaimedWithAddress,
            PickFrom));
    }

    [Fact]
    public void Ready_requires_eligibility_and_the_pick_status()
    {
        Assert.Equal(WorkItemActivities.Ready,
            Resolve(Unclaimed, automationEligible: true));
        Assert.Equal(WorkItemActivities.None,
            Resolve(Unclaimed, automationEligible: true, status: "In Progress"));
        Assert.Equal(WorkItemActivities.None, Resolve(Unclaimed));
    }

    [Fact]
    public void Detail_and_summary_overloads_agree()
    {
        var detail = new WorkItemDetail(
            new WorkItemId("local:1"), "Item", "Body", null, PickFrom,
            null, AutomationEligible: true);
        var summary = new WorkItemSummary(
            new WorkItemId("local:1"), "Item", null, PickFrom, null,
            AutomationEligible: true);

        Assert.Equal(
            WorkItemActivities.Resolve(detail, Unclaimed, null, PickFrom),
            WorkItemActivities.Resolve(summary, Unclaimed, PickFrom));
        Assert.Equal(
            WorkItemActivities.Resolve(detail, UnclaimedWithAddress, null, PickFrom),
            WorkItemActivities.Resolve(summary, UnclaimedWithAddress, PickFrom));
    }
}
