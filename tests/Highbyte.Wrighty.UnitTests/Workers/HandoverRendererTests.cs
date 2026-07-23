using Highbyte.Wrighty.Claims;
using Highbyte.Wrighty.Configuration;
using Highbyte.Wrighty.Models;
using Highbyte.Wrighty.Workers;

namespace Highbyte.Wrighty.UnitTests.Workers;

public sealed class HandoverRendererTests
{
    private static readonly WorkItemId Id = new("github:owner/repo#42");

    private static HandoverContent Content(
        HandoverPhase phase = HandoverPhase.NeedsAttention,
        HandoverCommentMode visibility = HandoverCommentMode.Full,
        string? finalMessage = "The agent paused for clarification.") =>
        new(
            Id,
            phase,
            RunOutcome.Succeeded,
            finalMessage,
            "build-host",
            "/tmp/worktree",
            "feature/thing",
            [new WorkerOperatorAction(
                "Clarify and requeue",
                ["wrighty requeue github:owner/repo#42"],
                "Edit the issue body, then requeue on this host.")],
            visibility);

    [Fact]
    public void Render_carries_the_marker_outcome_where_and_actions()
    {
        var body = HandoverRenderer.Render(Content());

        Assert.StartsWith(HandoverRenderer.Marker, body);
        Assert.True(HandoverRenderer.IsHandover(body));
        Assert.Contains("needs attention", body);
        Assert.Contains("The agent paused for clarification.", body);
        Assert.Contains("host `build-host`", body);
        Assert.Contains("workspace `/tmp/worktree`", body);
        Assert.Contains("branch `feature/thing`", body);
        Assert.Contains("wrighty requeue github:owner/repo#42", body);
    }

    [Fact]
    public void Minimal_visibility_drops_host_and_workspace_but_keeps_branch()
    {
        var body = HandoverRenderer.Render(Content(visibility: HandoverCommentMode.Minimal));

        Assert.DoesNotContain("build-host", body);
        Assert.DoesNotContain("/tmp/worktree", body);
        Assert.Contains("branch `feature/thing`", body);
    }

    [Fact]
    public void Completed_phase_frames_the_review_path()
    {
        var body = HandoverRenderer.Render(Content(HandoverPhase.Completed));

        Assert.Contains("completed", body);
        Assert.Contains("retained for review", body);
    }

    [Fact]
    public void Resolved_form_is_short_and_keeps_the_marker()
    {
        var body = HandoverRenderer.RenderResolved("The item was archived.");

        Assert.True(HandoverRenderer.IsHandover(body));
        Assert.Contains("resolved", body);
        Assert.Contains("The item was archived.", body);
        Assert.DoesNotContain("Next actions", body);
    }

    [Fact]
    public void Agent_prompt_renders_as_its_own_block_after_the_command()
    {
        var content = Content(finalMessage: null) with
        {
            Actions =
            [
                new WorkerOperatorAction(
                    "Guided completion in the recorded session",
                    ["wrighty resume-command github:owner/repo#42"],
                    "Run this in your terminal, then paste the prompt below into the opened session.",
                    AgentPrompt: "/wrighty Complete item github:owner/repo#42: summarize the diff.")
            ]
        };

        var body = HandoverRenderer.Render(content);
        var commandPos = body.IndexOf("wrighty resume-command github:owner/repo#42", StringComparison.Ordinal);
        var promptLeadIn = body.IndexOf("Then paste this into the opened agent session:", StringComparison.Ordinal);
        var promptPos = body.IndexOf("/wrighty Complete item", StringComparison.Ordinal);

        Assert.True(commandPos >= 0 && promptLeadIn > commandPos && promptPos > promptLeadIn,
            "the terminal command must come before the agent-prompt block");
        // Two fenced blocks: the command and the prompt live in separate code blocks, so the id in
        // the prompt is not rendered as auto-linked prose.
        Assert.Equal(4, System.Text.RegularExpressions.Regex.Matches(body, "```").Count);
    }

    [Fact]
    public void Long_final_messages_are_excerpted()
    {
        var body = HandoverRenderer.Render(Content(finalMessage: new string('x', 5000)));

        Assert.Contains("…", body);
        Assert.True(body.Length < 5000);
    }
}
