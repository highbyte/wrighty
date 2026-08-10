using System.Text.Json;
using Highbyte.Wrighty.Workers;

namespace Highbyte.Wrighty.UnitTests.Workers;

/// <summary>
/// The two vendors that answer *partially*, which is where the tri-state earns its keep.
///
/// Claude states effort support only positively — the one model that takes none omits the field
/// rather than denying it. Copilot states it for the session's current model alone. Neither can be
/// read as a refusal, and a test suite that only covered codex would never show that.
///
/// Payloads are trimmed from real replies (Claude Code 2.1.226, Copilot CLI 1.0.78, 2026-08-10).
/// </summary>
public sealed class ClaudeAndCopilotModelDiscoveryTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-08-10T09:00:00Z");

    // Note `haiku`: no supportsEffort key at all, while every other entry carries it explicitly true.
    private const string ClaudeReply = """
        {"type":"control_response","response":{"subtype":"success","request_id":"wrighty-models",
         "response":{
           "account":{"email":"someone@example.com","organization":"Example","subscriptionType":"Max"},
           "models":[
             {"value":"default","resolvedModel":"claude-opus-5[1m]","displayName":"Default (recommended)",
              "supportsEffort":true,"supportedEffortLevels":["low","medium","high","xhigh","max"]},
             {"value":"sonnet","resolvedModel":"claude-sonnet-5","displayName":"Sonnet",
              "supportsEffort":true,"supportedEffortLevels":["low","medium","high","xhigh","max"]},
             {"value":"haiku","resolvedModel":"claude-haiku-4-5-20251001","displayName":"Haiku"}
           ]}}}
        """;

    private const string CopilotReply = """
        {"jsonrpc":"2.0","id":2,"result":{
          "sessionId":"abc",
          "models":{"currentModelId":"gpt-5.4","availableModels":[
            {"modelId":"auto","name":"Auto","description":"Let Copilot pick"},
            {"modelId":"gpt-5.4","name":"GPT-5.4","_meta":{"copilotUsage":"6x","copilotEnablement":"enabled"}},
            {"modelId":"claude-haiku-4.5","name":"Claude Haiku 4.5","_meta":{"copilotUsage":"0.33x"}},
            {"modelId":"gemini-3.5-flash","name":"Gemini 3.5 Flash","_meta":{"copilotUsage":"14x"}}
          ]},
          "configOptions":[
            {"type":"select","id":"mode","name":"Mode","currentValue":"agent","options":[{"value":"agent","name":"Agent"}]},
            {"type":"select","id":"reasoning_effort","name":"Reasoning Effort","currentValue":"high",
             "options":[{"value":"none"},{"value":"low"},{"value":"medium"},{"value":"high"},{"value":"xhigh"}]}
          ]}}
        """;

    private static Task<AgentModelCatalog> ClaudeAsync(
        string? reply, ModelDiscoveryFailure failure = ModelDiscoveryFailure.None) =>
        new ClaudeModelDiscovery(new StubProbe(reply, failure, 1), () => Now)
            .DiscoverAsync(CancellationToken.None);

    private static Task<AgentModelCatalog> CopilotAsync(
        string? reply, ModelDiscoveryFailure failure = ModelDiscoveryFailure.None) =>
        new CopilotModelDiscovery(new StubProbe(reply, failure, 2), () => Now)
            .DiscoverAsync(CancellationToken.None);

    [Fact]
    public async Task Claude_reports_effort_levels_and_the_concrete_model_behind_an_alias()
    {
        var catalog = await ClaudeAsync(ClaudeReply);

        Assert.True(catalog.Succeeded);
        var sonnet = catalog.Find("sonnet");
        Assert.Equal(EffortSupport.Yes, sonnet!.Effort);
        Assert.Equal(["low", "medium", "high", "xhigh", "max"], sonnet.Efforts);
        // Resolving the alias is the point: two profiles pinned to different names can turn out to
        // run the same model, and only the resolved identifier reveals it.
        Assert.Equal("claude-sonnet-5", sonnet.ResolvedId);
        Assert.Equal("claude-opus-5[1m]", catalog.CurrentModelId);
    }

    [Fact]
    public async Task Claude_omitting_effort_support_is_unknown_rather_than_a_refusal()
    {
        // Measured: haiku takes no effort, and claude conveys that by leaving the key out entirely.
        // Reading absence as "no" would refuse a mapping the moment claude reorganised its payload,
        // so this stays unknown and the run-time relaunch fallback catches the real case.
        var catalog = await ClaudeAsync(ClaudeReply);

        var haiku = catalog.Find("haiku");
        Assert.Equal(EffortSupport.Unknown, haiku!.Effort);
        Assert.False(haiku.Rejects("high"));
        Assert.Empty(haiku.Efforts);
    }

    [Fact]
    public async Task Claude_account_identity_never_reaches_the_result()
    {
        // The initialize payload carries the operator's email, organization and subscription tier.
        // Discovery must read past all of it: anything surfaced here could be cached or logged.
        var catalog = await ClaudeAsync(ClaudeReply);

        var serialized = JsonSerializer.Serialize(catalog);
        Assert.DoesNotContain("example.com", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Example", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("Max", serialized, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Copilot_knows_effort_for_its_current_model_and_admits_ignorance_for_the_rest()
    {
        // The finding that shaped the contract. Copilot answers for one model, so copying its
        // levels onto the siblings would manufacture per-model knowledge from a sample of one.
        var catalog = await CopilotAsync(CopilotReply);

        var current = catalog.Find("gpt-5.4");
        Assert.Equal(EffortSupport.Yes, current!.Effort);
        Assert.Equal(["none", "low", "medium", "high", "xhigh"], current.Efforts);

        foreach (var other in catalog.Models.Where(model => model.Id != "gpt-5.4"))
        {
            Assert.Equal(EffortSupport.Unknown, other.Effort);
            Assert.Empty(other.Efforts);
            Assert.False(other.Rejects("max"));
        }
    }

    [Fact]
    public async Task A_models_effort_set_is_narrower_than_the_flag_it_is_passed_to()
    {
        // Copilot's --help declares the flag accepting none, minimal, low, medium, high, xhigh,
        // max — exactly what Wrighty's per-vendor gate ships, so that gate is correct. This model
        // accepts five of those seven. The two are different facts, and discovery supplies the
        // second *beneath* the gate rather than replacing it: narrowing the vendor set to one
        // observed model would refuse valid levels on every model never measured.
        var catalog = await CopilotAsync(CopilotReply);

        var current = catalog.Find("gpt-5.4")!;
        Assert.DoesNotContain("minimal", current.Efforts);
        Assert.DoesNotContain("max", current.Efforts);
        Assert.True(current.Rejects("minimal"));
        Assert.True(current.Rejects("max"));

        // And the narrowing applies only where it was measured. Every other model stays unknown,
        // so nothing is refused on its behalf.
        Assert.False(catalog.Find("claude-haiku-4.5")!.Rejects("minimal"));
    }

    [Fact]
    public async Task Copilot_reports_the_vendors_own_relative_cost_verbatim()
    {
        // Kept as the vendor's label rather than parsed into a number: Wrighty shows it so an
        // operator can weigh a choice, and must never rank or auto-select on it.
        var catalog = await CopilotAsync(CopilotReply);

        Assert.Equal("6x", catalog.Find("gpt-5.4")!.RelativeCost);
        Assert.Equal("0.33x", catalog.Find("claude-haiku-4.5")!.RelativeCost);
        Assert.Equal("14x", catalog.Find("gemini-3.5-flash")!.RelativeCost);
        // 'auto' carries no multiplier, because what it resolves to is not known in advance.
        Assert.Null(catalog.Find("auto")!.RelativeCost);
    }

    [Fact]
    public async Task Copilot_needing_a_sign_in_is_distinguished_from_being_broken()
    {
        // "Sign in" is actionable; "unavailable" is not. Collapsing them would leave an operator
        // with no idea that one command would fix it.
        var catalog = await CopilotAsync("""
            {"jsonrpc":"2.0","id":2,"error":{"code":-32000,"message":"Please log in with `copilot login`"}}
            """);

        Assert.Equal(ModelDiscoveryFailure.NotAuthenticated, catalog.Failure);
        Assert.Empty(catalog.Models);
    }

    [Fact]
    public async Task Copilot_reports_an_unrelated_error_as_unrecognized()
    {
        var catalog = await CopilotAsync("""
            {"jsonrpc":"2.0","id":2,"error":{"code":-32601,"message":"Method not found"}}
            """);

        Assert.Equal(ModelDiscoveryFailure.Unrecognized, catalog.Failure);
    }

    [Theory]
    [InlineData("""{"type":"control_response","response":{"subtype":"error","request_id":"wrighty-models"}}""")]
    [InlineData("""{"type":"control_response","response":{"subtype":"success","request_id":"wrighty-models","response":{}}}""")]
    public async Task Claude_answering_in_an_unexpected_shape_is_unrecognized(string reply)
    {
        var catalog = await ClaudeAsync(reply);

        Assert.Equal(ModelDiscoveryFailure.Unrecognized, catalog.Failure);
        Assert.Empty(catalog.Models);
    }

    [Fact]
    public async Task Copilot_missing_its_effort_option_leaves_every_model_unknown()
    {
        // A copilot release that drops or renames the reasoning_effort option must degrade to
        // unknown, not to an empty set that reads as a refusal.
        var catalog = await CopilotAsync("""
            {"jsonrpc":"2.0","id":2,"result":{"sessionId":"abc",
              "models":{"currentModelId":"gpt-5.4","availableModels":[{"modelId":"gpt-5.4"}]},
              "configOptions":[]}}
            """);

        var only = Assert.Single(catalog.Models);
        Assert.Equal(EffortSupport.Unknown, only.Effort);
        Assert.False(only.Rejects("high"));
    }

    [Theory]
    [InlineData(ModelDiscoveryFailure.NotInstalled)]
    [InlineData(ModelDiscoveryFailure.TimedOut)]
    public async Task Both_adapters_pass_a_probe_failure_through_unchanged(
        ModelDiscoveryFailure failure)
    {
        Assert.Equal(failure, (await ClaudeAsync(null, failure)).Failure);
        Assert.Equal(failure, (await CopilotAsync(null, failure)).Failure);
    }

    /// <param name="answeredTurns">
    /// How many turns must await a reply. Copilot's two-step handshake has to be sequenced — its
    /// ACP server ignores a session/new written before initialize is answered — so an adapter that
    /// pipelined both would work in this test and hang against the real CLI.
    /// </param>
    private sealed class StubProbe(
        string? reply, ModelDiscoveryFailure failure, int answeredTurns) : IAgentModelProbe
    {
        public Task<(JsonElement? Answer, ModelDiscoveryFailure Failure)> ExchangeAsync(
            string executable,
            IReadOnlyList<string> arguments,
            IReadOnlyList<ProbeTurn> turns,
            CancellationToken cancellationToken,
            TimeSpan? timeout = null)
        {
            Assert.Equal(answeredTurns, turns.Count(turn => turn.AwaitReply is not null));
            if (reply is null)
            {
                return Task.FromResult<(JsonElement?, ModelDiscoveryFailure)>((null, failure));
            }

            var answer = JsonDocument.Parse(reply).RootElement.Clone();
            Assert.True(turns[^1].AwaitReply!(answer));
            return Task.FromResult<(JsonElement?, ModelDiscoveryFailure)>(
                (answer, ModelDiscoveryFailure.None));
        }
    }
}
