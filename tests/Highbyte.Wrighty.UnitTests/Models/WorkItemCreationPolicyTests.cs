using Highbyte.Wrighty.Configuration;
using Highbyte.Wrighty.Errors;
using Highbyte.Wrighty.Models;

namespace Highbyte.Wrighty.UnitTests.Models;

public sealed class WorkItemCreationPolicyTests
{
    private static readonly TrackerConfig Config = new()
    {
        DefaultCreateStatus = "Todo",
        DefaultPickFrom = "Worker queue",
        DefaultPickTo = "In Progress",
        DefaultFinishTo = "Done",
        Archive = new ArchiveConfig { OnStatuses = ["Cancelled"] }
    };

    [Fact]
    public void AllowedStatuses_retains_entry_states_and_removes_lifecycle_destinations()
    {
        var allowed = WorkItemCreationPolicy.AllowedStatuses(
            Config,
            ["Todo", "Triage", "Worker queue", "In Progress", "Done", "Cancelled"]);

        Assert.Equal(["Todo", "Triage", "Worker queue"], allowed);
    }

    [Theory]
    [InlineData("In Progress", "active-work destination")]
    [InlineData("Done", "completion destination")]
    [InlineData("Cancelled", "archive-triggering terminal status")]
    public void EnsureAllowed_rejects_non_entry_statuses(string status, string reason)
    {
        var exception = Assert.Throws<TrackerException>(() =>
            WorkItemCreationPolicy.EnsureAllowed(Config, status));

        Assert.Equal("ARGUMENT_INVALID", exception.Code);
        Assert.Equal(2, exception.ExitCode);
        Assert.Contains(reason, exception.Message);
    }
}
