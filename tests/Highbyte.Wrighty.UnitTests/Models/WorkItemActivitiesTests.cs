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
    [InlineData(WorkerDispatchStates.RetryScheduled, WorkItemActivities.RetryScheduled)]
    [InlineData(WorkerDispatchStates.HandoffQueued, WorkItemActivities.HandoffQueued)]
    public void Deferred_dispatch_states_require_an_unclaimed_item(
        string workerState,
        string expectedActivity)
    {
        Assert.Equal(expectedActivity, Resolve(Unclaimed, workerState: workerState));
        Assert.Equal(WorkItemActivities.AgentActive, Resolve(
            new WorkItemClaimSummary(ClaimOwnershipState.OwnedByCurrent, ClaimantKind: "agent"),
            workerState: workerState));
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
    public void Completed_requires_a_succeeded_outcome_at_the_finish_status()
    {
        var finished = CompleteSession with { Outcome = RunOutcome.Succeeded };
        Assert.Equal(WorkItemActivities.Completed, WorkItemActivities.Resolve(
            workerState: null, automationEligible: false, status: "Done",
            Unclaimed, finished, PickFrom, defaultFinishTo: "Done"));
    }

    [Fact]
    public void Completed_falls_back_to_paused_without_the_finish_status_or_outcome()
    {
        var finished = CompleteSession with { Outcome = RunOutcome.Succeeded };
        // Succeeded outcome but the item never reached the finish status: still resumable/paused.
        Assert.Equal(WorkItemActivities.PausedSession, WorkItemActivities.Resolve(
            null, false, "In Progress", Unclaimed, finished, PickFrom, "Done"));
        // No captured outcome (older record): preserves the pre-plan-023 paused label.
        Assert.Equal(WorkItemActivities.PausedSession, WorkItemActivities.Resolve(
            null, false, "Done", Unclaimed, CompleteSession, PickFrom, "Done"));
        // Finish status not supplied by the caller: cannot distinguish, stays paused.
        Assert.Equal(WorkItemActivities.PausedSession, Resolve(Unclaimed, finished));
    }

    [Fact]
    public void Failed_outcome_never_reads_as_completed()
    {
        var failed = CompleteSession with { Outcome = RunOutcome.Failed };
        Assert.Equal(WorkItemActivities.PausedSession, WorkItemActivities.Resolve(
            null, false, "Done", Unclaimed, failed, PickFrom, "Done"));
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
