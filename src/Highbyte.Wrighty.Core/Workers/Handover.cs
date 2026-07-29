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
    DispatchInfo? Dispatch = null,
    ProviderCapacity? Provider = null,
    WorkItemPolicyPresentation? Policy = null);

/// <summary>Field-authoritative GitHub item policy shown alongside recovery guidance.</summary>
public sealed record WorkItemPolicyPresentation(
    bool AutomaticExecutionAllowed,
    string? AgentPolicy);

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
                $"**Recovery decision** — retry `{dispatch.SessionAgent ?? "agent"}` no earlier " +
                $"than `{dispatch.NotBefore:O}` (attempt {dispatch.Attempt} of " +
                $"{dispatch.MaxAttempts}).");
            builder.AppendLine();
        }

        if (content.Provider is { } provider)
        {
            builder.AppendLine($"**Provider capacity** — {ProviderSummary(provider)}");
            builder.AppendLine();
        }

        if (content.Policy is { } policy)
        {
            var execution = policy.AutomaticExecutionAllowed ? "Allowed" : "Manual only";
            var agentPolicy = string.IsNullOrWhiteSpace(policy.AgentPolicy)
                ? "Repository default"
                : AgentLabel(policy.AgentPolicy);
            builder.AppendLine(
                $"**Execution policy** — Automatic execution `{execution}`; agent " +
                $"`{agentPolicy}`. These values come from the authoritative Project fields; " +
                "the explicit item action below only overrides the retry timer/provider circuit.");
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
            + "hand-edit the `wrighty:dispatch-state` label; use Wrighty's CLI actions._");
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

    /// <summary>
    /// What happened, and where the session that it happened to now lives.
    ///
    /// "This machine" was written for a terminal, where it is unambiguous. Read on a GitHub issue
    /// by someone who was not at that terminal it names nothing at all — and the machine is not
    /// incidental, because only the host that recorded a session can resume it. So the host is
    /// named here, and <see cref="Where"/> no longer repeats it.
    /// </summary>
    private static string WhatHappened(HandoverContent content)
    {
        var host = HostPhrase(content);
        return content.Phase switch
        {
            HandoverPhase.NeedsAttention =>
                $"the agent session paused without finishing (run {OutcomeLabel(content.Outcome)}). " +
                $"It is retained {host} and can be clarified and resumed, or reopened. " +
                $"{OnlyThatHost(content)}",
            HandoverPhase.RetryScheduled =>
                $"the agent stopped because provider capacity is temporarily unavailable " +
                $"(run {OutcomeLabel(content.Outcome)}). Its vendor session and workspace are " +
                $"retained {host} for a bounded retry. {OnlyThatHost(content)}",
            _ =>
                $"the agent finished the item (run {OutcomeLabel(content.Outcome)}) and the work is " +
                $"retained {host} for review before it is integrated and archived."
        };
    }

    /// <summary>
    /// Where the session lives, named when the comment mode allows it. Minimal mode exists to keep
    /// host names off a shared tracker, so this says what still matters — that the machine is a
    /// specific one — without identifying it.
    /// </summary>
    private static string HostPhrase(HandoverContent content) =>
        content.Visibility != HandoverCommentMode.Minimal &&
        !string.IsNullOrWhiteSpace(content.Host)
            ? $"on host `{content.Host}`"
            : "on the machine that ran it";

    /// <summary>
    /// Its own sentence rather than a clause on the host, because it is a different fact and the
    /// two read as one long apposition when joined.
    /// </summary>
    private static string OnlyThatHost(HandoverContent content) =>
        content.Visibility != HandoverCommentMode.Minimal &&
        !string.IsNullOrWhiteSpace(content.Host)
            ? "That host is the only one that can resume it."
            : "That machine is the only one that can resume it.";

    private static void AppendAction(StringBuilder builder, WorkerOperatorAction action)
    {
        builder.AppendLine($"- **{action.Scenario}**");
        if (!string.IsNullOrWhiteSpace(action.Description))
        {
            builder.AppendLine();
            // Line by line, indented to stay inside the bullet. A description that carries numbered
            // steps has to render as a list; joined into one paragraph the steps stop looking like
            // steps, which is exactly the state a reader of this comment is least able to recover
            // from.
            foreach (var line in action.Description.Replace("\r\n", "\n").Split('\n'))
                builder.AppendLine($"  {line}");
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
        // The host is named in "What happened" above, where it explains why it matters.
        if (content.Visibility != HandoverCommentMode.Minimal &&
            !string.IsNullOrWhiteSpace(content.WorkspacePath))
            parts.Add($"workspace `{content.WorkspacePath}`");

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

    private static string ProviderSummary(ProviderCapacity provider)
    {
        var agent = AgentLabel(provider.Agent);
        var state = provider.State switch
        {
            ProviderCapacityState.UnavailableUntil => provider.UnavailableUntil is { } until
                ? $"`{agent}` is unavailable until `{until:O}`"
                : $"`{agent}` is unavailable",
            ProviderCapacityState.ProbeInProgress => provider.UnavailableUntil is { } due
                ? $"`{agent}` has a probe in progress until `{due:O}`"
                : $"`{agent}` has a probe in progress",
            _ => $"`{agent}` is available"
        };
        if (string.IsNullOrWhiteSpace(provider.Reason))
            return $"{state}.";

        var reason = InlineExcerpt(provider.Reason).TrimEnd();
        var terminator = reason.EndsWith('.') ||
                         reason.EndsWith('!') ||
                         reason.EndsWith('?')
            ? string.Empty
            : ".";
        return $"{state}. Sanitized reason: {reason}{terminator}";
    }

    private static string AgentLabel(string? agent)
    {
        if (string.IsNullOrWhiteSpace(agent))
            return "Agent";
        var normalized = agent.Trim().ToLowerInvariant();
        return normalized switch
        {
            "claude" => "Claude",
            "codex" => "Codex",
            "copilot" => "Copilot",
            _ => "Other"
        };
    }

    private static string InlineExcerpt(string value)
    {
        var sanitized = value
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Replace('`', '\'')
            .Trim();
        return sanitized.Length <= 240 ? sanitized : sanitized[..240] + "…";
    }

    /// <summary>
    /// The agent's closing words, with its report block removed.
    ///
    /// Two reasons, and the first is a rendering fault rather than a preference. This excerpt is
    /// wrapped in a fenced block, and the report the agent is now required to write is itself
    /// fenced — the inner fence closes the outer one, and everything after it escapes the code box
    /// and lands as raw markdown in the comment.
    ///
    /// The second is that the block says the same thing twice. When reports are published it
    /// appears again, structured and labelled, in its own comment; when they are not, the prose
    /// around it is what a person actually wants here. A reader of the handover is deciding what to
    /// do next, not reading a record.
    /// </summary>
    /// <summary>
    /// The agent's closing words, with its report block removed — see
    /// <see cref="ApprovedContext.AgentReportParser.WithoutReportBlock"/> for why every surface
    /// that quotes a final message has to do this.
    /// </summary>
    private static string Excerpt(string message)
    {
        var trimmed = ApprovedContext.AgentReportParser.WithoutReportBlock(message);
        if (trimmed is null)
        {
            // The agent wrote the block and nothing else. Say so rather than rendering an empty
            // quote, which reads as the agent having returned nothing at all.
            return "(the agent's response was its structured report; see the run report for it)";
        }

        return trimmed.Length <= FinalMessageExcerptLength
            ? trimmed
            : trimmed[..FinalMessageExcerptLength] + "…";
    }
}
