using Highbyte.Wrighty.Workers;
using Highbyte.Wrighty.Caching;

namespace Highbyte.Wrighty.UnitTests.Workers;

public sealed class WorkerRunControlTests
{
    [Fact]
    public void Drain_closes_intake_without_cancelling_the_active_run()
    {
        using var control = new WorkerRunControl();

        Assert.True(control.RequestDrain());

        Assert.True(control.IntakeClosed);
        Assert.True(control.IntakeToken.IsCancellationRequested);
        Assert.False(control.InterruptionToken.IsCancellationRequested);
        Assert.Equal(WorkerInterruptionReason.None, control.InterruptionReason);
    }

    [Fact]
    public void Stop_now_escalates_a_drain_and_preserves_the_strongest_request()
    {
        using var control = new WorkerRunControl();
        control.RequestDrain();

        Assert.True(control.RequestInterrupt(WorkerInterruptionReason.OperatorStopNow));

        Assert.True(control.InterruptionToken.IsCancellationRequested);
        Assert.Equal(
            WorkerInterruptionReason.OperatorStopNow,
            WorkerRunControl.ReasonFor(control.InterruptionToken));
        Assert.False(control.RequestInterrupt(WorkerInterruptionReason.HostShutdown));
        Assert.Equal(WorkerInterruptionReason.OperatorStopNow, control.InterruptionReason);
    }

    [Fact]
    public async Task Registration_completion_publishes_the_exact_run_identity()
    {
        using var control = new WorkerRunControl();
        Assert.False(control.RegistrationCompleted.IsCompleted);

        control.RunId = "hosted-run";

        Assert.Equal("hosted-run", await control.RegistrationCompleted);
        Assert.Equal("hosted-run", control.RunId);
    }

    [Fact]
    public void Interruption_journal_is_configuration_scoped_non_secret_and_removable()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"wrighty-interruption-journal-{Guid.NewGuid():N}");
        var configPath = Path.Combine(directory, "repo", ".wrighty.json");
        var otherConfigPath = Path.Combine(directory, "other", ".wrighty.json");
        var paths = new CachePaths(directory);
        var journal = new WorkerInterruptionJournal(paths);
        try
        {
            var path = journal.Write(
                "run-one",
                configPath,
                "local:42",
                "codex",
                "claim-token-must-not-appear",
                workspacePresent: true,
                sessionPresent: true,
                WorkerInterruptionReason.OperatorStopNow,
                DateTimeOffset.Parse("2026-08-23T12:00:00Z"));

            var pending = Assert.Single(
                WorkerInterruptionJournal.ListPending(paths, configPath));
            Assert.Equal("local:42", pending.ItemId);
            Assert.Empty(WorkerInterruptionJournal.ListPending(paths, otherConfigPath));
            Assert.DoesNotContain(
                "claim-token-must-not-appear",
                File.ReadAllText(path),
                StringComparison.Ordinal);

            WorkerInterruptionJournal.Complete(path);
            Assert.Empty(WorkerInterruptionJournal.ListPending(paths, configPath));
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }
}
