using System.Runtime.InteropServices;

namespace Highbyte.Wrighty.Cli;

/// <summary>
/// The signal-to-cancellation bridge. Without it, SIGINT and SIGTERM end the process by their
/// default disposition — no unwinding, no disposal — so every cancellation-driven shutdown
/// behavior (worker loop exit, claim handling, worker-instance record removal) exists but is
/// unreachable from a terminal: Ctrl-C was simply a kill.
///
/// The first signal is marked handled and cancels <see cref="Token"/>, asking for the graceful
/// path. A second signal means the operator has stopped waiting for it, and forces the exit with
/// the conventional interrupted code. The instance lives for the whole invocation: disposing it
/// restores the default disposition, which mid-shutdown would turn the second signal's force-exit
/// back into an unclean kill.
/// </summary>
internal sealed class ShutdownSignals : IDisposable
{
    public const int InterruptedExitCode = 130;

    private readonly CancellationTokenSource source = new();
    private readonly Action<int> exit;
    private readonly IReadOnlyList<IDisposable> registrations;
    private int signals;

    /// <summary>Cancelled by the first SIGINT or SIGTERM; pass into the invocation.</summary>
    public CancellationToken Token => source.Token;

    public static ShutdownSignals Register() =>
        new(Environment.Exit, handler =>
        [
            PosixSignalRegistration.Create(PosixSignal.SIGINT, handler),
            PosixSignalRegistration.Create(PosixSignal.SIGTERM, handler)
        ]);

    // The registration source and the exit are injected so the decision logic is testable
    // in-process: a test cannot deliver a real signal to its own host without killing it, and a
    // real Environment.Exit would take the test runner with it.
    internal ShutdownSignals(
        Action<int> exit,
        Func<Action<PosixSignalContext>, IReadOnlyList<IDisposable>> register)
    {
        this.exit = exit;
        registrations = register(OnSignal);
    }

    internal void OnSignal(PosixSignalContext context)
    {
        // Always handled: the default disposition must never run while this bridge owns shutdown,
        // including for the second signal — that exit is ours, with a deliberate code.
        context.Cancel = true;
        if (Interlocked.Increment(ref signals) == 1)
            source.Cancel();
        else
            exit(InterruptedExitCode);
    }

    public void Dispose()
    {
        foreach (var registration in registrations)
            registration.Dispose();
        source.Dispose();
    }
}
