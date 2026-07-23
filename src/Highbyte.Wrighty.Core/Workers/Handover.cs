using System.Text;
using Highbyte.Wrighty.Claims;
using Highbyte.Wrighty.Configuration;
using Highbyte.Wrighty.Models;

namespace Highbyte.Wrighty.Workers;

/// <summary>
/// Which phase of the two-path operator model the handover describes: a run that ended in
/// needs-attention (clarify → requeue) or one that finished with work retained for review
/// (review → integrate → archive).
/// </summary>
public enum HandoverPhase
{
    NeedsAttention,
    RetryScheduled,
    Completed
}

/// <summary>
/// The backend-neutral payload for a run handover: what happened, where the session lives, and the
/// exact next-step commands. Rendered to a single overwrite-style GitHub issue comment today; the
/// same content is the natural body for a future Slack notification (plan 016) or cross-agent
/// handoff (plan 026), so it deliberately carries data, not GitHub markup.
/// </summary>
public sealed record HandoverContent(
    WorkItemId Id,
    HandoverPhase Phase,
    RunOutcome Outcome,
    string? FinalMessage,
    string? Host,
    string? WorkspacePath,
    string? Branch,
    IReadOnlyList<WorkerOperatorAction> Actions,
    HandoverCommentMode Visibility,
    WorkerDispatchInfo? Dispatch = null);

/// <summary>
/// Renders <see cref="HandoverContent"/> to the marker-identified GitHub issue comment body. A
/// single comment per issue, found by marker and edited in place on subsequent runs.
/// </summary>
public static class HandoverRenderer
{
    public const string Marker = "<!-- wrighty-handover:v1 -->";

    private const int FinalMessageExcerptLength = 1200;

    public static bool IsHandover(string? body) =>
        body is not null && body.Contains(Marker, StringComparison.Ordinal);

    public static string Render(HandoverContent content)
    {
        var builder = new StringBuilder();
        builder.AppendLine(Marker);
        builder.AppendLine(content.Phase switch
        {
            HandoverPhase.NeedsAttention => "### Wrighty handover — needs attention",
            HandoverPhase.RetryScheduled => "### Wrighty handover — retry scheduled",
            _ => "### Wrighty handover — completed, work retained for review"
        });
        builder.AppendLine();

        if (content.Dispatch is { } dispatch)
        {
            builder.AppendLine(
                $"**Recovery decision** — retry `{dispatch.CurrentAgent ?? "agent"}` no earlier " +
                $"than `{dispatch.NotBefore:O}` (attempt {dispatch.Attempt} of " +
                $"{dispatch.MaxAttempts}).");
            builder.AppendLine();
        }

        builder.Append("**What happened** — ");
        builder.AppendLine(WhatHappened(content));
        builder.AppendLine();

        if (!string.IsNullOrWhiteSpace(content.FinalMessage))
        {
            builder.AppendLine("**Agent's final message**");
            builder.AppendLine();
            builder.AppendLine("```");
            builder.AppendLine(Excerpt(content.FinalMessage));
            builder.AppendLine("```");
            builder.AppendLine();
        }

        var where = Where(content);
        if (where is not null)
        {
            builder.AppendLine($"**Where** — {where}");
            builder.AppendLine();
        }

        if (content.Actions.Count > 0)
        {
            builder.AppendLine("**Next actions**");
            builder.AppendLine();
            foreach (var action in content.Actions)
                AppendAction(builder, action);
        }

        builder.Append("_Wrighty maintains this single comment; it is overwritten on each run and "
            + "trimmed once the item is requeued, archived, or its workspace is cleaned up. Do not "
            + "hand-edit the `wrighty:worker-state` label; use Wrighty's CLI actions._");
        return builder.ToString();
    }

    /// <summary>
    /// The trimmed "resolved" body written when the item is requeued, archived, or its workspace is
    /// cleaned up, so stale instructions do not linger. Keeps the marker so the same comment is
    /// found and reused on the next run.
    /// </summary>
    public static string RenderResolved(string reason)
    {
        var builder = new StringBuilder();
        builder.AppendLine(Marker);
        builder.AppendLine($"### Wrighty handover — resolved");
        builder.AppendLine();
        builder.Append($"_{reason}_");
        return builder.ToString();
    }

    private static string WhatHappened(HandoverContent content) => content.Phase switch
    {
        HandoverPhase.NeedsAttention =>
            $"the agent session paused without finishing (run {OutcomeLabel(content.Outcome)}). " +
            "It is retained on this machine and can be clarified and requeued, or reopened.",
        HandoverPhase.RetryScheduled =>
            $"the agent stopped because provider capacity is temporarily unavailable " +
            $"(run {OutcomeLabel(content.Outcome)}). Its vendor session and workspace are retained " +
            "on the recording installation for a bounded retry.",
        _ =>
            $"the agent finished the item (run {OutcomeLabel(content.Outcome)}) and the work is " +
            "retained for review before it is integrated and archived."
    };

    private static void AppendAction(StringBuilder builder, WorkerOperatorAction action)
    {
        builder.AppendLine($"- **{action.Scenario}**");
        if (!string.IsNullOrWhiteSpace(action.Description))
        {
            builder.AppendLine();
            builder.AppendLine($"  {action.Description}");
        }

        if (action.Commands.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("  ```");
            foreach (var command in action.Commands)
                builder.AppendLine($"  {command}");
            builder.AppendLine("  ```");
        }

        // The agent prompt goes to a different destination (the opened session), so it gets its own
        // fenced block after the terminal commands — keeping it out of prose also stops GitHub from
        // auto-linking a work-item id like owner/repo#42.
        if (!string.IsNullOrWhiteSpace(action.AgentPrompt))
        {
            builder.AppendLine();
            builder.AppendLine("  Then paste this into the opened agent session:");
            builder.AppendLine();
            builder.AppendLine("  ```");
            builder.AppendLine($"  {action.AgentPrompt}");
            builder.AppendLine("  ```");
        }

        builder.AppendLine();
    }

    private static string? Where(HandoverContent content)
    {
        var parts = new List<string>();
        if (content.Visibility != HandoverCommentMode.Minimal)
        {
            if (!string.IsNullOrWhiteSpace(content.Host))
                parts.Add($"host `{content.Host}`");
            if (!string.IsNullOrWhiteSpace(content.WorkspacePath))
                parts.Add($"workspace `{content.WorkspacePath}`");
        }

        if (!string.IsNullOrWhiteSpace(content.Branch))
            parts.Add($"branch `{content.Branch}`");
        return parts.Count == 0 ? null : string.Join(", ", parts);
    }

    private static string OutcomeLabel(RunOutcome outcome) => outcome switch
    {
        RunOutcome.Succeeded => "succeeded",
        RunOutcome.Failed => "failed",
        RunOutcome.Rejected => "rejected",
        _ => outcome.ToString().ToLowerInvariant()
    };

    private static string Excerpt(string message)
    {
        var trimmed = message.Trim();
        return trimmed.Length <= FinalMessageExcerptLength
            ? trimmed
            : trimmed[..FinalMessageExcerptLength] + "…";
    }
}
