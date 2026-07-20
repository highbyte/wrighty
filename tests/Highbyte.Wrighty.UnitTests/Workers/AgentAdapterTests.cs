using System.Text;
using Highbyte.Wrighty.Models;
using Highbyte.Wrighty.Workers;

namespace Highbyte.Wrighty.UnitTests.Workers;

public sealed class AgentAdapterTests
{
    private static readonly WorkItemDetail Item = new(
        new WorkItemId("local:42"), "Title", "Body", null, "Todo", "P1");
    private static readonly Workspace Workspace = new("/tmp/repo");

    [Fact]
    public void Handles_are_stable_per_claim_generation_and_vendor_appropriate()
    {
        var first = SessionHandles.ForClaude(Item.Id, "claim-token-1").Value;
        var repeated = SessionHandles.ForClaude(Item.Id, "claim-token-1").Value;
        var next = SessionHandles.ForClaude(Item.Id, "claim-token-2").Value;
        var namedFirst = SessionHandles.ForNamedVendor(Item.Id, "claim-token-1").Value;
        var namedRepeated = SessionHandles.ForNamedVendor(Item.Id, "claim-token-1").Value;
        var namedNext = SessionHandles.ForNamedVendor(Item.Id, "claim-token-2").Value;

        Assert.Equal(first, repeated);
        Assert.NotEqual(first, next);
        Assert.True(Guid.TryParse(first, out _));
        Assert.Equal(namedFirst, namedRepeated);
        Assert.NotEqual(namedFirst, namedNext);
        Assert.StartsWith("wrighty-local-42-", namedFirst);
    }

    [Fact]
    public void Claude_start_has_preassigned_uuid_json_and_autonomy_flags()
    {
        var invocation = new ClaudeAgentAdapter().BuildStart(
            Item, SessionHandles.ForClaude(Item.Id, "claim-token"), Workspace);

        Assert.Equal("claude", invocation.Executable);
        Assert.Contains("--session-id", invocation.Arguments);
        Assert.Contains("--output-format", invocation.Arguments);
        Assert.Contains("--dangerously-skip-permissions", invocation.Arguments);
        Assert.StartsWith("/wrighty Work Wrighty item local:42.", invocation.Arguments[1]);
    }

    [Fact]
    public void Commit_instruction_is_explicit_in_both_directions_and_worktree_only()
    {
        var worktree = new Workspace("/tmp/ws", IsWorktree: true, Branch: "wrighty-worker/x");
        var checkout = new Workspace("/tmp/repo");

        Assert.Null(WorkerPrompt.CommitInstruction(checkout, null));
        Assert.Null(WorkerPrompt.CommitInstruction(checkout, "agent"));
        Assert.Contains("Do not run git commit",
            WorkerPrompt.CommitInstruction(worktree, null));
        Assert.Contains("Do not run git commit",
            WorkerPrompt.CommitInstruction(worktree, "inspect"));
        Assert.Contains("Commit your work",
            WorkerPrompt.CommitInstruction(worktree, "agent"));

        var invocation = new ClaudeAgentAdapter().BuildStart(
            Item, SessionHandles.ForClaude(Item.Id, "claim-token"), worktree,
            WorkerPrompt.CommitInstruction(worktree, "inspect"));
        Assert.Contains("Do not run git commit", invocation.Arguments[1]);
    }

    [Fact]
    public void Worker_prompt_treats_wrighty_mutation_errors_as_lease_authority()
    {
        var prompt = WorkerPrompt.For(Item.Id);

        Assert.Contains("do not speculate about `expiresAt`", prompt);
        Assert.Contains("only CLAIM_EXPIRED or CLAIM_STALE from a Wrighty mutation is authoritative", prompt);
        Assert.Contains("do not attempt to reclaim", prompt);
    }

    [Fact]
    public void Codex_start_closes_stdin_skips_repo_check_and_sets_directory()
    {
        var invocation = new CodexAgentAdapter().BuildStart(
            Item, SessionHandles.ForNamedVendor(Item.Id, "claim-token"), Workspace);

        Assert.True(invocation.CloseStandardInput);
        Assert.Contains("--skip-git-repo-check", invocation.Arguments);
        Assert.Contains("-C", invocation.Arguments);
        Assert.Contains("/tmp/repo", invocation.Arguments);
    }

    [Fact]
    public void Codex_resume_places_exec_options_before_resume_subcommand()
    {
        var invocation = new CodexAgentAdapter().BuildResume(
            new SessionHandle("session-one"),
            Workspace,
            "Continue the clarified item.");

        Assert.True(invocation.CloseStandardInput);
        Assert.Equal(
            [
                "exec",
                "--json",
                "--skip-git-repo-check",
                "--sandbox",
                "workspace-write",
                "-C",
                "/tmp/repo",
                "resume",
                "session-one",
                "Continue the clarified item."
            ],
            invocation.Arguments);
    }

    [Fact]
    public void Copilot_start_is_local_json_and_all_tools()
    {
        var invocation = new CopilotAgentAdapter().BuildStart(
            Item, SessionHandles.ForNamedVendor(Item.Id, "claim-token"), Workspace);

        Assert.Contains("--no-remote", invocation.Arguments);
        Assert.Contains("--allow-all-tools", invocation.Arguments);
        Assert.Contains("--output-format", invocation.Arguments);
    }

    [Theory]
    [InlineData("claude", "claude --resume 'session-one'")]
    [InlineData("codex", "codex resume 'session-one'")]
    [InlineData("copilot", "copilot --resume='session-one'")]
    public void Interactive_resume_applies_claim_environment_to_vendor_process(
        string agentType,
        string expectedVendorCommand)
    {
        IAgentAdapter adapter = agentType switch
        {
            "claude" => new ClaudeAgentAdapter(),
            "codex" => new CodexAgentAdapter(),
            "copilot" => new CopilotAgentAdapter(),
            _ => throw new InvalidOperationException()
        };

        var command = adapter.BuildInteractiveCommand(
            new SessionHandle("session-one"),
            new Workspace("/tmp/repo with space"),
            new Dictionary<string, string>
            {
                ["WRIGHTY_CLAIMANT_ID"] = "agent:test",
                ["WRIGHTY_CLAIM_TOKEN"] = "token-one"
            });

        Assert.Equal(
            $"cd '/tmp/repo with space' && " +
            $"WRIGHTY_CLAIMANT_ID='agent:test' WRIGHTY_CLAIM_TOKEN='token-one' {expectedVendorCommand}",
            command);
    }

    [Theory]
    [InlineData("""{"type":"thread.started","thread_id":"019f-thread"}\n{"type":"turn.completed"}\n""", AgentOutcome.Succeeded, "019f-thread")]
    [InlineData("""{"type":"thread.started","thread_id":"019f-thread"}\n{"type":"turn.failed"}\n""", AgentOutcome.Failed, "019f-thread")]
    [InlineData("""{"type":"turn.completed"}\n""", AgentOutcome.Rejected, null)]
    public async Task Codex_interprets_captured_jsonl(string fixture, AgentOutcome outcome, string? session)
    {
        fixture = fixture.Replace("\\n", "\n", StringComparison.Ordinal);
        var result = await new CodexAgentAdapter().InterpretAsync(Stream(fixture), 0, CancellationToken.None);
        Assert.Equal(outcome, result.Outcome);
        Assert.Equal(session, result.SessionId);
    }

    [Theory]
    [InlineData("""{"type":"result","subtype":"success","is_error":false,"session_id":"uuid","result":"OK"}""", 0, AgentOutcome.Succeeded)]
    [InlineData("""{"type":"result","subtype":"error","is_error":true,"session_id":"uuid","result":"bad"}""", 1, AgentOutcome.Failed)]
    public async Task Claude_interprets_typed_result(string fixture, int exitCode, AgentOutcome outcome)
    {
        var result = await new ClaudeAgentAdapter().InterpretAsync(Stream(fixture), exitCode, CancellationToken.None);
        Assert.Equal(outcome, result.Outcome);
        Assert.Equal("uuid", result.SessionId);
    }

    [Theory]
    [InlineData(0, AgentOutcome.Succeeded)]
    [InlineData(7, AgentOutcome.Failed)]
    public async Task Copilot_interprets_terminal_exit_code(int resultExit, AgentOutcome outcome)
    {
        var fixture = $$"""{"type":"result","sessionId":"copilot-session","exitCode":{{resultExit}}}""";
        var result = await new CopilotAgentAdapter().InterpretAsync(Stream(fixture), 0, CancellationToken.None);
        Assert.Equal(outcome, result.Outcome);
        Assert.Equal("copilot-session", result.SessionId);
    }

    [Fact]
    public void Prompt_contains_preclaim_and_stale_stop_contract()
    {
        var prompt = WorkerPrompt.For(Item.Id);
        Assert.Contains("do not claim it again", prompt);
        Assert.Contains("WRIGHTY_CLAIMANT_ID", prompt);
        Assert.Contains("CLAIM_STALE", prompt);
        Assert.Contains("stop immediately", prompt);
        Assert.Contains("Do not suggest Wrighty claim, edit, takeover, finish, archive, or worker commands", prompt);
        Assert.Contains("the worker prints the operator's next actions", prompt);
    }

    [Fact]
    public void Claude_resume_explicitly_invokes_user_only_skill()
    {
        var invocation = new ClaudeAgentAdapter().BuildResume(
            new SessionHandle("session-one"),
            Workspace,
            "Continue the clarified item.");

        Assert.Equal("-p", invocation.Arguments[0]);
        Assert.Equal("/wrighty Continue the clarified item.", invocation.Arguments[1]);
    }

    [Theory]
    [InlineData("claude", "/wrighty Item local:42 has been clarified.")]
    [InlineData("copilot", "/wrighty Item local:42 has been clarified.")]
    [InlineData("codex", "$wrighty Item local:42 has been clarified.")]
    public void Resume_prompt_explicitly_invokes_vendor_skill(
        string agentType,
        string expectedStart)
    {
        var prompt = WorkerPrompt.ForResume(Item.Id, agentType);
        Assert.StartsWith(expectedStart, prompt);
        Assert.Contains("Do not suggest Wrighty claim, edit, takeover, finish, archive, or worker commands", prompt);
    }

    private static MemoryStream Stream(string value) => new(Encoding.UTF8.GetBytes(value));
}
