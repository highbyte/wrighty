using Highbyte.Wrighty.AgentContext;
using Highbyte.Wrighty.Errors;

namespace Highbyte.Wrighty.UnitTests.AgentContext;

public sealed class AgentExecutionContextProviderTests
{
    [Theory]
    [InlineData("CODEX_THREAD_ID", "codex-session", "codex")]
    [InlineData("CLAUDE_CODE_SESSION_ID", "claude-session", "claude")]
    [InlineData("COPILOT_AGENT_SESSION_ID", "copilot-session", "copilot")]
    public void Strong_vendor_signal_detects_type_and_session(
        string variable,
        string sessionId,
        string agentType)
    {
        var context = Resolve(new() { [variable] = sessionId });

        Assert.Equal(agentType, context.AgentType);
        Assert.Equal(sessionId, context.SessionId);
        Assert.Equal(AgentContextSource.VendorEnvironment, context.Source);
        Assert.Equal(ClaimantKind.Agent, context.ClaimantKind);
        Assert.Null(context.Warning);
    }

    [Fact]
    public void Claude_weak_signal_detects_only_agent_type()
    {
        var context = Resolve(new() { ["CLAUDECODE"] = "1" });

        Assert.Equal("claude", context.AgentType);
        Assert.Null(context.SessionId);
    }

    [Fact]
    public void Claude_remote_flag_detects_agent_type_without_a_session_id()
    {
        var context = Resolve(new() { ["CLAUDE_CODE_REMOTE"] = "true" });

        Assert.Equal("claude", context.AgentType);
        Assert.Null(context.SessionId);
    }

    [Fact]
    public void Claude_remote_session_is_preferred()
    {
        var context = Resolve(new()
        {
            ["CLAUDE_CODE_SESSION_ID"] = "local-session",
            ["CLAUDE_CODE_REMOTE_SESSION_ID"] = "remote-session"
        });

        Assert.Equal("claude", context.AgentType);
        Assert.Equal("remote-session", context.SessionId);
    }

    [Fact]
    public void Explicit_fields_override_tracker_and_vendor_environment_independently()
    {
        var environment = new Dictionary<string, string?>
        {
            ["WRIGHTY_AGENT_TYPE"] = "claude",
            ["WRIGHTY_SESSION_ID"] = "tracker-session",
            ["CODEX_THREAD_ID"] = "codex-session"
        };

        var context = Resolve(
            environment,
            new AgentContextInput("copilot", "explicit-session"));

        Assert.Equal("copilot", context.AgentType);
        Assert.Equal("explicit-session", context.SessionId);
        Assert.Equal(AgentContextSource.ExplicitOption, context.Source);
    }

    [Fact]
    public void Tracker_environment_can_supply_one_field_and_vendor_the_other()
    {
        var context = Resolve(new()
        {
            ["WRIGHTY_AGENT_TYPE"] = "other",
            ["CODEX_THREAD_ID"] = "codex-session"
        });

        Assert.Equal("other", context.AgentType);
        Assert.Equal("codex-session", context.SessionId);
        Assert.Equal(AgentContextSource.TrackerEnvironment, context.Source);
    }

    [Fact]
    public void Disabled_context_suppresses_every_source()
    {
        var context = Resolve(
            new() { ["CODEX_THREAD_ID"] = "codex-session" },
            new AgentContextInput("codex", "explicit-session", Disabled: true));

        Assert.Null(context.AgentType);
        Assert.Null(context.SessionId);
        Assert.Null(context.Warning);
    }

    [Fact]
    public void Tracker_disable_environment_suppresses_every_source()
    {
        var context = Resolve(new()
        {
            ["WRIGHTY_NO_AGENT_CONTEXT"] = "true",
            ["CODEX_THREAD_ID"] = "codex-session"
        });

        Assert.Equal(AgentExecutionContext.None, context);
    }

    [Fact]
    public void Direct_cli_use_defaults_to_human()
    {
        var context = Resolve([]);

        Assert.Equal(ClaimantKind.Human, context.ClaimantKind);
        Assert.Null(context.AgentType);
        Assert.Null(context.SessionId);
    }

    [Fact]
    public void Explicit_agent_kind_falls_back_to_other_when_runtime_is_not_detected()
    {
        var context = Resolve([], new AgentContextInput(ClaimantKind: "agent"));

        Assert.Equal(ClaimantKind.Agent, context.ClaimantKind);
        Assert.Equal("other", context.AgentType);
    }

    [Fact]
    public void Automation_environment_overrides_vendor_detection()
    {
        var context = Resolve(new()
        {
            ["WRIGHTY_CLAIMANT_KIND"] = "automation",
            ["CODEX_THREAD_ID"] = "ambient-agent-session"
        });

        Assert.Equal(ClaimantKind.Automation, context.ClaimantKind);
        Assert.Null(context.AgentType);
        Assert.Null(context.SessionId);
    }

    [Fact]
    public void Non_agent_kind_rejects_configured_agent_metadata()
    {
        var exception = Assert.Throws<TrackerException>(() => Resolve(
            [],
            new AgentContextInput("codex", null, ClaimantKind: "automation")));

        Assert.Equal("ARGUMENT_INVALID", exception.Code);
    }

    [Fact]
    public void Unknown_claimant_kind_is_rejected()
    {
        Assert.Throws<TrackerException>(() =>
            Resolve([], new AgentContextInput(ClaimantKind: "robot")));
    }

    [Fact]
    public void Conflicting_vendor_signals_warn_and_are_not_guessed()
    {
        var context = Resolve(new()
        {
            ["CODEX_THREAD_ID"] = "codex-session",
            ["CLAUDE_CODE_SESSION_ID"] = "claude-session"
        });

        Assert.Null(context.AgentType);
        Assert.Null(context.SessionId);
        Assert.NotNull(context.Warning);
    }

    [Theory]
    [InlineData("")]
    [InlineData("https://example.test/session")]
    [InlineData("line\nbreak")]
    public void Invalid_explicit_session_is_rejected(string sessionId)
    {
        var exception = Assert.Throws<TrackerException>(() =>
            Resolve([], new AgentContextInput("codex", sessionId)));

        Assert.Equal("ARGUMENT_INVALID", exception.Code);
    }

    [Fact]
    public void Oversized_explicit_session_is_rejected()
    {
        Assert.Throws<TrackerException>(() =>
            Resolve([], new AgentContextInput("codex", new string('x', 201))));
    }

    [Fact]
    public void Unknown_explicit_agent_type_is_rejected()
    {
        Assert.Throws<TrackerException>(() =>
            Resolve([], new AgentContextInput("cursor", "session")));
    }

    private static AgentExecutionContext Resolve(
        Dictionary<string, string?> environment,
        AgentContextInput? input = null) =>
        new AgentExecutionContextProvider(environment).Resolve(input ?? new AgentContextInput());
}
