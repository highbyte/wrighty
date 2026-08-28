using Highbyte.Wrighty.Workers;

namespace Highbyte.Wrighty.UnitTests.Workers;

public sealed class WorkerEventClassifierTests
{
    public static TheoryData<WorkerEventSemantic, string[]> Classifications => new()
    {
        {
            WorkerEventSemantic.Success,
            ["check", "finished", "workspace-removed", "requirements-assessment-ready"]
        },
        {
            WorkerEventSemantic.Info,
            ["info", "ready", "preparing", "started", "resumed", "session", "dry-run",
                "requirements-assessment-started"]
        },
        {
            WorkerEventSemantic.Warning,
            ["needs-attention", "workspace-busy", "skipped-claimed",
                "requirements-assessment-disabled",
                "requirements-assessment-needs-clarification"]
        },
        {
            WorkerEventSemantic.Danger,
            ["failed", "fenced", "timed-out", "rejected",
                "requirements-assessment-invalid", "requirements-assessment-unavailable",
                "requirements-assessment-invalidated"]
        },
        {
            WorkerEventSemantic.Muted,
            ["idle", "no-item", "running", "renewed", "waiting"]
        }
    };

    [Theory]
    [MemberData(nameof(Classifications))]
    public void Classifies_every_current_worker_event_type(
        WorkerEventSemantic expected,
        string[] eventTypes)
    {
        foreach (var eventType in eventTypes)
            Assert.Equal(expected, WorkerEventClassifier.Classify(eventType));
    }

    [Fact]
    public void Unknown_event_types_are_unclassified()
    {
        Assert.Null(WorkerEventClassifier.Classify("future-event"));
    }
}
