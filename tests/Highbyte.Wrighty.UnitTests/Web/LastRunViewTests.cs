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
}
