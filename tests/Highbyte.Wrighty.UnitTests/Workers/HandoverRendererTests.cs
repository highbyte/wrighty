using Highbyte.Wrighty.ApprovedContext;
using Highbyte.Wrighty.Claims;
using Highbyte.Wrighty.Configuration;
using Highbyte.Wrighty.Models;
using Highbyte.Wrighty.Workers;

namespace Highbyte.Wrighty.UnitTests.Workers;

public sealed class HandoverRendererTests
{
    private static readonly WorkItemId Id = new("github:owner/repo#42");
    private static readonly DateTimeOffset Ended =
        DateTimeOffset.Parse("2026-08-05T07:29:50Z");

    private static AgentRunReport Report(
        string? fallback = "The agent paused for clarification.",
        TrustedContinuationEvent? trigger = null) =>
        RunReportRenderer.Build(
            new RunIdentity(Id, "run-1", "codex"),
            RunReportDisposition.NeedsAttention,
            AgentOutcome.Succeeded,
            Ended,
            reported: null,
            rawFallback: fallback,
            trigger: trigger);

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
            visibility,
            Report: Report(finalMessage),
            Continuation: new WorkerContinuationConfig(),
            TrustedAuthors: ["operator"]);

    [Theory]
    [InlineData(RunOutcome.Failed, "Vendor process: failed")]
    [InlineData(RunOutcome.Rejected, "Vendor process: rejected")]
    public void Render_labels_non_success_outcomes(RunOutcome outcome, string expected)
    {
        var body = HandoverRenderer.Render(Content() with { Outcome = outcome });

        Assert.Contains(expected, body);
    }

    [Fact]
    public void Render_carries_the_marker_outcome_where_and_actions()
    {
        var body = HandoverRenderer.Render(Content());

        Assert.StartsWith(HandoverRenderer.Marker, body);
        Assert.True(HandoverRenderer.IsHandover(body));
        Assert.Contains(AgentRunReport.MarkerPrefix, body);
        Assert.Contains("needs attention", body);
        Assert.Contains("The agent paused for clarification.", body);
        Assert.Contains("host `build-host`", body);
        Assert.Contains("workspace `/tmp/worktree`", body);
        Assert.Contains("branch `feature/thing`", body);
        Assert.Contains("wrighty requeue github:owner/repo#42", body);
    }

    [Fact]
    public void Render_names_the_accepted_control_trigger()
    {
        var trigger = new TrustedContinuationEvent(
            "reaction-1",
            TrustedContinuationSource.Reaction,
            "operator",
            DateTimeOffset.UtcNow,
            Kind: TrustedContinuationKind.CompletionRequested);

        var body = HandoverRenderer.Render(Content() with { Report = Report(trigger: trigger) });

        Assert.Contains("Continuation trigger", body, StringComparison.Ordinal);
        Assert.Contains("completion reaction by @operator", body, StringComparison.Ordinal);
    }

    [Fact]
    public void Trusted_reply_and_reaction_choices_are_unambiguous()
    {
        var report = RunReportRenderer.Build(
            new RunIdentity(Id, "run-1", "codex"),
            RunReportDisposition.NeedsAttention,
            AgentOutcome.Succeeded,
            Ended,
            new AgentReportContent(RequestedInput: ["Choose blue or green."]));
        var body = HandoverRenderer.Render(Content() with { Report = report });

        Assert.Contains("**Codex needs:**", body, StringComparison.Ordinal);
        Assert.Contains("Choose blue or green.", body, StringComparison.Ordinal);
        Assert.Contains(
            "That reply alone continues the retained session; do not also react.",
            body,
            StringComparison.Ordinal);
        Assert.Contains("React 🚀 to this Wrighty comment", body, StringComparison.Ordinal);
        Assert.Contains("React 🎉 to this Wrighty comment", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Wrighty run report", body, StringComparison.Ordinal);
        Assert.Equal(1, Occurrences(body, "Choose blue or green."));
    }

    [Fact]
    public void Structured_summary_and_details_are_each_rendered_once()
    {
        var report = RunReportRenderer.Build(
            new RunIdentity(Id, "run-1", "codex"),
            RunReportDisposition.NeedsAttention,
            AgentOutcome.Succeeded,
            Ended,
            new AgentReportContent(
                "Waiting for a choice.",
                Changes: ["Updated the parser."],
                Verification: ["dotnet test"]));

        var body = HandoverRenderer.Render(Content() with { Report = report });

        Assert.Equal(1, Occurrences(body, "Waiting for a choice."));
        Assert.Contains("Checks the agent says it ran", body, StringComparison.Ordinal);
        Assert.Contains(
            "not independently verified by Wrighty", body, StringComparison.Ordinal);
    }

    private static int Occurrences(string text, string value) =>
        text.Split(value, StringSplitOptions.None).Length - 1;

    [Fact]
    public void Command_only_reply_names_the_required_first_line()
    {
        var content = Content() with
        {
            Continuation = new WorkerContinuationConfig
            {
                Trigger = WorkerContinuationConfig.TriggerModes.CommandOnly,
                Command = "/continue"
            }
        };

        var body = HandoverRenderer.Render(content);

        Assert.Contains(
            "Reply with `/continue` as the first line", body, StringComparison.Ordinal);
        Assert.Contains("do not also react", body, StringComparison.Ordinal);
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
    public void Retry_phase_shows_bounded_sanitized_decision()
    {
        var dispatch = new DispatchInfo(
            DispatchStates.RetryScheduled,
            "Usage limit reached.",
            "claude",
            null,
            DateTimeOffset.Parse("2026-07-24T04:02:00Z"),
            2,
            5,
            DateTimeOffset.Parse("2026-07-23T22:00:00Z"),
            true);
        var body = HandoverRenderer.Render(
            Content(HandoverPhase.RetryScheduled, finalMessage: "Usage limit reached.") with
            {
                Dispatch = dispatch
            });

        Assert.Contains("retry scheduled", body);
        Assert.Contains("Retry: `claude` no earlier than `2026-07-24T04:02:00.0000000+00:00`", body);
        Assert.Contains("attempt 2 of 5", body);
        Assert.DoesNotContain("account balance", body);
    }

    [Fact]
    public void Retry_phase_shows_sanitized_provider_state_policy_and_commands()
    {
        var content = Content(
            HandoverPhase.RetryScheduled,
            finalMessage: "Agent usage is exhausted.") with
        {
            Dispatch = new DispatchInfo(
                DispatchStates.RetryScheduled,
                "Agent usage is exhausted.",
                "claude",
                null,
                DateTimeOffset.Parse("2026-07-24T04:02:00Z"),
                2,
                5,
                DateTimeOffset.Parse("2026-07-23T22:00:00Z"),
                true),
            Provider = new ProviderCapacity(
                "claude",
                ProviderCapacityState.UnavailableUntil,
                "Usage exhausted\n`account payload omitted`",
                DateTimeOffset.Parse("2026-07-24T04:02:00Z"),
                AgentFailureConfidence.Authoritative,
                1,
                DateTimeOffset.Parse("2026-07-23T22:00:00Z")),
            Policy = new WorkItemPolicyPresentation(true, "codex"),
            Actions =
            [
                new WorkerOperatorAction(
                    "Probe Claude capacity",
                    ["wrighty provider probe claude"],
                    "Perform one bounded capacity check."),
                new WorkerOperatorAction(
                    "Retry now",
                    ["wrighty worker --item github:owner/repo#42 --yes"],
                    "Override the retry timer and provider circuit.")
            ]
        };

        var body = HandoverRenderer.Render(content);

        Assert.Contains("Provider capacity", body);
        Assert.Contains("`Claude` is unavailable until", body);
        Assert.Contains("Usage exhausted 'account payload omitted'", body);
        Assert.DoesNotContain("omitted'..", body);
        Assert.DoesNotContain("\n`account payload omitted`", body);
        Assert.Contains("automatic execution `Allowed`", body);
        Assert.Contains("agent `Codex`", body);
        Assert.Contains("wrighty provider probe claude", body);
        Assert.Contains("wrighty worker --item github:owner/repo#42 --yes", body);
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

    [Fact]
    public void TheFinalMessageExcerptDropsTheAgentsReportBlock()
    {
        // The excerpt is wrapped in a fence and the report block is itself fenced, so leaving it in
        // closes the outer fence early: everything after it escapes the code box and renders as raw
        // markdown in the comment. Observed on a real published comment before this was fixed.
        var body = HandoverRenderer.Render(Content(finalMessage:
            "I need a decision before finishing.\n\n```wrighty-report\n{\"summary\":\"x\"}\n```"));

        Assert.Contains("I need a decision before finishing.", body, StringComparison.Ordinal);
        Assert.DoesNotContain("wrighty-report", body, StringComparison.Ordinal);
        Assert.DoesNotContain("\"summary\"", body, StringComparison.Ordinal);
    }

    [Fact]
    public void AResponseThatIsOnlyAReportBlockSaysSoRatherThanQuotingNothing()
    {
        var body = HandoverRenderer.Render(Content(finalMessage:
            "```wrighty-report\n{\"summary\":\"x\"}\n```"));

        Assert.Contains("consisted only of its structured report", body, StringComparison.Ordinal);
    }
}
