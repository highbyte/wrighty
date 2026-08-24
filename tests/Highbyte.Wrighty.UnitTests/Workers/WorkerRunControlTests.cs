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
    public async Task Disposing_before_registration_cancels_waiters_and_is_idempotent()
    {
        var control = new WorkerRunControl();
        var interruptionToken = control.InterruptionToken;

        control.Dispose();
        control.Dispose();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await control.RegistrationCompleted);
        Assert.Equal(
            WorkerInterruptionReason.None,
            WorkerRunControl.ReasonFor(interruptionToken));
    }

    [Fact]
    public void Interruption_validates_reason_and_runs_preparation_once()
    {
        using var control = new WorkerRunControl();
        var observed = WorkerInterruptionReason.None;
        using var preparation = control.PrepareInterruption(reason => observed = reason);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            control.RequestInterrupt(WorkerInterruptionReason.None));
        Assert.True(control.RequestInterrupt(WorkerInterruptionReason.HostShutdown));
        Assert.Equal(WorkerInterruptionReason.HostShutdown, observed);
        Assert.Equal(WorkerInterruptionReason.HostShutdown, control.InterruptionReason);
    }

    [Fact]
    public void Late_preparation_observes_an_existing_interrupt_and_io_does_not_block_it()
    {
        using var control = new WorkerRunControl();
        Assert.True(control.RequestInterrupt(WorkerInterruptionReason.OperatorStopNow));

        Assert.Throws<IOException>(() =>
            control.PrepareInterruption(_ => throw new IOException("late write failed")));

        using var second = new WorkerRunControl();
        using var preparation = second.PrepareInterruption(
            _ => throw new UnauthorizedAccessException("write denied"));
        Assert.True(second.RequestInterrupt(WorkerInterruptionReason.OperatorStopNow));
        Assert.True(second.IsInterrupted);
    }

    [Theory]
    [InlineData("started")]
    [InlineData("resumed")]
    [InlineData("running")]
    [InlineData("session")]
    public void Worker_instance_projection_tracks_running_events(string type)
    {
        using var control = new WorkerRunControl();

        var projected = WorkerInstanceEventProjection.Project(
            new WorkerEvent(type, "local:42", "codex"),
            control);

        Assert.NotNull(projected);
        Assert.Equal("local:42", projected.ItemId);
        Assert.Equal("codex", projected.Agent);
        Assert.Equal(WorkerInstanceState.RunningItem, projected.State);

        control.RequestDrain();
        projected = WorkerInstanceEventProjection.Project(
            new WorkerEvent(type, "local:42", "codex"),
            control);
        Assert.Equal(WorkerInstanceState.Draining, projected!.State);
    }

    [Theory]
    [InlineData("finished")]
    [InlineData("needs-attention")]
    [InlineData("failed")]
    [InlineData("fenced")]
    [InlineData("timed-out")]
    [InlineData("rejected")]
    [InlineData("retry-scheduled")]
    [InlineData("interrupted")]
    public void Worker_instance_projection_tracks_terminal_events(string type)
    {
        using var idleControl = new WorkerRunControl();
        var idle = WorkerInstanceEventProjection.Project(new WorkerEvent(type), idleControl);
        Assert.Equal(WorkerInstanceState.Idle, idle!.State);
        Assert.Null(idle.ItemId);

        using var drainingControl = new WorkerRunControl();
        drainingControl.RequestDrain();
        var draining = WorkerInstanceEventProjection.Project(
            new WorkerEvent(type),
            drainingControl);
        Assert.Equal(WorkerInstanceState.Draining, draining!.State);

        using var interruptedControl = new WorkerRunControl();
        interruptedControl.RequestInterrupt(WorkerInterruptionReason.OperatorStopNow);
        var finalizing = WorkerInstanceEventProjection.Project(
            new WorkerEvent(type),
            interruptedControl);
        Assert.Equal(WorkerInstanceState.Finalizing, finalizing!.State);
    }

    [Fact]
    public void Worker_instance_projection_ignores_non_lifecycle_events_and_missing_item_ids()
    {
        using var control = new WorkerRunControl();

        Assert.Null(WorkerInstanceEventProjection.Project(new WorkerEvent("idle"), control));
        Assert.Null(WorkerInstanceEventProjection.Project(new WorkerEvent("started"), control));
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
                new WorkerInterruptionIdentity(
                    "run-one",
                    configPath,
                    "local:42",
                    "codex",
                    "claim-token-must-not-appear"),
                new WorkerInterruptionSnapshot(
                    WorkspacePresent: true,
                    SessionPresent: true,
                    WorkerInterruptionReason.OperatorStopNow,
                    DateTimeOffset.Parse("2026-08-23T12:00:00Z")));

            var pending = Assert.Single(
                WorkerInterruptionJournal.ListPending(paths, configPath));
            Assert.Equal("local:42", pending.ItemId);
            Assert.Equal("codex", pending.Agent);
            Assert.Equal(WorkerInterruptionReason.OperatorStopNow, pending.Reason);
            Assert.Equal(DateTimeOffset.Parse("2026-08-23T12:00:00Z"), pending.OccurredAt);
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

    [Fact]
    public void Interruption_journal_ignores_missing_and_corrupt_records()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"wrighty-interruption-journal-invalid-{Guid.NewGuid():N}");
        var paths = new CachePaths(directory);
        var configPath = Path.Combine(directory, "repo", ".wrighty.json");
        try
        {
            Assert.Empty(WorkerInterruptionJournal.ListPending(paths, configPath));

            Directory.CreateDirectory(paths.WorkerInterruptionsRoot);
            File.WriteAllText(
                Path.Combine(paths.WorkerInterruptionsRoot, "bad.json"),
                "{bad-json");

            Assert.Empty(WorkerInterruptionJournal.ListPending(paths, configPath));
            WorkerInterruptionJournal.Complete(null);
            WorkerInterruptionJournal.Complete(Path.Combine(directory, "missing.json"));
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }
}
