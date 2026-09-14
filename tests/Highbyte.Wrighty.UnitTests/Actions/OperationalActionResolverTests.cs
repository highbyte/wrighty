using Highbyte.Wrighty.Actions;
using Highbyte.Wrighty.Claims;
using Highbyte.Wrighty.Configuration;
using Highbyte.Wrighty.Models;
using Highbyte.Wrighty.Workers;

namespace Highbyte.Wrighty.UnitTests.Actions;

public sealed class OperationalActionResolverTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-14T10:00:00Z");
    private static readonly TrackerConfig Config = new()
    {
        Backend = "local-markdown",
        DefaultPickFrom = "Automation",
        DefaultPickTo = "Doing",
        DefaultFinishTo = "Complete",
        LocalMarkdown = new() { Statuses = ["Ideas", "Automation", "Doing", "Complete"] }
    };

    [Theory]
    [InlineData("Ideas", "queue", null)]
    [InlineData("Automation", "queue", "WORKFLOW_STATE_INVALID")]
    [InlineData("Doing", "queue", "WORKFLOW_STATE_INVALID")]
    [InlineData("Complete", "queue", "WORKFLOW_STATE_INVALID")]
    [InlineData("Automation", "send-back", null)]
    [InlineData("Ideas", "send-back", "WORKFLOW_STATE_INVALID")]
    public void Board_actions_use_configured_roles(string status, string action, string? code)
    {
        var result = Resolve(State(status));
        Assert.Equal(code, Find(result, action).UnavailableCode);
        Assert.Null(result.RecommendedAction);
        Assert.Contains("Ideas", Find(result, "send-back").Description);
    }

    [Fact]
    public void Queue_policy_disabled_does_not_promise_automatic_authorization()
    {
        var result = Resolve(State(), Config with { Worker = new() { UseWorkerQueue = false } });
        Assert.Contains("independent", Find(result, "queue").Description);
        Assert.DoesNotContain("revokes", Find(result, "send-back").Description);
    }

    [Fact]
    public void Missing_backlog_and_recovery_state_block_moves()
    {
        var config = Config with { LocalMarkdown = new() { Statuses = ["Automation", "Doing", "Complete"] } };
        Assert.Equal("STATUS_UNAVAILABLE", Find(Resolve(State("Automation"), config), "send-back").UnavailableCode);
        var state = State() with { Item = State().Item with { DispatchState = DispatchStates.RetryScheduled } };
        Assert.Equal("WORKER_RECOVERY_PENDING", Find(Resolve(state), "queue").UnavailableCode);
    }

    [Theory]
    [InlineData(ClaimOwnershipState.OwnedByCurrent)]
    [InlineData(ClaimOwnershipState.HeldByOther)]
    public void Active_claim_blocks_queue_without_takeover(ClaimOwnershipState ownership)
    {
        var state = State() with { Claim = new(ownership) };
        Assert.Equal("CLAIM_HELD", Find(Resolve(state), "queue").UnavailableCode);
        Assert.Equal("CLAIM_HELD", Find(Resolve(state), "clarify").UnavailableCode);
    }

    [Fact]
    public void Archived_item_retains_review_but_blocks_mutations()
    {
        var state = State() with { Item = State().Item with { Archived = true } };
        var result = Resolve(state);
        Assert.Equal("available", Find(result, "open-item").Availability);
        Assert.Equal("ITEM_ARCHIVED", Find(result, "queue").UnavailableCode);
        Assert.Equal("ITEM_ARCHIVED", Find(result, "clarify").UnavailableCode);
        Assert.Equal("WORKER_ITEM_TERMINAL", Find(result, "continue-worker").UnavailableCode);
    }

    [Fact]
    public void Attention_recommends_clarification_and_exposes_distinct_resume_actions()
    {
        var state = Paused() with { OperationalStatus = OperationalStatuses.NeedsAttention };
        var result = Resolve(state);
        Assert.Equal("clarify", result.RecommendedAction);
        Assert.True(result.Actions[0].Recommended);
        Assert.Equal("available", Find(result, "resume").Availability);
        Assert.Empty(Find(result, "resume").Commands);
        Assert.True(Find(result, "continue-worker").StartsProcess);
        Assert.False(Find(result, "resume").StartsProcess);
        Assert.Equal("required", Find(result, "resume").Confirmation);
    }

    [Fact]
    public void Resume_needs_execution_policy_and_active_work_status()
    {
        var state = Paused() with { OperationalStatus = OperationalStatuses.NeedsAttention };
        state = state with { Item = state.Item with { AutomaticExecutionAllowed = false } };
        Assert.Equal("WORKER_ITEM_INELIGIBLE", Find(Resolve(state), "resume").UnavailableCode);
        state = state with { Item = state.Item with { AutomaticExecutionAllowed = true, Status = "Ideas" } };
        Assert.Equal("WORKER_ITEM_INELIGIBLE", Find(Resolve(state), "resume").UnavailableCode);
    }

    [Theory]
    [InlineData("missing", "RESUME_ADDRESS_UNAVAILABLE")]
    [InlineData("incomplete", "RESUME_ADDRESS_UNAVAILABLE")]
    [InlineData("remote", "RESUME_ADDRESS_NOT_LOCAL")]
    [InlineData("worktree", "RESUME_WORKTREE_ABSENT")]
    [InlineData("other-claim", "CLAIM_NOT_OWNER")]
    [InlineData("active", "CLAIM_HELD")]
    public void Unusable_sessions_do_not_advertise_launch(string scenario, string code)
    {
        var state = Paused();
        state = scenario switch
        {
            "missing" => state with { Session = null },
            "incomplete" => state with { Session = state.Session! with { SessionId = null } },
            "remote" => state with { Session = state.Session! with { FromCurrentInstallation = false } },
            "other-claim" => state with { Claim = new(ClaimOwnershipState.HeldByOther) },
            "active" => state with { OperationalStatus = OperationalStatuses.AgentActive },
            _ => state
        };
        var result = Resolve(state, workspaceExists: scenario != "worktree");
        Assert.Equal(code, Find(result, "resume-session").UnavailableCode);
        Assert.Equal(code, Find(result, "continue-worker").UnavailableCode);
        Assert.Equal("available", Find(result, "open-item").Availability);
    }

    [Fact]
    public void Missing_admission_fails_closed_without_hiding_review()
    {
        var result = OperationalActionResolver.Resolve(new(Config, Paused(), Now, true));
        Assert.Equal("ACTION_STATE_UNVERIFIED", Find(result, "continue-worker").UnavailableCode);
        Assert.Equal("ACTION_STATE_UNVERIFIED", Find(result, "resume-session").UnavailableCode);
        Assert.Equal("available", Find(result, "open-item").Availability);
    }

    [Theory]
    [InlineData("AGENT_DISABLED")]
    [InlineData("AGENT_NOT_INSTALLED")]
    [InlineData("CONTEXT_NOT_APPROVED")]
    public void Admission_refusals_are_preserved(string code)
    {
        var result = OperationalActionResolver.Resolve(new(Config, Paused(), Now, true,
            new(code, "Blocked by authoritative preflight."), ActionAvailability.Available));
        Assert.Equal(code, Find(result, "continue-worker").UnavailableCode);
        Assert.Equal("available", Find(result, "resume-session").Availability);
    }

    [Theory]
    [InlineData(OperationalStatuses.RetryScheduled, "retry-now")]
    [InlineData(OperationalStatuses.HandoffQueued, "continue-worker")]
    public void Deferred_work_never_recommends_overriding_the_schedule(string status, string action)
    {
        var result = Resolve(Paused() with { OperationalStatus = status });
        Assert.Null(result.RecommendedAction);
        Assert.Equal("available", Find(result, action).Availability);
        Assert.All(result.Actions, value => Assert.Equal("manual-only", value.Execution));
        Assert.Equal(result.Actions.Count, result.Actions.Select(value => value.Name).Distinct().Count());
    }

    [Fact]
    public void GitHub_uses_links_and_preserves_clarification_guidance()
    {
        var state = Paused() with { OperationalStatus = OperationalStatuses.NeedsAttention };
        state = state with { Item = state.Item with { Url = "https://github.com/o/r/issues/42" } };
        var result = Resolve(state, Config with { Backend = "github" });
        Assert.Equal("answer-on-issue", result.RecommendedAction);
        var answer = Find(result, "answer-on-issue");
        Assert.Equal(state.Item.Url, answer.Url);
        Assert.Empty(answer.Commands);
        Assert.Contains("new comment", answer.Description);
        Assert.Equal("NOT_SUPPORTED", Find(result, "queue").UnavailableCode);
    }

    [Fact]
    public void Discovery_and_handover_share_clarification_and_retry_guidance()
    {
        var state = Paused() with { OperationalStatus = OperationalStatuses.NeedsAttention };
        var guidance = OperationalActionGuidance.NeedsAttentionActions(state.Item.Id, "claude",
            OperatorSurface.For(Config, null)).Single(value => value.Name == "clarify");
        var action = Find(Resolve(state), "clarify");
        Assert.Equal(guidance.Description, action.Description);
        Assert.Equal(guidance.Commands, action.Commands);
        var retry = Find(Resolve(state with { OperationalStatus = OperationalStatuses.RetryScheduled }), "retry-now");
        Assert.Equal(OperationalActionGuidance.RetryNow(state.Item.Id).Commands, retry.Commands);
    }

    private static OperationalActionDiscovery Resolve(WorkItemOperationalState state,
        TrackerConfig? config = null, bool workspaceExists = true) => OperationalActionResolver.Resolve(
            new(config ?? Config, state, Now, workspaceExists, ActionAvailability.Available, ActionAvailability.Available));
    private static OperationalAction Find(OperationalActionDiscovery result, string name) =>
        Assert.Single(result.Actions, action => action.Name == name);
    private static WorkItemOperationalState State(string status = "Ideas") => new(
        new(new("local:42"), "Work", "Requirements", null, status, "P1", AutomaticExecutionAllowed: true),
        new(ClaimOwnershipState.Unclaimed), null, OperationalStatuses.None);
    private static WorkItemOperationalState Paused() => State("Doing") with
    {
        OperationalStatus = OperationalStatuses.PausedSession,
        Session = new("claude", "session-42", "/workspace", Now, true)
    };
}
