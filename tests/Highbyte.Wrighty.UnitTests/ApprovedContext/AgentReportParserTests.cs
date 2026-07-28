using Highbyte.Wrighty.ApprovedContext;
using Highbyte.Wrighty.Workers;

namespace Highbyte.Wrighty.UnitTests.ApprovedContext;

/// <summary>
/// Reading the report block an agent was asked to end with. The input is a language model's best
/// effort at a format, not a serialized object, so nearly every test here is a way that effort goes
/// wrong — and none of them may turn a run that did real work into a broken one.
/// </summary>
public class AgentReportParserTests
{
    private static string Block(string json) => $"Here is what I did.\n\n```wrighty-report\n{json}\n```";

    [Fact]
    public void AWellFormedBlockIsRead()
    {
        var report = AgentReportParser.TryParse(Block("""
            {
              "summary": "Made the retry budget configurable.",
              "changes": ["WorkerConfig", "docs/reference/configuration.md"],
              "verification": ["dotnet test — 1166 passed"],
              "decisions": ["Two approved entries disagreed on the cap; followed the later one."],
              "requestedInput": [],
              "remainingWork": ["Wire the CLI flag"],
              "references": ["branch wrighty-worker/42"]
            }
            """));

        Assert.NotNull(report);
        Assert.Equal("Made the retry budget configurable.", report!.Summary);
        Assert.Equal(2, report.Changes!.Count);
        Assert.Contains("followed the later one", report.Decisions![0], StringComparison.Ordinal);
        // An empty array is nothing said, not an empty thing said.
        Assert.Null(report.RequestedInput);
    }

    [Theory]
    [InlineData("no block at all, just prose")]
    [InlineData("```wrighty-report\nnot json {{{\n```")]
    [InlineData("```wrighty-report\n[1,2,3]\n```")]
    [InlineData("```wrighty-report\n\n```")]
    [InlineData("```json\n{\"summary\":\"wrong fence tag\"}\n```")]
    [InlineData("")]
    [InlineData(null)]
    public void AnythingUnusableReadsAsNoReportRatherThanAnError(string? message)
    {
        // Each of these is something an agent plausibly produces. None may throw, and none may be
        // mistaken for a report: the worker still has its own observed facts either way.
        Assert.Null(AgentReportParser.TryParse(message));
    }

    [Fact]
    public void ABlockWhoseNewlinesWereLostInTransportIsStillRead()
    {
        // Measured against a live Copilot run: the agent produced a correct block, and the adapter
        // put the whole final message through a failure sanitizer that collapses whitespace, so it
        // arrived on one line. The adapter no longer does that — but the report was right and the
        // agent had no way to know, so refusing it here would blame the wrong party.
        var report = AgentReportParser.TryParse(
            """Done! ```wrighty-report { "summary": "Added the section.", "changes": ["README.md"] } ```""");

        Assert.Equal("Added the section.", report!.Summary);
        Assert.Equal(["README.md"], report.Changes);
    }

    [Fact]
    public void TheLastBlockWins()
    {
        // An agent that shows an example and then writes its real report ends with the one it meant.
        var report = AgentReportParser.TryParse(
            Block("""{"summary":"the example"}""") + "\n" +
            Block("""{"summary":"the real one"}"""));

        Assert.Equal("the real one", report!.Summary);
    }

    [Fact]
    public void AStringWhereAListWasAskedForIsStillRead()
    {
        // The agent said something useful in the wrong shape. Discarding it would lose content over
        // a formatting slip.
        var report = AgentReportParser.TryParse(Block("""{"changes":"one file"}"""));

        Assert.Equal(["one file"], report!.Changes);
    }

    [Fact]
    public void OversizedFieldsAreBoundedAndMarked()
    {
        // A report is published where collaborators read it. An agent that pastes a log into a field
        // must not turn that comment into the log.
        var report = AgentReportParser.TryParse(Block(
            $$"""{"summary":"{{new string('x', 5_000)}}","changes":["{{new string('y', 5_000)}}"]}"""));

        Assert.True(report!.Summary!.Length <= AgentReportParser.MaxSummaryCharacters + 1);
        Assert.EndsWith("…", report.Summary, StringComparison.Ordinal);
        Assert.True(report.Changes![0].Length <= AgentReportParser.MaxItemCharacters + 1);
    }

    [Fact]
    public void TooManyItemsAreCapped()
    {
        var many = string.Join(",", Enumerable.Range(0, 100).Select(i => $"\"item {i}\""));
        var report = AgentReportParser.TryParse(Block($$"""{"changes":[{{many}}]}"""));

        Assert.Equal(AgentReportParser.MaxItemsPerField, report!.Changes!.Count);
    }

    [Fact]
    public void AnOutcomeTheAgentInventsIsNotRead()
    {
        // The contract never asks for one. Reading it would let a run claim to be finished, which is
        // Wrighty's observation to make and no one else's.
        var report = AgentReportParser.TryParse(Block(
            """{"summary":"done","outcome":"finished","disposition":"success"}"""));

        Assert.NotNull(report);
        Assert.Equal("done", report!.Summary);
        // The record has nowhere to put either, so they cannot reach a caller.
        Assert.DoesNotContain("finished", report.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ARunResultExposesItsReportWithoutTheAdapterHavingToRemember()
    {
        var result = new AgentRunResult(
            AgentOutcome.Succeeded, "s1", Block("""{"summary":"parsed from the result"}"""));

        Assert.Equal("parsed from the result", result.Report!.Summary);
        Assert.Null(result.ReportFallback);
    }

    [Fact]
    public void AResponseWithNoBlockKeepsABoundedFallbackInstead()
    {
        var result = new AgentRunResult(
            AgentOutcome.Succeeded, "s1", "I could not finish: the build is broken.");

        Assert.Null(result.Report);
        Assert.Equal("I could not finish: the build is broken.", result.ReportFallback);
    }

    [Fact]
    public void AVeryLongResponseWithNoBlockIsTruncatedVisibly()
    {
        // A record that simply stops is indistinguishable from an agent that stopped.
        var result = new AgentRunResult(
            AgentOutcome.Succeeded, "s1", new string('z', 10_000));

        Assert.Contains("(truncated)", result.ReportFallback!, StringComparison.Ordinal);
        Assert.True(result.ReportFallback!.Length < 10_000);
    }

    [Fact]
    public void StrippingTheBlockLeavesTheProseEverySurfaceQuotes()
    {
        var stripped = AgentReportParser.WithoutReportBlock(
            "I paused for a decision.\n\n```wrighty-report\n{\"summary\":\"x\"}\n```");

        Assert.Equal("I paused for a decision.", stripped);
    }

    [Fact]
    public void AResponseThatIsOnlyABlockStripsToNothing()
    {
        // The caller decides what to say instead — a quote of an empty string reads as an agent
        // that returned nothing at all, which is a different thing.
        Assert.Null(AgentReportParser.WithoutReportBlock("```wrighty-report\n{\"summary\":\"x\"}\n```"));
    }

    [Fact]
    public void StrippingLeavesAResponseWithNoBlockAlone()
    {
        Assert.Equal("Just prose.", AgentReportParser.WithoutReportBlock("  Just prose.  "));
    }
}
