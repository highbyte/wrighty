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
    HandoffQueued,
    Completed
}

/// <summary>
/// The backend-neutral payload for a run handover: what happened, where the session lives, and the
/// exact next-step commands. Rendered to a single overwrite-style GitHub issue comment today; the
/// same content is the natural body for a future Slack notification (plan 016) or cross-agent
/// handoff (plan 026), so it deliberately carries data, not GitHub markup.
/// <c>RequiresUserConfirmation</c> distinguishes a run that stopped because this repository holds
/// completion for a person from one where the agent could not finish.
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
    WorkItemPolicyPresentation? Policy = null,
    bool RequiresUserConfirmation = false,
    ApprovedContext.AgentRunReport? Report = null,
    WorkerContinuationConfig? Continuation = null,
    IReadOnlyList<string>? TrustedAuthors = null,
    ApprovedContext.TrustedContinuationBudget? ContinuationBudget = null);

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
        if (content.Report is { } report)
            builder.AppendLine(ApprovedContext.RunReportRenderer.RenderMarker(report, content.Id));
        builder.AppendLine(content.Phase switch
        {
            HandoverPhase.NeedsAttention => "### Wrighty — needs attention",
            HandoverPhase.RetryScheduled => "### Wrighty — retry scheduled",
            HandoverPhase.HandoffQueued => "### Wrighty — cross-agent handoff queued",
            _ => "### Wrighty — completed, work retained for review"
        });
        builder.AppendLine();

        AppendCurrentResult(builder, content);
        if (content.Phase == HandoverPhase.NeedsAttention)
        {
            AppendNeedsAttentionContext(builder, content);
            AppendContinuationChoices(builder, content);
        }

        AppendRunDetails(builder, content);

        if (content.Actions.Count > 0)
        {
            builder.AppendLine("<details>");
            builder.AppendLine("<summary>Other recovery options</summary>");
            builder.AppendLine();
            foreach (var action in content.Actions)
                AppendAction(builder, action);
            builder.AppendLine("</details>");
            builder.AppendLine();
        }

        builder.Append("_Wrighty keeps one current status comment and replaces it after each run._");
        return builder.ToString();
    }

    private static void AppendCurrentResult(StringBuilder builder, HandoverContent content)
    {
        var report = content.Report;
        var agent = AgentLabel(report?.AgentType);
        if (report?.RequestedInput is { Count: > 0 } requested)
        {
            builder.AppendLine($"**{agent} needs:**");
            builder.AppendLine();
            foreach (var item in requested)
                builder.AppendLine($"- {item}");
            builder.AppendLine();
            return;
        }

        if (!string.IsNullOrWhiteSpace(report?.Summary))
        {
            builder.AppendLine($"**{agent} reports:** {report.Summary}");
            builder.AppendLine();
            return;
        }

        var fallback = report?.AgentReportedBody ?? content.FinalMessage;
        if (!string.IsNullOrWhiteSpace(fallback))
        {
            builder.AppendLine($"**{agent}'s final response:**");
            builder.AppendLine();
            builder.AppendLine("```text");
            builder.AppendLine(Excerpt(fallback));
            builder.AppendLine("```");
            builder.AppendLine();
            return;
        }

        if (content.Dispatch is { } dispatch)
        {
            builder.AppendLine(dispatch.State == Models.DispatchStates.HandoffQueued
                ? $"**Handoff:** to `{dispatch.Agent ?? "agent"}` in the retained workspace " +
                  $"(recovery attempt {dispatch.Attempt} of {dispatch.MaxAttempts})."
                : $"**Retry:** `{dispatch.SessionAgent ?? "agent"}` no earlier than " +
                  $"`{dispatch.NotBefore:O}` (attempt {dispatch.Attempt} of {dispatch.MaxAttempts}).");
            builder.AppendLine();
            return;
        }

        builder.AppendLine(content.Phase switch
        {
            HandoverPhase.NeedsAttention => "The retained agent session needs an operator decision.",
            HandoverPhase.RetryScheduled => "The retained agent session is waiting for a retry.",
            HandoverPhase.HandoffQueued =>
                "The retained workspace is queued to continue under a different agent.",
            _ => "The work is retained for review."
        });
        builder.AppendLine();
    }

    private static void AppendContinuationChoices(StringBuilder builder, HandoverContent content)
    {
        var authors = content.TrustedAuthors?
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray() ?? [];
        if (authors.Length == 0)
        {
            builder.AppendLine(
                "Reply to the issue with the requested information. A context approver must then " +
                "approve that reply before a worker can continue the session.");
            builder.AppendLine();
            return;
        }

        if (content.ContinuationBudget is { IsExhausted: true } budget)
        {
            builder.AppendLine(
                $"Automatic continuation has reached its limit of " +
                $"{budget.MaxAutomaticContinuations}. Reply with the requested information, then " +
                "use one of the manual recovery options below.");
            builder.AppendLine();
            return;
        }

        var continuation = content.Continuation ?? new WorkerContinuationConfig();
        var agent = AgentLabel(content.Report?.AgentType);
        builder.AppendLine("**Continue this item — choose one:**");
        builder.AppendLine();
        builder.AppendLine(
            $"_Automatic controls are accepted from {string.Join(", ", authors.Select(Author))}._");
        builder.AppendLine();
        if (continuation.RequiresCommand)
        {
            builder.AppendLine(
                $"- **Answer {agent}:** Reply with `{continuation.Command}` as the first line and " +
                "put the requested information below it. That reply alone continues the retained " +
                "session; do not also react.");
        }
        else
        {
            builder.AppendLine(
                $"- **Answer {agent}:** Reply to the issue with the requested information. That " +
                "reply alone continues the retained session; do not also react.");
        }
        builder.AppendLine(
            $"- **Continue without adding information:** React " +
            $"{ApprovedContext.ReactionKinds.Glyph(continuation.ResumeReaction)} to this Wrighty " +
            "comment.");
        builder.AppendLine(
            $"- **Verify and finish:** React " +
            $"{ApprovedContext.ReactionKinds.Glyph(continuation.CompletionReaction)} to this " +
            $"Wrighty comment to ask {agent} to verify the work and finish through Wrighty's " +
            "normal checks.");
        builder.AppendLine();
    }

    private static void AppendNeedsAttentionContext(
        StringBuilder builder,
        HandoverContent content)
    {
        if (content.RequiresUserConfirmation)
        {
            builder.AppendLine(
                "This repository expects the agent to stop for a person to confirm completion. " +
                "A blocked run and work the agent considers complete therefore look alike here. " +
                "Read its report above. Reply to accept or clarify the work.");
        }
        else
        {
            builder.AppendLine(
                "The retained agent session paused without finishing and needs one of the choices " +
                "below.");
        }
        builder.AppendLine();
    }

    private static string Author(string value) => $"@{value.Trim().TrimStart('@')}";

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

    private static void AppendRunDetails(StringBuilder builder, HandoverContent content)
    {
        var report = content.Report;
        builder.AppendLine("<details>");
        builder.AppendLine("<summary>Run and session details</summary>");
        builder.AppendLine();
        builder.AppendLine($"- Agent: {AgentLabel(report?.AgentType)}");
        builder.AppendLine($"- Vendor process: {OutcomeLabel(content.Outcome)}");
        if (report is not null)
        {
            builder.AppendLine($"- Wrighty disposition: " +
                               $"{DescribeDisposition(report.ObservedDisposition)}");
            builder.AppendLine($"- Ended: `{report.EndedAt:u}`");
            if (report.Trigger is { } trigger)
            {
                builder.AppendLine(
                    $"- Continuation trigger: {trigger.Describe()} " +
                    $"(`{trigger.TriggerMode}`, `{trigger.ConsumptionKey}`)");
            }
        }

        builder.AppendLine($"- Session: {SessionLocation(content)}");
        if (Where(content) is { } where)
            builder.AppendLine($"- Work: {where}");
        if (content.Dispatch is { } dispatch)
        {
            builder.AppendLine(dispatch.State == Models.DispatchStates.HandoffQueued
                ? $"- Handoff: to `{dispatch.Agent ?? "agent"}` in the retained workspace " +
                  $"(recovery attempt {dispatch.Attempt} of {dispatch.MaxAttempts})"
                : $"- Retry: `{dispatch.SessionAgent ?? "agent"}` no earlier than " +
                  $"`{dispatch.NotBefore:O}` (attempt {dispatch.Attempt} of " +
                  $"{dispatch.MaxAttempts})");
        }
        if (content.Provider is { } provider)
            builder.AppendLine($"- Provider capacity: {ProviderSummary(provider)}");
        if (content.Policy is { } policy)
        {
            builder.AppendLine(
                $"- Policy: automatic execution " +
                $"`{(policy.AutomaticExecutionAllowed ? "Allowed" : "Denied")}`" +
                (string.IsNullOrWhiteSpace(policy.AgentPolicy)
                    ? string.Empty
                    : $", agent `{AgentLabel(policy.AgentPolicy)}`"));
        }
        builder.AppendLine();

        if (report is not null)
        {
            var hasAgentDetails =
                (report.Changes?.Count ?? 0) > 0 ||
                (report.Verification?.Count ?? 0) > 0 ||
                (report.Decisions?.Count ?? 0) > 0 ||
                (report.RemainingWork?.Count ?? 0) > 0;
            if (hasAgentDetails)
            {
                builder.AppendLine(
                    "_The following details are reported by the agent and are not independently " +
                    "verified by Wrighty._");
                builder.AppendLine();
                AppendReportSection(builder, "Changed", report.Changes);
                AppendReportSection(
                    builder, "Checks the agent says it ran", report.Verification);
                AppendReportSection(builder, "Decisions and assumptions", report.Decisions);
                AppendReportSection(builder, "Remaining work", report.RemainingWork);
            }
        }

        builder.AppendLine("</details>");
        builder.AppendLine();
    }

    private static void AppendReportSection(
        StringBuilder builder,
        string heading,
        IReadOnlyList<string>? items)
    {
        if (items is not { Count: > 0 }) return;
        builder.AppendLine($"**{heading}**");
        builder.AppendLine();
        foreach (var item in items)
            builder.AppendLine($"- {item}");
        builder.AppendLine();
    }

    private static string SessionLocation(HandoverContent content)
    {
        if (content.Visibility != HandoverCommentMode.Minimal &&
            !string.IsNullOrWhiteSpace(content.Host))
        {
            return $"retained on host `{content.Host}`; only that host can resume it";
        }

        return "retained on the machine that ran it; only that machine can resume it";
    }

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

    private static string DescribeDisposition(
        ApprovedContext.RunReportDisposition disposition) => disposition switch
        {
            ApprovedContext.RunReportDisposition.Finished => "finished",
            ApprovedContext.RunReportDisposition.NeedsAttention => "needs attention",
            ApprovedContext.RunReportDisposition.Failed => "failed",
            _ => "rejected"
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
            return "(the agent's response consisted only of its structured report)";
        }

        return trimmed.Length <= FinalMessageExcerptLength
            ? trimmed
            : trimmed[..FinalMessageExcerptLength] + "…";
    }
}
