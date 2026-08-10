using System.Text.Json;
using Highbyte.Wrighty.Workers;

namespace Highbyte.Wrighty.UnitTests.Workers;

/// <summary>
/// Discovery exists to make a configuration choice honest, so its failure modes matter more than
/// its happy path: a probe that reported "no models" when it meant "I could not ask" would move an
/// operator from an informed choice to a wrong one.
///
/// The payload below is trimmed from a real <c>codex app-server</c> <c>model/list</c> reply
/// (codex-cli 0.145.0, 2026-08-10), keeping the fields this adapter reads plus the ones it must
/// ignore, so the shape under test is the vendor's rather than one invented to pass.
/// </summary>
public sealed class CodexModelDiscoveryTests
{
    private const string RealReply = """
        {"id":2,"result":{"data":[
          {"id":"gpt-5.6-sol","model":"gpt-5.6-sol","displayName":"GPT-5.6-Sol",
           "description":"Latest frontier agentic coding model.","hidden":false,
           "supportedReasoningEfforts":[
             {"reasoningEffort":"low","description":"Fast"},
             {"reasoningEffort":"medium","description":"Balanced"},
             {"reasoningEffort":"high","description":"Deeper"},
             {"reasoningEffort":"xhigh","description":"Extra"},
             {"reasoningEffort":"max","description":"Maximum"},
             {"reasoningEffort":"ultra","description":"Maximum with delegation"}],
           "defaultReasoningEffort":"low","isDefault":true},
          {"id":"gpt-5.4","model":"gpt-5.4","displayName":"GPT-5.4","hidden":false,
           "supportedReasoningEfforts":[
             {"reasoningEffort":"low"},{"reasoningEffort":"medium"},
             {"reasoningEffort":"high"},{"reasoningEffort":"xhigh"}],
           "defaultReasoningEffort":"medium","isDefault":false},
          {"id":"gpt-4-retired","model":"gpt-4-retired","displayName":"Retired","hidden":true,
           "supportedReasoningEfforts":[],"defaultReasoningEffort":null}
        ]}}
        """;

    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-08-10T09:00:00Z");

    private static Task<AgentModelCatalog> DiscoverAsync(string? reply, ModelDiscoveryFailure failure) =>
        new CodexModelDiscovery(new StubProbe(reply, failure), () => Now)
            .DiscoverAsync(CancellationToken.None);

    [Fact]
    public async Task Every_model_reports_the_efforts_codex_enumerates_for_it()
    {
        var catalog = await DiscoverAsync(RealReply, ModelDiscoveryFailure.None);

        Assert.True(catalog.Succeeded);
        Assert.Equal("codex", catalog.Agent);
        Assert.Equal(Now, catalog.DiscoveredAt);

        var sol = catalog.Find("gpt-5.6-sol");
        Assert.Equal(EffortSupport.Yes, sol!.Effort);
        Assert.Equal(["low", "medium", "high", "xhigh", "max", "ultra"], sol.Efforts);
        // The vendor's own default, which adopting any profile silently overrides. Recorded so an
        // operator can see what they are giving up.
        Assert.Equal("low", sol.DefaultEffort);
    }

    [Fact]
    public async Task Effort_validity_differs_between_models_of_one_vendor()
    {
        // The finding this whole plan rests on: a per-vendor effort set cannot be correct, because
        // 'ultra' works on the GPT-5.6 family and nowhere else.
        var catalog = await DiscoverAsync(RealReply, ModelDiscoveryFailure.None);

        Assert.False(catalog.Find("gpt-5.6-sol")!.Rejects("ultra"));
        Assert.True(catalog.Find("gpt-5.4")!.Rejects("ultra"));
        Assert.False(catalog.Find("gpt-5.4")!.Rejects("xhigh"));
    }

    [Fact]
    public async Task A_hidden_model_is_not_offered()
    {
        // Codex hides retired and internal models. Offering one would let an operator pin a model
        // their own CLI declines to show them.
        var catalog = await DiscoverAsync(RealReply, ModelDiscoveryFailure.None);

        Assert.Null(catalog.Find("gpt-4-retired"));
        Assert.Equal(2, catalog.Models.Count);
    }

    [Fact]
    public async Task The_model_codex_would_use_by_itself_is_reported()
    {
        var catalog = await DiscoverAsync(RealReply, ModelDiscoveryFailure.None);

        Assert.Equal("gpt-5.6-sol", catalog.CurrentModelId);
    }

    [Theory]
    [InlineData(ModelDiscoveryFailure.NotInstalled)]
    [InlineData(ModelDiscoveryFailure.TimedOut)]
    [InlineData(ModelDiscoveryFailure.NotAuthenticated)]
    [InlineData(ModelDiscoveryFailure.Unavailable)]
    public async Task An_unreachable_vendor_yields_a_reason_rather_than_an_empty_answer(
        ModelDiscoveryFailure failure)
    {
        // The distinction that matters: "I could not ask" must never be presentable as "there are
        // no models", because the second would justify refusing a mapping the operator can see is
        // valid.
        var catalog = await DiscoverAsync(null, failure);

        Assert.False(catalog.Succeeded);
        Assert.Equal(failure, catalog.Failure);
        Assert.Empty(catalog.Models);
        Assert.Equal(Now, catalog.DiscoveredAt);
    }

    [Theory]
    // A JSON-RPC error reply, an unexpected envelope, and a shape change — all the same to a caller.
    [InlineData("""{"id":2,"error":{"code":-32601,"message":"Method not found"}}""")]
    [InlineData("""{"id":2,"result":{"models":[]}}""")]
    [InlineData("""{"id":2,"result":{"data":"not-an-array"}}""")]
    public async Task A_response_shape_this_adapter_does_not_know_is_unrecognized_not_a_crash(
        string reply)
    {
        // app-server is marked experimental by codex, so this is an expected eventuality rather
        // than a defensive flourish.
        var catalog = await DiscoverAsync(reply, ModelDiscoveryFailure.None);

        Assert.Equal(ModelDiscoveryFailure.Unrecognized, catalog.Failure);
        Assert.Empty(catalog.Models);
    }

    [Fact]
    public async Task An_entry_missing_its_identifier_is_skipped_without_losing_the_rest()
    {
        var catalog = await DiscoverAsync("""
            {"id":2,"result":{"data":[
              {"displayName":"No identifier"},
              {"id":"gpt-5.4","model":"gpt-5.4","supportedReasoningEfforts":[{"reasoningEffort":"low"}]}
            ]}}
            """, ModelDiscoveryFailure.None);

        Assert.True(catalog.Succeeded);
        Assert.Equal("gpt-5.4", Assert.Single(catalog.Models).Id);
    }

    [Fact]
    public async Task A_mapping_pinned_to_a_resolved_identifier_still_finds_its_model()
    {
        var catalog = await DiscoverAsync("""
            {"id":2,"result":{"data":[
              {"id":"latest","model":"gpt-5.6-sol","supportedReasoningEfforts":[{"reasoningEffort":"low"}]}
            ]}}
            """, ModelDiscoveryFailure.None);

        Assert.NotNull(catalog.Find("latest"));
        Assert.NotNull(catalog.Find("gpt-5.6-sol"));
        Assert.NotNull(catalog.Find("GPT-5.6-SOL"));
        Assert.Null(catalog.Find("something-else"));
    }

    [Fact]
    public void An_unknown_model_rejects_nothing()
    {
        // The permissive direction, matching the existing capability gate: absence of knowledge
        // must never become a refusal, or a working mapping would be blocked by a failed probe.
        var unknown = new AgentModel("mystery");

        Assert.Equal(EffortSupport.Unknown, unknown.Effort);
        Assert.False(unknown.Rejects("ultra"));
        Assert.Empty(unknown.Efforts);
    }

    [Fact]
    public void A_model_that_takes_no_effort_rejects_every_level()
    {
        // Distinct from unknown despite both carrying no levels, which is the entire reason the
        // flag is a tri-state. No vendor states a refusal today — claude signals it by omitting a
        // field, which must read as unknown — so this state exists for the one that eventually does.
        var none = new AgentModel("haiku", Effort: EffortSupport.No);

        Assert.True(none.Rejects("low"));
        Assert.True(none.Rejects("high"));
    }

    private sealed class StubProbe(string? reply, ModelDiscoveryFailure failure) : IAgentModelProbe
    {
        public Task<(JsonElement? Answer, ModelDiscoveryFailure Failure)> ExchangeAsync(
            string executable,
            IReadOnlyList<string> arguments,
            IReadOnlyList<ProbeTurn> turns,
            CancellationToken cancellationToken,
            TimeSpan? timeout = null)
        {
            Assert.Equal("codex", executable);
            Assert.Equal(["app-server"], arguments);
            // initialize (answered), the initialized notification (not answered), then model/list.
            // The middle turn awaits nothing because a JSON-RPC notification is never replied to,
            // and waiting for one would hang until the timeout.
            Assert.Equal(3, turns.Count);
            Assert.Null(turns[1].AwaitReply);

            if (reply is null)
            {
                return Task.FromResult<(JsonElement?, ModelDiscoveryFailure)>((null, failure));
            }

            var answer = JsonDocument.Parse(reply).RootElement.Clone();
            // Proves the adapter matches its own reply rather than taking whichever line arrives
            // first — codex interleaves unsolicited notifications with its responses.
            var isAnswer = turns[^1].AwaitReply!;
            Assert.True(isAnswer(answer));
            Assert.False(isAnswer(
                JsonDocument.Parse("""{"method":"remoteControl/status/changed"}""").RootElement));
            return Task.FromResult<(JsonElement?, ModelDiscoveryFailure)>(
                (answer, ModelDiscoveryFailure.None));
        }
    }
}
