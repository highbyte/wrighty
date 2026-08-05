using Highbyte.Wrighty.ApprovedContext;
using Highbyte.Wrighty.Models;
using Highbyte.Wrighty.Workers;

namespace Highbyte.Wrighty.UnitTests.ApprovedContext;

public class RunReportRendererTests
{
    private static readonly WorkItemId Id = new("github:owner/repo#42");
    private static readonly DateTimeOffset Ended = new(2026, 7, 28, 9, 30, 0, TimeSpan.Zero);

    private static AgentRunReport Report(
        AgentReportContent? reported,
        RunReportDisposition observed = RunReportDisposition.NeedsAttention,
        string? fallback = null) =>
        RunReportRenderer.Build(
            new RunIdentity(Id, "run-1", "claude"), observed, AgentOutcome.Succeeded, Ended,
            reported, fallback);

    [Fact]
    public void Build_keeps_observed_and_agent_reported_facts_separate()
    {
        var report = Report(new AgentReportContent(
            "Agent summary", Changes: ["a.cs"], Verification: ["dotnet test"]));

        Assert.Equal(RunReportDisposition.NeedsAttention, report.ObservedDisposition);
        Assert.Equal(AgentOutcome.Succeeded, report.AgentProcessOutcome);
        Assert.Equal("Agent summary", report.Summary);
        Assert.Equal(["a.cs"], report.Changes);
        Assert.Equal(["dotnet test"], report.Verification);
    }

    [Fact]
    public void Structured_report_does_not_also_store_the_raw_response()
    {
        var report = Report(new AgentReportContent("Structured."), fallback: "Raw text.");

        Assert.Null(report.AgentReportedBody);
    }

    [Fact]
    public void Unstructured_response_is_retained_as_the_fallback()
    {
        var report = Report(null, fallback: "I could not finish.");

        Assert.Equal("I could not finish.", report.AgentReportedBody);
    }

    [Fact]
    public void Marker_carries_identity_only_and_the_report_id_is_stable()
    {
        var first = Report(new AgentReportContent("x"));
        var second = Report(new AgentReportContent("y"));
        var marker = RunReportRenderer.RenderMarker(first, Id);

        Assert.Equal(first.ReportId, second.ReportId);
        Assert.Contains(AgentRunReport.MarkerPrefix, marker, StringComparison.Ordinal);
        Assert.Contains(first.ReportId, marker, StringComparison.Ordinal);
        Assert.DoesNotContain("summary", marker, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Automatic_trigger_is_stored_without_comment_content()
    {
        var trigger = new TrustedContinuationEvent(
            "reaction-9", TrustedContinuationSource.Reaction, "operator",
            Ended.AddMinutes(-1), Kind: TrustedContinuationKind.CompletionRequested,
            ConsumedAt: Ended.AddMinutes(-1), TriggeredRunId: "run-1");
        var report = RunReportRenderer.Build(
            new RunIdentity(Id, "run-1", "codex"),
            RunReportDisposition.NeedsAttention,
            AgentOutcome.Succeeded,
            Ended,
            reported: null,
            trigger: trigger);

        Assert.Equal(trigger, report.Trigger);
        Assert.DoesNotContain("body", RunReportRenderer.RenderMarker(report, Id),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Each_consumed_event_derives_a_distinct_stable_run_id()
    {
        var first = AgentRunReport.DeriveTriggeredRunId("vendor-session", "reaction:1");
        var retry = AgentRunReport.DeriveTriggeredRunId("vendor-session", "reaction:1");
        var next = AgentRunReport.DeriveTriggeredRunId("vendor-session", "reaction:2");

        Assert.Equal(first, retry);
        Assert.NotEqual(first, next);
    }
}
