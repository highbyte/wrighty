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
        string? rawFallback = null,
        TrustedContinuationEvent? trigger = null)
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
            AgentReportedBody: reported is null ? rawFallback : null,
            Trigger: trigger);
    }

    /// <summary>
    /// The strict content-free identity marker shared by the durable report and the combined
    /// GitHub handover. Keeping it independent of the visible rendering lets the control-reaction
    /// reader authenticate one current comment without requiring a second report comment.
    /// </summary>
    public static string RenderMarker(AgentRunReport report, WorkItemId itemId)
    {
        var body = new StringBuilder();
        body.Append(AgentRunReport.MarkerPrefix).AppendLine();
        body.AppendLine(JsonSerializer.Serialize(new
        {
            itemId = itemId.Value,
            runId = report.RunId,
            reportId = report.ReportId,
            formatVersion = report.FormatVersion
        }));
        body.Append("-->");
        return body.ToString();
    }

}
