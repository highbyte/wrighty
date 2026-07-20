using Highbyte.Wrighty.Workers;

namespace Highbyte.Wrighty.UnitTests.Workers;

public sealed class WorkerEventClassifierTests
{
    public static TheoryData<WorkerEventSemantic, string[]> Classifications => new()
    {
        {
            WorkerEventSemantic.Success,
            ["check", "finished", "workspace-removed"]
        },
        {
            WorkerEventSemantic.Info,
            ["info", "ready", "started", "resumed", "session", "dry-run"]
        },
        {
            WorkerEventSemantic.Warning,
            ["needs-attention", "workspace-busy", "skipped-claimed"]
        },
        {
            WorkerEventSemantic.Danger,
            ["failed", "fenced", "timed-out", "rejected"]
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
