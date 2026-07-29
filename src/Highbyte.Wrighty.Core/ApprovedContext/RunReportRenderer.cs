using System.Text;
using System.Text.Json;
using Highbyte.Wrighty.Models;
using Highbyte.Wrighty.Workers;

namespace Highbyte.Wrighty.ApprovedContext;

/// <summary>
/// Builds and renders the durable record of one worker run.
///
/// The split between what Wrighty observed and what the agent said is the whole point, and it is a
/// split in trust rather than in provenance. Wrighty owns the disposition, the process outcome and
/// the timing; the agent supplies narrative, and an agent that believes it finished cannot make a
/// run finished.
///
/// That labelling is load-bearing rather than decorative. Measured against a real run: asked to
/// report on work it had been *told* was complete, an agent wrote "Confirmed notes.md exists and
/// its contents are the single word hello" — a verification it had no way to perform. A reader
/// skims a verification line and believes it, so the boundary is marked where they will meet it,
/// not in a footnote.
/// </summary>
/// <summary>
/// What identifies one run: the item it belongs to, the run itself, and the agent that performed
/// it. Grouped because they always travel together and are always known together — the alternative
/// is three positional strings among five other arguments, where transposing two is silent.
/// </summary>
public readonly record struct RunIdentity(WorkItemId ItemId, string RunId, string AgentType);

public static class RunReportRenderer
{
    /// <summary>
    /// Combines what the worker observed with what the agent reported.
    /// </summary>
    /// <param name="runId">
    /// Identifies this run. The report id derives from it, so republishing after a failed request
    /// updates that run's comment rather than adding a second.
    /// </param>
    public static AgentRunReport Build(
        RunIdentity run,
        RunReportDisposition observed,
        AgentOutcome processOutcome,
        DateTimeOffset endedAt,
        AgentReportContent? reported,
        string? rawFallback = null)
    {
        var (itemId, runId, agentType) = run;
        return
        new(runId,
            AgentRunReport.DeriveReportId(itemId, runId),
            agentType,
            observed,
            processOutcome,
            endedAt,
            Summary: reported?.Summary,
            Changes: reported?.Changes,
            Verification: reported?.Verification,
            Decisions: reported?.Decisions,
            RequestedInput: reported?.RequestedInput,
            RemainingWork: reported?.RemainingWork,
            // Kept only when there is no structured report to render. Both would publish the same
            // account twice, once in fields and once as prose.
            AgentReportedBody: reported is null ? rawFallback : null);
    }

    /// <summary>
    /// The published comment body: a machine-readable marker, the facts Wrighty observed, and then
    /// the agent's account under a heading that says whose account it is.
    /// </summary>
    public static string Render(AgentRunReport report, WorkItemId itemId, string? branch = null)
    {
        var body = new StringBuilder();

        // The marker carries identity only, so a reader sees no duplicated content and an updater
        // can find this comment without parsing the prose below it.
        body.Append(AgentRunReport.MarkerPrefix).AppendLine();
        body.AppendLine(JsonSerializer.Serialize(new
        {
            itemId = itemId.Value,
            runId = report.RunId,
            reportId = report.ReportId,
            formatVersion = report.FormatVersion
        }));
        body.AppendLine("-->");
        body.AppendLine();

        body.AppendLine($"### Wrighty run report — {Describe(report.ObservedDisposition)}");
        body.AppendLine();
        body.AppendLine("**Observed by Wrighty**");
        body.AppendLine();
        body.AppendLine($"- Outcome: {Describe(report.ObservedDisposition)}");
        body.AppendLine($"- Agent: {report.AgentType}");
        body.AppendLine($"- Vendor process: {report.AgentProcessOutcome}");
        body.AppendLine($"- Ended: {report.EndedAt:u}");
        if (!string.IsNullOrWhiteSpace(branch))
            body.AppendLine($"- Branch: `{branch}`");
        body.AppendLine();

        if (report.IsObservedOnly)
        {
            // Said plainly. A silent gap invites a reader to assume the section failed to render
            // rather than that the agent supplied nothing.
            body.AppendLine("The agent reported nothing for this run.");
            return body.ToString().TrimEnd() + "\n";
        }

        body.AppendLine("**Agent-reported — the agent's own account, not verified by Wrighty**");
        body.AppendLine();
        if (!string.IsNullOrWhiteSpace(report.Summary))
        {
            body.AppendLine(report.Summary);
            body.AppendLine();
        }

        Section(body, "Changed", report.Changes);
        // Named for what it is. "Verification" alone reads as a fact established; this says who is
        // making the claim, right where the claim appears.
        Section(body, "Checks the agent says it ran", report.Verification);
        Section(body, "Decisions and assumptions", report.Decisions);
        Section(body, "Input requested", report.RequestedInput);
        Section(body, "Remaining work", report.RemainingWork);

        if (!string.IsNullOrWhiteSpace(report.AgentReportedBody))
        {
            body.AppendLine("**Agent's final response** (no structured report was provided)");
            body.AppendLine();
            body.AppendLine("```text");
            body.AppendLine(report.AgentReportedBody);
            body.AppendLine("```");
        }

        return body.ToString().TrimEnd() + "\n";
    }

    private static void Section(StringBuilder body, string heading, IReadOnlyList<string>? items)
    {
        if (items is null || items.Count == 0) return;
        body.AppendLine($"*{heading}*");
        body.AppendLine();
        foreach (var item in items)
            body.AppendLine($"- {item}");
        body.AppendLine();
    }

    private static string Describe(RunReportDisposition disposition) => disposition switch
    {
        RunReportDisposition.Finished => "finished",
        RunReportDisposition.NeedsAttention => "needs attention",
        RunReportDisposition.Failed => "failed",
        _ => "rejected"
    };
}
