using System.Text.Json;
using Highbyte.Wrighty.Configuration;
using Highbyte.Wrighty.Models;
using Highbyte.Wrighty.Workers;

namespace Highbyte.Wrighty.UnitTests.Workers;

/// <summary>
/// The shared launch boundary itself: stage ordering, first-refusal semantics, and the built-in
/// policy/permission checks. End-to-end wiring through <see cref="WorkerService"/> is covered by
/// <see cref="LaunchPreflightWorkerTests"/>.
/// </summary>
public sealed class LaunchPreflightTests
{
    private static readonly TrackerConfig Config = new()
    {
        Backend = "local-markdown",
        Worker = new WorkerConfig { DefaultAgent = "claude" }
    };

    private static WorkerOptions Options(
        IReadOnlyDictionary<string, string>? filters = null,
        string? agent = "claude") =>
        new(agent, true, null, WorkspaceMode.Current,
            filters ?? new Dictionary<string, string>(), null, TimeSpan.FromMinutes(10),
            FencedAction.Kill, null, "agent", false, false);

    private static WorkItemDetail Item(
        bool automatic = true,
        string? agentPolicy = "claude",
        string? dispatchState = null,
        string? status = "In progress",
        IReadOnlyDictionary<string, JsonElement>? fields = null) =>
        new(new WorkItemId("local:1"), "Title", "Body", null, status, "P1", Fields: fields,
            AutomaticExecutionAllowed: automatic, AgentPolicy: agentPolicy,
            DispatchState: dispatchState);

    private static LaunchPreflightRequest Request(
        WorkItemDetail detail,
        LaunchStage stage = LaunchStage.PostClaim,
        LaunchKind kind = LaunchKind.Fresh,
        string agent = "claude",
        WorkerOptions? options = null) =>
        new(Config, options ?? Options(), detail, agent, kind, stage);

    [Theory]
    [InlineData(WorkerPolicyReason.PausedOrQueued)]
    [InlineData(WorkerPolicyReason.ExecutionNotAutomatic)]
    [InlineData(WorkerPolicyReason.FilteredOut)]
    [InlineData(WorkerPolicyReason.UnresolvedAgent)]
    public void Policy_gate_reports_the_specific_reason_an_item_is_ineligible(
        WorkerPolicyReason expected)
    {
        var (detail, options) = expected switch
        {
            WorkerPolicyReason.PausedOrQueued => (Item(dispatchState: "queued"), Options()),
            WorkerPolicyReason.ExecutionNotAutomatic => (Item(automatic: false), Options()),
            WorkerPolicyReason.FilteredOut => (
                Item(),
                Options(new Dictionary<string, string> { ["priority"] = "P9" })),
            _ => (Item(agentPolicy: "unknown-vendor"), Options(agent: null))
        };

        var decision = WorkerPolicyGate.Evaluate(
            detail, options, Config.EffectiveWorker.DefaultAgent, IsClaude);

        Assert.Equal(expected, decision.Reason);
        Assert.False(decision.Eligible);
        Assert.Null(decision.Agent);
        Assert.NotEmpty(WorkerPolicyGate.Describe(decision.Reason));
    }

    [Fact]
    public void Policy_gate_resolves_the_agent_from_option_then_item_then_configuration()
    {
        Assert.Equal("claude", WorkerPolicyGate.Evaluate(
            Item(agentPolicy: "codex"), Options(agent: "CLAUDE"), "codex", _ => true).Agent);
        Assert.Equal("codex", WorkerPolicyGate.Evaluate(
            Item(agentPolicy: " Codex "), Options(agent: null), "claude", _ => true).Agent);
        Assert.Equal("claude", WorkerPolicyGate.Evaluate(
            Item(agentPolicy: null), Options(agent: null), "claude", _ => true).Agent);
    }

    [Fact]
    public async Task Post_claim_admits_an_unchanged_item_and_reports_its_evidence()
    {
        var preflight = BuiltIn();

        var result = await preflight.EvaluateAsync(Request(Item()), CancellationToken.None);

        Assert.True(result.Admitted);
        Assert.Null(result.RefusedBy);
        Assert.Contains("policy-agent=claude", result.Evidence!);
        Assert.Contains("permission-profile=workspace", result.Evidence!);
    }

    [Fact]
    public async Task Post_claim_refuses_when_project_policy_changed_after_selection()
    {
        var result = await BuiltIn().EvaluateAsync(
            Request(Item(automatic: false)), CancellationToken.None);

        Assert.False(result.Admitted);
        Assert.Equal("worker-policy", result.RefusedBy);
        Assert.Equal(LaunchPreflightCodes.PolicyChanged, result.Code);
        Assert.Contains("unattended execution", result.Message);
        Assert.Contains($"policy-reason={WorkerPolicyReason.ExecutionNotAutomatic}", result.Evidence!);
    }

    [Fact]
    public async Task Post_claim_refuses_when_policy_now_selects_a_different_agent()
    {
        // Both vendors are installed here, so the refusal is specifically "policy moved the item
        // to another agent" rather than the generic unresolved-agent case.
        var preflight = new WorkerLaunchPreflight([new WorkerPolicyLaunchCheck(_ => true)]);
        var result = await preflight.EvaluateAsync(
            Request(Item(agentPolicy: "codex"), options: Options(agent: null)),
            CancellationToken.None);

        Assert.False(result.Admitted);
        Assert.Equal(LaunchPreflightCodes.AgentChanged, result.Code);
        Assert.Contains("claimed-agent=claude", result.Evidence!);
        Assert.Contains("policy-agent=codex", result.Evidence!);
    }

    [Fact]
    public async Task Post_claim_refuses_an_unresolvable_permission_profile_rather_than_guessing()
    {
        var config = new TrackerConfig
        {
            Backend = "local-markdown",
            Worker = new WorkerConfig { DefaultAgent = "claude", AgentPermissions = "nonsense" }
        };
        var request = new LaunchPreflightRequest(
            config, Options(), Item(), "claude", LaunchKind.Fresh, LaunchStage.PostClaim);

        var result = await BuiltIn().EvaluateAsync(request, CancellationToken.None);

        Assert.False(result.Admitted);
        Assert.Equal("agent-permissions", result.RefusedBy);
        Assert.Equal(LaunchPreflightCodes.PermissionsUnavailable, result.Code);
    }

    [Fact]
    public async Task Post_claim_refuses_an_agent_this_installation_does_not_support()
    {
        var preflight = new WorkerLaunchPreflight(
        [
            new AgentPermissionLaunchCheck(_ => false, (_, _) =>
                throw new Xunit.Sdk.XunitException("Permissions must not be described."))
        ]);

        var result = await preflight.EvaluateAsync(Request(Item()), CancellationToken.None);

        Assert.False(result.Admitted);
        Assert.Equal(LaunchPreflightCodes.PermissionsUnavailable, result.Code);
    }

    [Fact]
    public async Task Evaluation_stops_at_the_first_refusal_and_keeps_registration_order()
    {
        var second = new RecordingCheck("second", admit: true);
        var preflight = new WorkerLaunchPreflight(
            [new RecordingCheck("first", admit: false), second]);

        var result = await preflight.EvaluateAsync(Request(Item()), CancellationToken.None);

        Assert.False(result.Admitted);
        Assert.Equal("first", result.RefusedBy);
        Assert.Equal(0, second.Evaluations);
    }

    [Fact]
    public async Task Admission_accumulates_evidence_from_every_check_that_ran()
    {
        var preflight = new WorkerLaunchPreflight(
            [new RecordingCheck("first", admit: true), new RecordingCheck("second", admit: true)]);

        var result = await preflight.EvaluateAsync(Request(Item()), CancellationToken.None);

        Assert.True(result.Admitted);
        Assert.Equal(["first-ran", "second-ran"], result.Evidence!);
    }

    [Fact]
    public void Built_in_checks_gate_the_stages_and_kinds_they_claim()
    {
        var preflight = BuiltIn();

        Assert.Equal(
            ["worker-policy", "agent-permissions"],
            preflight.CheckNamesFor(LaunchStage.PostClaim, LaunchKind.Fresh));
        // A resume re-enters a session that already exists here, so fresh-work policy selection
        // must not be able to strand it. Claim ownership stays the authority for resume.
        Assert.Equal(
            ["agent-permissions"],
            preflight.CheckNamesFor(LaunchStage.PostClaim, LaunchKind.Resume));
        // Pre-spawn carries no built-in check yet: it is the seam plan 030's approved-context
        // revision check registers into. It must still be wired and enforced.
        Assert.Empty(preflight.CheckNamesFor(LaunchStage.PreSpawn, LaunchKind.Fresh));
        Assert.Empty(preflight.CheckNamesFor(LaunchStage.PreClaim, LaunchKind.Fresh));
    }

    [Fact]
    public async Task An_additional_check_can_gate_pre_spawn_without_touching_the_built_in_set()
    {
        var preflight = new WorkerLaunchPreflight(
        [
            new WorkerPolicyLaunchCheck(IsClaude),
            new StageCheck("approved-context", LaunchStage.PreSpawn)
        ]);

        Assert.True((await preflight.EvaluateAsync(
            Request(Item()), CancellationToken.None)).Admitted);

        var refused = await preflight.EvaluateAsync(
            Request(Item(), LaunchStage.PreSpawn), CancellationToken.None);

        Assert.False(refused.Admitted);
        Assert.Equal("approved-context", refused.RefusedBy);
    }

    private static bool IsClaude(string agent) =>
        string.Equals(agent, "claude", StringComparison.OrdinalIgnoreCase);

    private static WorkerLaunchPreflight BuiltIn() => new(
    [
        new WorkerPolicyLaunchCheck(IsClaude),
        new AgentPermissionLaunchCheck(
            IsClaude,
            (config, agent) => new ClaudeAgentAdapter().DescribePermissions(
                config.EffectiveWorker.RequestedAgentPermissions(agent)))
    ]);

    private sealed class RecordingCheck(string name, bool admit) : ILaunchPreflightCheck
    {
        public int Evaluations { get; private set; }

        public string Name => name;

        public bool AppliesTo(LaunchStage stage, LaunchKind kind) => true;

        public ValueTask<LaunchPreflightDecision> EvaluateAsync(
            LaunchPreflightRequest request, CancellationToken cancellationToken)
        {
            Evaluations++;
            return ValueTask.FromResult(admit
                ? LaunchPreflightDecision.Admit([$"{name}-ran"])
                : LaunchPreflightDecision.Refuse("REFUSED", $"{name} refused.", [$"{name}-ran"]));
        }
    }

    private sealed class StageCheck(string name, LaunchStage stage) : ILaunchPreflightCheck
    {
        public string Name => name;

        public bool AppliesTo(LaunchStage value, LaunchKind kind) => value == stage;

        public ValueTask<LaunchPreflightDecision> EvaluateAsync(
            LaunchPreflightRequest request, CancellationToken cancellationToken) =>
            ValueTask.FromResult(LaunchPreflightDecision.Refuse("REFUSED", $"{name} refused."));
    }
}
