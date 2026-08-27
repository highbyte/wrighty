using Highbyte.Wrighty.Claims;
using Highbyte.Wrighty.Models;

namespace Highbyte.Wrighty.UnitTests.Models;

public sealed class OperationalStatusesTests
{
    private const string PickFrom = "Todo";

    private static WorkItemClaimSummary Unclaimed => new(ClaimOwnershipState.Unclaimed);

    private static WorkItemClaimSummary UnclaimedWithAddress => new(
        ClaimOwnershipState.Unclaimed,
        "worker-a",
        Agent: "codex",
        SessionId: "session-1",
        WorkspacePath: "/tmp/ws");

    private static AgentSessionRecord CompleteSession => new(
        "codex", "session-1", "/tmp/ws", DateTimeOffset.UnixEpoch, true);

    private static string Resolve(
        WorkItemClaimSummary claim,
        AgentSessionRecord? session = null,
        string? dispatchState = null,
        bool automaticExecutionAllowed = false,
        string status = PickFrom) =>
        OperationalStatuses.Resolve(
            dispatchState, automaticExecutionAllowed, status, claim, session, PickFrom);

    [Fact]
    public void Needs_attention_takes_precedence_over_everything()
    {
        Assert.Equal(OperationalStatuses.NeedsAttention, Resolve(
            new WorkItemClaimSummary(ClaimOwnershipState.OwnedByCurrent, ClaimantKind: "agent"),
            CompleteSession,
            DispatchStates.NeedsAttention,
            automaticExecutionAllowed: true));
    }

    [Fact]
    public void Queued_requires_an_unclaimed_item()
    {
        Assert.Equal(OperationalStatuses.Queued,
            Resolve(Unclaimed, dispatchState: DispatchStates.Queued));
        Assert.Equal(OperationalStatuses.HumanEditing, Resolve(
            new WorkItemClaimSummary(ClaimOwnershipState.OwnedByCurrent, ClaimantKind: "human"),
            dispatchState: DispatchStates.Queued));
    }

    [Theory]
    [InlineData(DispatchStates.RetryScheduled, OperationalStatuses.RetryScheduled)]
    [InlineData(DispatchStates.HandoffQueued, OperationalStatuses.HandoffQueued)]
    public void Deferred_dispatch_states_require_an_unclaimed_item(
        string dispatchState,
        string expectedActivity)
    {
        Assert.Equal(expectedActivity, Resolve(Unclaimed, dispatchState: dispatchState));
        Assert.Equal(OperationalStatuses.AgentActive, Resolve(
            new WorkItemClaimSummary(ClaimOwnershipState.OwnedByCurrent, ClaimantKind: "agent"),
            dispatchState: dispatchState));
    }

    [Theory]
    [InlineData("agent", OperationalStatuses.AgentActive)]
    [InlineData("human", OperationalStatuses.HumanEditing)]
    [InlineData("automation", OperationalStatuses.AutomationActive)]
    [InlineData("unknown", OperationalStatuses.None)]
    public void Active_claims_resolve_by_claimant_kind(string kind, string expected)
    {
        Assert.Equal(expected, Resolve(
            new WorkItemClaimSummary(ClaimOwnershipState.HeldByOther, ClaimantKind: kind)));
    }

    [Fact]
    public void Agent_claim_is_preparing_until_the_process_has_started()
    {
        Assert.Equal(OperationalStatuses.WorkerPreparing, Resolve(
            new WorkItemClaimSummary(
                ClaimOwnershipState.OwnedByCurrent,
                Agent: "codex",
                ClaimantKind: "agent",
                ExecutionPhase: ClaimExecutionPhases.Preparing)));
        Assert.Equal(OperationalStatuses.AgentActive, Resolve(
            new WorkItemClaimSummary(
                ClaimOwnershipState.OwnedByCurrent,
                Agent: "codex",
                ClaimantKind: "agent",
                ExecutionPhase: ClaimExecutionPhases.Invoking)));
    }

    [Fact]
    public void Agent_claim_without_a_phase_remains_active_for_backward_compatibility()
    {
        Assert.Equal(OperationalStatuses.AgentActive, Resolve(
            new WorkItemClaimSummary(
                ClaimOwnershipState.HeldByOther,
                Agent: "claude",
                ClaimantKind: "agent")));
    }

    [Fact]
    public void Paused_session_resolves_from_a_complete_session_record()
    {
        Assert.Equal(OperationalStatuses.PausedSession, Resolve(Unclaimed, CompleteSession));
    }

    [Fact]
    public void Completed_requires_a_succeeded_outcome_at_the_finish_status()
    {
        var finished = CompleteSession with { Outcome = RunOutcome.Succeeded };
        Assert.Equal(OperationalStatuses.Completed, OperationalStatuses.Resolve(
            dispatchState: null, automaticExecutionAllowed: false, status: "Done",
            Unclaimed, finished, PickFrom, defaultFinishTo: "Done"));
    }

    [Fact]
    public void Completed_falls_back_to_paused_without_the_finish_status_or_outcome()
    {
        var finished = CompleteSession with { Outcome = RunOutcome.Succeeded };
        // Succeeded outcome but the item never reached the finish status: still resumable/paused.
        Assert.Equal(OperationalStatuses.PausedSession, OperationalStatuses.Resolve(
            null, false, "In Progress", Unclaimed, finished, PickFrom, "Done"));
        // No captured outcome (older record): preserves the pre-plan-023 paused label.
        Assert.Equal(OperationalStatuses.PausedSession, OperationalStatuses.Resolve(
            null, false, "Done", Unclaimed, CompleteSession, PickFrom, "Done"));
        // Finish status not supplied by the caller: cannot distinguish, stays paused.
        Assert.Equal(OperationalStatuses.PausedSession, Resolve(Unclaimed, finished));
    }

    [Fact]
    public void Failed_outcome_never_reads_as_completed()
    {
        var failed = CompleteSession with { Outcome = RunOutcome.Failed };
        Assert.Equal(OperationalStatuses.PausedSession, OperationalStatuses.Resolve(
            null, false, "Done", Unclaimed, failed, PickFrom, "Done"));
    }

    [Fact]
    public void Paused_session_resolves_from_a_claim_summary_address_without_a_session()
    {
        // Regression: the two pre-unification resolver overloads disagreed here — the
        // detail-based overload returned "none" for exactly this state.
        Assert.Equal(OperationalStatuses.PausedSession, Resolve(UnclaimedWithAddress));
        Assert.Equal(OperationalStatuses.PausedSession, OperationalStatuses.Resolve(
            new WorkItemSummary(new WorkItemId("local:1"), "Item", null, PickFrom, null),
            UnclaimedWithAddress,
            PickFrom));
    }

    [Fact]
    public void Ready_requires_eligibility_and_the_pick_status()
    {
        Assert.Equal(OperationalStatuses.Ready,
            Resolve(Unclaimed, automaticExecutionAllowed: true));
        Assert.Equal(OperationalStatuses.None,
            Resolve(Unclaimed, automaticExecutionAllowed: true, status: "In Progress"));
        Assert.Equal(OperationalStatuses.None, Resolve(Unclaimed));
    }

    [Fact]
    public void Detail_and_summary_overloads_agree()
    {
        var detail = new WorkItemDetail(
            new WorkItemId("local:1"), "Item", "Body", null, PickFrom,
            null, AutomaticExecutionAllowed: true);
        var summary = new WorkItemSummary(
            new WorkItemId("local:1"), "Item", null, PickFrom, null,
            AutomaticExecutionAllowed: true);

        Assert.Equal(
            OperationalStatuses.Resolve(detail, Unclaimed, null, PickFrom),
            OperationalStatuses.Resolve(summary, Unclaimed, PickFrom));
        Assert.Equal(
            OperationalStatuses.Resolve(detail, UnclaimedWithAddress, null, PickFrom),
            OperationalStatuses.Resolve(summary, UnclaimedWithAddress, PickFrom));
    }
}
