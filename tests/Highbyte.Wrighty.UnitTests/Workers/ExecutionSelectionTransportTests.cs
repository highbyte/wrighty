using Highbyte.Wrighty.Models;
using Highbyte.Wrighty.Workers;

namespace Highbyte.Wrighty.UnitTests.Workers;

/// <summary>
/// Pins the exact arguments a resolved selection produces, and — more importantly — proves it
/// reaches only fresh launches.
/// </summary>
public sealed class ExecutionSelectionTransportTests
{
    private static readonly SessionHandle Handle = new("11111111-2222-3333-4444-555555555555");
    private static readonly Workspace Space = new("/tmp/workspace");

    private static WorkItemDetail Item() =>
        new(new WorkItemId("local:1"), "Title", "Body", Url: null, Status: "Todo", Priority: null);

    private static ExecutionSelection Selection(
        string? model = "opus", ExecutionEffort? effort = ExecutionEffort.XHigh) =>
        new("deep", "claude", model, effort);

    public static TheoryData<IAgentAdapter> Adapters() =>
        [
            new ClaudeAgentAdapter(),
            new CodexAgentAdapter(),
            new CopilotAgentAdapter(),
            new OpenCodeAgentAdapter()
        ];

    [Theory]
    [MemberData(nameof(Adapters))]
    public void A_fresh_launch_without_a_selection_is_byte_identical_to_before(IAgentAdapter adapter)
    {
        var withoutSelection = adapter.BuildStart(
            Item(), Handle, Space, AgentPermissionProfile.Workspace);
        var withNullSelection = adapter.BuildStart(
            Item(), Handle, Space, AgentPermissionProfile.Workspace, selection: null);

        Assert.Equal(withoutSelection.Arguments, withNullSelection.Arguments);
        Assert.DoesNotContain("--model", withoutSelection.Arguments);
        Assert.DoesNotContain("--effort", withoutSelection.Arguments);
        Assert.DoesNotContain(
            withoutSelection.Arguments,
            argument => argument.StartsWith("model_reasoning_effort", StringComparison.Ordinal));
    }

    [Theory]
    [MemberData(nameof(Adapters))]
    public void Resume_and_check_never_carry_a_selection(IAgentAdapter adapter)
    {
        // There is no overload that could: the parameter exists only on the fresh-start methods.
        // This asserts the resulting arguments too, so the guarantee survives a future signature
        // change that adds one.
        var resume = Assert.IsAssignableFrom<IAgentResumeAdapter>(adapter);
        var invocations = new[]
        {
            resume.BuildResume(Handle, Space, "prompt", AgentPermissionProfile.Workspace),
            resume.BuildResumeWithPrompt(Handle, Space, AgentPermissionProfile.Workspace, "prompt"),
            adapter.BuildCheck(Handle, Space)
        };

        foreach (var invocation in invocations)
        {
            Assert.DoesNotContain("--model", invocation.Arguments);
            Assert.DoesNotContain("--effort", invocation.Arguments);
            Assert.DoesNotContain(
                invocation.Arguments,
                argument => argument.Contains("model_reasoning_effort", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void Claude_passes_model_and_effort_as_plain_flags()
    {
        var invocation = new ClaudeAgentAdapter().BuildStartWithPrompt(
            Handle, Space, AgentPermissionProfile.Workspace, "prompt", Selection());

        AssertAdjacent(invocation.Arguments, "--model", "opus");
        AssertAdjacent(invocation.Arguments, "--effort", "xhigh");
    }

    [Fact]
    public void Copilot_passes_model_and_effort_as_plain_flags()
    {
        var invocation = new CopilotAgentAdapter().BuildStartWithPrompt(
            Handle, Space, AgentPermissionProfile.Workspace, "prompt",
            new ExecutionSelection("economy", "copilot", "auto", ExecutionEffort.Minimal));

        AssertAdjacent(invocation.Arguments, "--model", "auto");
        AssertAdjacent(invocation.Arguments, "--effort", "minimal");
    }

    [Fact]
    public void Codex_builds_the_whole_config_token_itself()
    {
        var invocation = new CodexAgentAdapter().BuildStartWithPrompt(
            Handle, Space, AgentPermissionProfile.Workspace, "prompt",
            new ExecutionSelection("deep", "codex", "gpt-5.6-sol", ExecutionEffort.High));

        AssertAdjacent(invocation.Arguments, "--model", "gpt-5.6-sol");
        AssertAdjacent(invocation.Arguments, "-c", "model_reasoning_effort=high");

        // The sandbox override codex already carried must survive alongside it: two -c arguments
        // with different keys, neither displacing the other.
        AssertAdjacent(
            invocation.Arguments, "-c", "sandbox_workspace_write.network_access=true");
        Assert.Equal(2, invocation.Arguments.Count(argument => argument == "-c"));
    }

    [Fact]
    public void OpenCode_passes_provider_model_and_variant_as_plain_flags()
    {
        var invocation = new OpenCodeAgentAdapter().BuildStartWithPrompt(
            Handle, Space, AgentPermissionProfile.Workspace, "prompt",
            new ExecutionSelection(
                "deep", "opencode", "anthropic/claude-sonnet-4-5", ExecutionEffort.High));

        AssertAdjacent(invocation.Arguments, "--model", "anthropic/claude-sonnet-4-5");
        AssertAdjacent(invocation.Arguments, "--variant", "high");
    }

    [Fact]
    public void Codex_keeps_the_prompt_as_the_final_positional_argument()
    {
        // `codex exec` takes the prompt positionally, so an inserted flag pair must not displace it.
        var invocation = new CodexAgentAdapter().BuildStart(
            Item(), Handle, Space, AgentPermissionProfile.Workspace,
            selection: new ExecutionSelection("deep", "codex", "gpt-5.6-sol", ExecutionEffort.High));

        Assert.DoesNotContain("--model", invocation.Arguments[^1]);
        Assert.Contains("local:1", invocation.Arguments[^1]);

        var stdinForm = new CodexAgentAdapter().BuildStartWithPrompt(
            Handle, Space, AgentPermissionProfile.Workspace, "prompt",
            new ExecutionSelection("deep", "codex", "gpt-5.6-sol", ExecutionEffort.High));
        Assert.Equal("-", stdinForm.Arguments[^1]);
    }

    [Fact]
    public void A_model_only_selection_passes_no_effort_argument()
    {
        var invocation = new ClaudeAgentAdapter().BuildStartWithPrompt(
            Handle, Space, AgentPermissionProfile.Workspace, "prompt",
            Selection(effort: null));

        AssertAdjacent(invocation.Arguments, "--model", "opus");
        Assert.DoesNotContain("--effort", invocation.Arguments);
    }

    [Fact]
    public void An_effort_only_selection_passes_no_model_argument()
    {
        // The mapping deliberately defers the model to the vendor CLI's own configuration.
        var invocation = new ClaudeAgentAdapter().BuildStartWithPrompt(
            Handle, Space, AgentPermissionProfile.Workspace, "prompt",
            Selection(model: null));

        Assert.DoesNotContain("--model", invocation.Arguments);
        AssertAdjacent(invocation.Arguments, "--effort", "xhigh");
    }

    [Fact]
    public void A_hostile_model_string_stays_one_argument_and_cannot_add_options()
    {
        // Settings are hand-editable, so a model value could contain anything. It must land as a
        // single argv element rather than being split into further options.
        const string hostile = "sonnet --dangerously-skip-permissions";
        var invocation = new ClaudeAgentAdapter().BuildStartWithPrompt(
            Handle, Space, AgentPermissionProfile.Workspace, "prompt",
            Selection(model: hostile));

        AssertAdjacent(invocation.Arguments, "--model", hostile);
        Assert.Single(invocation.Arguments, argument => argument == hostile);
        Assert.DoesNotContain("--dangerously-skip-permissions", invocation.Arguments);
    }

    [Fact]
    public void A_hostile_model_string_cannot_smuggle_a_second_codex_config_override()
    {
        // codex's -c reaches sandbox and approval settings, so this is the argument that would hurt.
        const string hostile = "gpt-5.6-luna -c sandbox_mode=danger-full-access";
        var invocation = new CodexAgentAdapter().BuildStartWithPrompt(
            Handle, Space, AgentPermissionProfile.Workspace, "prompt",
            new ExecutionSelection("economy", "codex", hostile, ExecutionEffort.Low));

        AssertAdjacent(invocation.Arguments, "--model", hostile);
        Assert.Single(
            invocation.Arguments,
            argument => argument.StartsWith("model_reasoning_effort", StringComparison.Ordinal));
        Assert.DoesNotContain("sandbox_mode=danger-full-access", invocation.Arguments);
    }

    /// <summary>
    /// Asserts the flag/value pair appears together somewhere in the argument list. It matches any
    /// occurrence rather than the first, because a flag can legitimately repeat: codex already
    /// passes <c>-c sandbox_workspace_write.network_access=true</c> for its sandbox, so the effort
    /// override is the second <c>-c</c> on the line.
    /// </summary>
    private static void AssertAdjacent(
        IReadOnlyList<string> arguments, string flag, string value)
    {
        for (var index = 0; index < arguments.Count - 1; index++)
        {
            if (arguments[index] == flag && arguments[index + 1] == value)
            {
                return;
            }
        }

        Assert.Fail($"expected '{flag} {value}' in: {string.Join(" ", arguments)}");
    }
}
