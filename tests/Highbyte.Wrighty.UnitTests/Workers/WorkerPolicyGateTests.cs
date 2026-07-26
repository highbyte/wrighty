using System.Text.Json;
using Highbyte.Wrighty.Models;
using Highbyte.Wrighty.Workers;

namespace Highbyte.Wrighty.UnitTests.Workers;

/// <summary>
/// The single policy evaluation shared by the pre-claim candidate scan and the post-claim launch
/// preflight. These cover the filter matching and refusal wording directly, because both callers
/// depend on them agreeing and a divergence would let an item be admitted by one path under rules
/// the other would refuse.
/// </summary>
public class WorkerPolicyGateTests
{
    private static WorkItemDetail Item(
        string? status = "Todo",
        string? priority = "P1",
        bool automatic = true,
        string? agentPolicy = "claude",
        string? dispatchState = null,
        IReadOnlyList<string>? labels = null,
        IReadOnlyDictionary<string, JsonElement>? fields = null) =>
        new(new WorkItemId("local:1"), "Title", "Body", Url: null, status, priority,
            Fields: fields, AutomaticExecutionAllowed: automatic, AgentPolicy: agentPolicy,
            Labels: labels, DispatchState: dispatchState);

    private static WorkerOptions Options(IReadOnlyDictionary<string, string>? filters = null) =>
        new(null, true, null, WorkspaceMode.Current, filters ?? new Dictionary<string, string>(),
            null, TimeSpan.FromMinutes(30), FencedAction.Kill, null, "agent", false, false);

    private static WorkerPolicyDecision Evaluate(
        WorkItemDetail detail,
        IReadOnlyDictionary<string, string>? filters = null) =>
        WorkerPolicyGate.Evaluate(detail, Options(filters), null, agent => agent == "claude");

    private static IReadOnlyDictionary<string, JsonElement> Fields(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;

    [Fact]
    public void EligibleItemResolvesItsAgent()
    {
        var decision = Evaluate(Item());
        Assert.True(decision.Eligible);
        Assert.Equal("claude", decision.Agent);
    }

    [Fact]
    public void DispatchStateIsHandledByTheQueuedPathNotTheFreshPath()
    {
        var decision = Evaluate(Item(dispatchState: DispatchStates.RetryScheduled));
        Assert.Equal(WorkerPolicyReason.PausedOrQueued, decision.Reason);
        Assert.Null(decision.Agent);
    }

    [Fact]
    public void ManualExecutionIsRefusedBeforeAnyAgentResolution()
    {
        var decision = Evaluate(Item(automatic: false, agentPolicy: null));
        Assert.Equal(WorkerPolicyReason.ExecutionNotAutomatic, decision.Reason);
    }

    [Fact]
    public void AnUnsupportedAgentDoesNotResolve()
    {
        var decision = Evaluate(Item(agentPolicy: "not-a-vendor"));
        Assert.Equal(WorkerPolicyReason.UnresolvedAgent, decision.Reason);
    }

    [Fact]
    public void AnItemWithNoAgentAnywhereDoesNotResolve()
    {
        var decision = Evaluate(Item(agentPolicy: null));
        Assert.Equal(WorkerPolicyReason.UnresolvedAgent, decision.Reason);
    }

    [Theory]
    [InlineData("status", "Todo", true)]
    [InlineData("status", "Done", false)]
    [InlineData("priority", "P1", true)]
    [InlineData("priority", "P0", false)]
    [InlineData("agent", "claude", true)]
    [InlineData("agent", "codex", false)]
    public void BuiltInFilterKeysMatchTheirItemField(string key, string value, bool expected)
    {
        var decision = Evaluate(Item(), new Dictionary<string, string> { [key] = value });
        Assert.Equal(expected, decision.Eligible);
        if (!expected)
            Assert.Equal(WorkerPolicyReason.FilteredOut, decision.Reason);
    }

    [Fact]
    public void FilterMatchingIsCaseInsensitive()
    {
        Assert.True(Evaluate(Item(), new Dictionary<string, string> { ["STATUS"] = "todo" })
            .Eligible);
    }

    [Fact]
    public void LabelFilterMatchesAnyLabel()
    {
        var item = Item(labels: ["urgent", "backend"]);
        Assert.True(WorkerPolicyGate.MatchesFilters(
            item, new Dictionary<string, string> { ["label"] = "backend" }));
        Assert.False(WorkerPolicyGate.MatchesFilters(
            item, new Dictionary<string, string> { ["label"] = "frontend" }));
    }

    [Fact]
    public void LabelFilterDoesNotMatchWhenTheItemHasNoLabels()
    {
        Assert.False(WorkerPolicyGate.MatchesFilters(
            Item(), new Dictionary<string, string> { ["label"] = "urgent" }));
    }

    [Theory]
    [InlineData("""{"team":"platform"}""", "team", "platform", true)]
    [InlineData("""{"team":"platform"}""", "team", "infra", false)]
    [InlineData("""{"blocked":true}""", "blocked", "true", true)]
    [InlineData("""{"blocked":false}""", "blocked", "false", true)]
    [InlineData("""{"blocked":true}""", "blocked", "false", false)]
    [InlineData("""{"points":5}""", "points", "5", true)]
    [InlineData("""{"points":5}""", "points", "8", false)]
    public void CustomFieldFiltersCompareScalarValues(
        string json, string key, string value, bool expected) =>
        Assert.Equal(expected, WorkerPolicyGate.MatchesFilters(
            Item(fields: Fields(json)), new Dictionary<string, string> { [key] = value }));

    [Fact]
    public void ANonScalarCustomFieldNeverMatches() =>
        Assert.False(WorkerPolicyGate.MatchesFilters(
            Item(fields: Fields("""{"tags":["a","b"]}""")),
            new Dictionary<string, string> { ["tags"] = "a" }));

    [Fact]
    public void AnUnknownFilterKeyNeverMatches() =>
        Assert.False(WorkerPolicyGate.MatchesFilters(
            Item(), new Dictionary<string, string> { ["nope"] = "value" }));

    [Fact]
    public void EveryFilterMustMatch()
    {
        var filters = new Dictionary<string, string> { ["status"] = "Todo", ["priority"] = "P0" };
        Assert.False(WorkerPolicyGate.MatchesFilters(Item(), filters));
    }

    [Fact]
    public void NoFiltersMatchesEverything() =>
        Assert.True(WorkerPolicyGate.MatchesFilters(Item(), new Dictionary<string, string>()));

    [Theory]
    [InlineData(WorkerPolicyReason.Eligible)]
    [InlineData(WorkerPolicyReason.PausedOrQueued)]
    [InlineData(WorkerPolicyReason.ExecutionNotAutomatic)]
    [InlineData(WorkerPolicyReason.FilteredOut)]
    [InlineData(WorkerPolicyReason.UnresolvedAgent)]
    public void EveryReasonHasALowercaseFragmentCallersCanCompose(WorkerPolicyReason reason)
    {
        var described = WorkerPolicyGate.Describe(reason);
        Assert.NotEmpty(described);
        // Composed after a caller's lead-in ("... changed after claim: <fragment>."), so it must
        // not start a sentence or carry its own terminator.
        Assert.False(char.IsUpper(described[0]));
        Assert.DoesNotContain('.', described);
    }

    [Fact]
    public void AnUnknownReasonStillDescribesARefusal() =>
        Assert.Equal("authoritative worker policy refused this run",
            WorkerPolicyGate.Describe((WorkerPolicyReason)999));
}
