using Highbyte.Wrighty.Claims;
using Highbyte.Wrighty.Models;
using Highbyte.Wrighty.Web;

namespace Highbyte.Wrighty.UnitTests.Web;

public sealed class ItemOrganizationTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 21, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Board_query_ignores_invalid_values_and_normalizes_equivalent_filters()
    {
        var first = BoardListQuery.Parse(new BoardListInput
        {
            Sort = "updated:desc",
            ColumnSort = ["1:title:asc", "bad", "101:number:asc"],
            ClaimKind = ["AGENT", "invalid"],
            Agent = [" Codex "],
            Priority = [" P1 "],
            ClaimState = ["current"],
            UpdatedWithin = "7D"
        });
        var second = BoardListQuery.Parse(new BoardListInput
        {
            Sort = "UPDATED:DESC",
            ColumnSort = ["1:TITLE:ASC"],
            ClaimKind = ["agent"],
            Agent = ["codex"],
            Priority = ["p1"],
            ClaimState = ["CURRENT"],
            UpdatedWithin = "7d"
        });

        Assert.Equal(second.RevisionKey, first.RevisionKey);
        Assert.Equal(new ItemSort(ItemSortField.Updated, true), first.Sort);
        Assert.Equal(new ItemSort(ItemSortField.Title, false), first.SortForColumn(1));
        Assert.Equal(first.Sort, first.SortForColumn(2));
        Assert.Equal(["agent"], first.ClaimKinds);
    }

    [Fact]
    public void Board_query_filters_claim_agent_priority_state_and_recent_update()
    {
        var query = BoardListQuery.Parse(new BoardListInput
        {
            ClaimKind = ["agent"],
            Agent = ["codex"],
            Priority = ["p1"],
            ClaimState = ["current"],
            UpdatedWithin = "7d"
        });

        Assert.True(query.Matches(Card(
            "local:1", "P1", ClaimOwnershipState.OwnedByCurrent,
            "agent", "codex", Now.AddDays(-1)), Now));
        Assert.False(query.Matches(Card(
            "local:2", "P1", ClaimOwnershipState.HeldByOther,
            "agent", "codex", Now.AddDays(-1)), Now));
        Assert.False(query.Matches(Card(
            "local:3", "P1", ClaimOwnershipState.OwnedByCurrent,
            "agent", "claude", Now.AddDays(-1)), Now));
        Assert.False(query.Matches(Card(
            "local:4", "P1", ClaimOwnershipState.OwnedByCurrent,
            "agent", "codex", null), Now));
    }

    [Fact]
    public void Board_search_is_server_resolved_and_bounded_for_batch_scope()
    {
        var query = BoardListQuery.Parse(new BoardListInput { Q = "  LOCAL:1  " });

        Assert.True(query.HasFilters);
        Assert.False(query.HasStructuredFilters);
        Assert.Equal("LOCAL:1", query.Search);
        Assert.True(query.Matches(Card("local:1", "P1"), Now));
        Assert.False(query.Matches(Card("local:2", "P1"), Now));

        var oversized = BoardListQuery.Parse(new BoardListInput { Q = new string('x', 201) });
        Assert.Null(oversized.Search);
        Assert.False(oversized.HasFilters);
    }

    [Fact]
    public void Timestamp_sort_keeps_missing_values_last_in_both_directions()
    {
        var older = Card("local:2", updatedAt: Now.AddDays(-2));
        var newer = Card("local:3", updatedAt: Now.AddDays(-1));
        var missing = Card("local:1", updatedAt: null);

        var ascending = new[] { newer, missing, older };
        Array.Sort(ascending, new BoardCardComparer(
            new ItemSort(ItemSortField.Updated, false), ["P0", "P1"]));
        Assert.Equal(["local:2", "local:3", "local:1"], ascending.Select(card => card.Id));

        var descending = new[] { older, missing, newer };
        Array.Sort(descending, new BoardCardComparer(
            new ItemSort(ItemSortField.Updated, true), ["P0", "P1"]));
        Assert.Equal(["local:3", "local:2", "local:1"], descending.Select(card => card.Id));
    }

    [Fact]
    public void Default_sort_puts_live_execution_before_needs_attention()
    {
        var cards = new[]
        {
            Card("local:12", "P1"),
            Card("local:3", "P0"),
            Card("local:20", "P0", operationalStatus: OperationalStatuses.NeedsAttention),
            Card("local:30", "P1", operationalStatus: OperationalStatuses.AgentActive),
            Card("local:31", "P1", operationalStatus: OperationalStatuses.WorkerPreparing)
        };

        Array.Sort(cards, new BoardCardComparer(ItemSort.Default, ["P0", "P1"]));

        Assert.Equal(
            ["local:30", "local:31", "local:20", "local:3", "local:12"],
            cards.Select(card => card.Id));
    }

    [Fact]
    public void Operations_query_filters_requested_agent_and_only_applies_claims_locally()
    {
        var query = OperationsListQuery.Parse(new OperationsListInput
        {
            Sort = "operational:asc",
            Search = "retry",
            Agent = "codex",
            Priority = "P1",
            WorkflowStatus = "Todo",
            OperationalStatus = OperationalStatuses.RetryScheduled,
            Recovery = "absent",
            ContextState = "unknown",
            UpdatedWithin = "7d",
            ClaimKind = "human",
            ClaimState = "current"
        });
        var item = Operation(
            "local:7",
            "Retry payment",
            requestedAgent: "codex",
            updatedAt: Now.AddDays(-1),
            claimantKind: "agent",
            claimState: ClaimOwnershipState.HeldByOther);

        Assert.Equal(new ItemSort(ItemSortField.Operational, false), query.Sort);
        Assert.True(query.Matches(item, Now, localClaimsAvailable: false));
        Assert.False(query.Matches(item, Now, localClaimsAvailable: true));
        Assert.False(query.Matches(
            item with { RequestedAgent = "claude" }, Now, localClaimsAvailable: false));
    }

    [Fact]
    public void Operations_timestamp_sort_keeps_unknown_values_last()
    {
        var newer = Operation("local:2", "New", updatedAt: Now);
        var older = Operation("local:1", "Old", updatedAt: Now.AddDays(-1));
        var unknown = Operation("local:3", "Unknown");
        var values = new[] { older, unknown, newer };

        Array.Sort(values, new OperationsItemComparer(
            new ItemSort(ItemSortField.Updated, true), ["P0", "P1"]));

        Assert.Equal(["local:2", "local:1", "local:3"], values.Select(value => value.Id));
    }

    [Fact]
    public void Operations_default_sort_matches_the_board_live_execution_order()
    {
        var values = new[]
        {
            Operation("local:20", "Needs attention",
                operationalStatus: OperationalStatuses.NeedsAttention),
            Operation("local:31", "Worker preparing",
                operationalStatus: OperationalStatuses.WorkerPreparing),
            Operation("local:30", "Agent working",
                operationalStatus: OperationalStatuses.AgentActive)
        };

        Array.Sort(values, new OperationsItemComparer(ItemSort.Default, ["P0", "P1"]));

        Assert.Equal(["local:30", "local:31", "local:20"], values.Select(value => value.Id));
    }

    [Fact]
    public void Operations_requested_agent_sort_is_case_insensitive_and_keeps_missing_values_last()
    {
        var codex = Operation("local:3", "Codex", requestedAgent: "codex");
        var claude = Operation("local:2", "Claude", requestedAgent: "Claude");
        var missing = Operation("local:1", "Missing");

        var ascending = new[] { codex, missing, claude };
        Array.Sort(ascending, new OperationsItemComparer(
            new ItemSort(ItemSortField.Agent, false), ["P0", "P1"]));
        Assert.Equal(["local:2", "local:3", "local:1"], ascending.Select(value => value.Id));

        var descending = new[] { claude, missing, codex };
        Array.Sort(descending, new OperationsItemComparer(
            new ItemSort(ItemSortField.Agent, true), ["P0", "P1"]));
        Assert.Equal(["local:3", "local:2", "local:1"], descending.Select(value => value.Id));
    }

    private static BoardCardModel Card(
        string id,
        string? priority = null,
        ClaimOwnershipState claimState = ClaimOwnershipState.Unclaimed,
        string? claimantKind = null,
        string? agent = null,
        DateTimeOffset? updatedAt = null,
        string operationalStatus = OperationalStatuses.None) =>
        new(
            id,
            $"#{id.Split(':')[1]}",
            id,
            "Todo",
            priority,
            false,
            claimState,
            "Unclaimed",
            claimantKind,
            agent,
            true,
            null,
            null,
            operationalStatus,
            UpdatedAt: updatedAt,
            AgentKey: agent);

    private static OperationsItemView Operation(
        string id,
        string title,
        string? requestedAgent = null,
        DateTimeOffset? updatedAt = null,
        string? claimantKind = null,
        ClaimOwnershipState? claimState = null,
        string operationalStatus = OperationalStatuses.RetryScheduled) =>
        new(
            id,
            title,
            "Todo",
            "P1",
            DispatchStates.RetryScheduled,
            operationalStatus,
            null,
            null,
            RequestedAgent: requestedAgent,
            UpdatedAt: updatedAt,
            ClaimantKind: claimantKind,
            ClaimState: claimState);
}
