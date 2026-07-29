using Highbyte.Wrighty.ApprovedContext;
using Highbyte.Wrighty.Models;
using Highbyte.Wrighty.Workers;

namespace Highbyte.Wrighty.UnitTests.ApprovedContext;

/// <summary>
/// The published record of one run. Its job is to keep two kinds of statement apart: what Wrighty
/// observed, and what the agent said about itself.
/// </summary>
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
    public void ObservedFactsAndAgentClaimsAreSeparatedAndLabelled()
    {
        var body = RunReportRenderer.Render(
            Report(new AgentReportContent("Did the thing.", Changes: ["a.cs"])), Id, "branch-1");

        Assert.Contains("**Observed by Wrighty**", body, StringComparison.Ordinal);
        Assert.Contains("**Agent-reported — the agent's own account, not verified by Wrighty**",
            body, StringComparison.Ordinal);
        Assert.True(
            body.IndexOf("Observed by Wrighty", StringComparison.Ordinal) <
            body.IndexOf("Agent-reported", StringComparison.Ordinal),
            "what Wrighty knows comes before what it was told");
    }

    [Fact]
    public void VerificationIsHeadedAsAClaimRatherThanAFact()
    {
        // Measured against a real run: an agent reported "Confirmed notes.md exists…" for a check it
        // never ran. A reader skims a verification line and believes it, so the heading has to say
        // who is claiming, at the point the claim appears.
        var body = RunReportRenderer.Render(
            Report(new AgentReportContent(Verification: ["dotnet test — all green"])), Id);

        Assert.Contains("Checks the agent says it ran", body, StringComparison.Ordinal);
        Assert.DoesNotContain("*Verification*", body, StringComparison.Ordinal);
    }

    [Fact]
    public void TheAgentsOutcomeClaimCannotReachTheRendering()
    {
        // The disposition rendered is the one Wrighty observed. A run whose agent insists it
        // finished still publishes as needs-attention.
        var body = RunReportRenderer.Render(
            Report(new AgentReportContent("All done and finished successfully!"),
                RunReportDisposition.NeedsAttention), Id);

        Assert.Contains("- Outcome: needs attention", body, StringComparison.Ordinal);
        Assert.DoesNotContain("- Outcome: finished", body, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEmptyReportSaysSoRatherThanRenderingAGap()
    {
        var body = RunReportRenderer.Render(Report(null), Id);

        Assert.Contains("The agent reported nothing for this run.", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Agent-reported —", body, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnstructuredResponseIsPublishedAsProseAndSaidToBeUnstructured()
    {
        var body = RunReportRenderer.Render(Report(null, fallback: "I could not finish."), Id);

        Assert.Contains("no structured report was provided", body, StringComparison.Ordinal);
        Assert.Contains("I could not finish.", body, StringComparison.Ordinal);
    }

    [Fact]
    public void AStructuredReportDoesNotAlsoPublishTheRawResponse()
    {
        // Both would say the same thing twice, once as fields and once as prose.
        var report = Report(new AgentReportContent("Structured."), fallback: "Raw text.");

        Assert.Null(report.AgentReportedBody);
        Assert.DoesNotContain("Raw text.", RunReportRenderer.Render(report, Id), StringComparison.Ordinal);
    }

    [Fact]
    public void TheMarkerCarriesIdentityOnlyAndTheReportIdIsStableAcrossAttempts()
    {
        // Republishing after a failed request must update this run's comment, not add another.
        var first = Report(new AgentReportContent("x"));
        var second = Report(new AgentReportContent("y"));
        var body = RunReportRenderer.Render(first, Id);

        Assert.Equal(first.ReportId, second.ReportId);
        Assert.Contains(AgentRunReport.MarkerPrefix, body, StringComparison.Ordinal);
        Assert.Contains(first.ReportId, body, StringComparison.Ordinal);
        Assert.DoesNotContain("\"summary\"", body, StringComparison.Ordinal);
    }
}
