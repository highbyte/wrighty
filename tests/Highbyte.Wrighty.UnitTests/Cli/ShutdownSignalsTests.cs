using System.Runtime.InteropServices;
using Highbyte.Wrighty.Cli;

namespace Highbyte.Wrighty.UnitTests.Cli;

/// <summary>
/// The decision logic of the signal bridge, exercised in-process. A test cannot deliver a real
/// signal to its own host without killing it, so the handler is driven directly; the end-to-end
/// path — cancellation unwinding the worker and removing its instance record — is covered by the
/// CLI cancellation test, and was verified against a live process for both SIGINT and SIGTERM.
/// </summary>
public sealed class ShutdownSignalsTests : IDisposable
{
    private readonly List<int> exits = [];
    private readonly List<FakeRegistration> registrations = [];
    private readonly ShutdownSignals signals;

    public ShutdownSignalsTests() =>
        signals = new ShutdownSignals(
            exits.Add,
            handler =>
            {
                registrations.Add(new FakeRegistration());
                registrations.Add(new FakeRegistration());
                return registrations.ToArray();
            });

    public void Dispose() => signals.Dispose();

    [Fact]
    public void TheFirstSignalCancelsTheTokenAndSuppressesTheDefaultDisposition()
    {
        var context = new PosixSignalContext(PosixSignal.SIGINT);

        signals.OnSignal(context);

        // Handled, not killed: the graceful path needs the process alive to run it.
        Assert.True(context.Cancel);
        Assert.True(signals.Token.IsCancellationRequested);
        Assert.Empty(exits);
    }

    [Fact]
    public void TheSecondSignalForcesTheExitWithTheInterruptedCode()
    {
        signals.OnSignal(new PosixSignalContext(PosixSignal.SIGINT));
        var second = new PosixSignalContext(PosixSignal.SIGINT);

        signals.OnSignal(second);

        // Still marked handled — the exit is ours, with a deliberate code, not the default kill.
        Assert.True(second.Cancel);
        Assert.Equal([ShutdownSignals.InterruptedExitCode], exits);
    }

    [Fact]
    public void ATerminateAfterAnInterruptCountsAsTheSecondSignal()
    {
        // An operator pressing Ctrl-C and a supervisor sending SIGTERM are one shutdown request
        // each; whichever arrives second is the order to stop waiting.
        signals.OnSignal(new PosixSignalContext(PosixSignal.SIGINT));
        signals.OnSignal(new PosixSignalContext(PosixSignal.SIGTERM));

        Assert.Equal([ShutdownSignals.InterruptedExitCode], exits);
    }

    [Fact]
    public void DisposalReleasesEveryRegistration()
    {
        signals.Dispose();

        Assert.Equal(2, registrations.Count);
        Assert.All(registrations, value => Assert.True(value.Disposed));
    }

    private sealed class FakeRegistration : IDisposable
    {
        public bool Disposed { get; private set; }

        public void Dispose() => Disposed = true;
    }
}
