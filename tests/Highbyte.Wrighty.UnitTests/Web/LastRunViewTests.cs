using Highbyte.Wrighty.Claims;
using Highbyte.Wrighty.Web;

namespace Highbyte.Wrighty.UnitTests.Web;

public sealed class LastRunViewTests
{
    [Fact]
    public void From_returns_null_without_a_recorded_outcome()
    {
        Assert.Null(LastRunView.From(null));
        Assert.Null(LastRunView.From(new AgentSessionRecord(
            "claude", "s", "/tmp/ws", DateTimeOffset.UnixEpoch, true, "feature/x", null, null, null)));
    }

    [Theory]
    [InlineData(RunOutcome.Succeeded, "succeeded")]
    [InlineData(RunOutcome.Failed, "failed")]
    [InlineData(RunOutcome.Rejected, "rejected")]
    public void From_maps_the_outcome_label_and_carries_the_message(RunOutcome outcome, string label)
    {
        var view = LastRunView.From(new AgentSessionRecord(
            "codex", "s", "/tmp/ws", DateTimeOffset.UnixEpoch, true, "feature/x", outcome, "the message",
            DateTimeOffset.UnixEpoch));

        Assert.NotNull(view);
        Assert.Equal(outcome, view!.Outcome);
        Assert.Equal(label, view.Label);
        Assert.Equal("the message", view.FinalMessage);
        Assert.Equal(DateTimeOffset.UnixEpoch, view.EndedAt);
    }

    [Fact]
    public void The_label_prefers_what_wrighty_observed_over_the_vendors_process_result()
    {
        // A vendor that stops to ask a question still exits successfully. Labelling the panel from
        // that alone tells an operator the run succeeded when it is waiting on them.
        var report = Highbyte.Wrighty.ApprovedContext.RunReportRenderer.Build(
            new Highbyte.Wrighty.ApprovedContext.RunIdentity(
                new Highbyte.Wrighty.Models.WorkItemId("local:1"), "s", "claude"),
            Highbyte.Wrighty.ApprovedContext.RunReportDisposition.NeedsAttention,
            Highbyte.Wrighty.Workers.AgentOutcome.Succeeded, DateTimeOffset.UnixEpoch,
            new Highbyte.Wrighty.ApprovedContext.AgentReportContent("Paused for a decision."));

        var view = LastRunView.From(new AgentSessionRecord(
            "claude", "s", "/tmp/ws", DateTimeOffset.UnixEpoch, true, "feature/x",
            RunOutcome.Succeeded, "I need a decision.", DateTimeOffset.UnixEpoch,
            LastReport: report));

        Assert.Equal("needs attention", view!.Label);
        // The vendor's own result is still carried, for anything that needs it.
        Assert.Equal(RunOutcome.Succeeded, view.Outcome);
    }
}
