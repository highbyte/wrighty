using System.Collections.Concurrent;
using Highbyte.Wrighty.Configuration;
using Highbyte.Wrighty.Models;

namespace Highbyte.Wrighty.Workers;

public enum WorkerInterruptionReason
{
    None,
    OperatorStopNow,
    HostShutdown
}

public sealed record WorkerRunSelection(
    WorkItemId? ItemId,
    WorkerItemIntent Intent = WorkerItemIntent.Auto,
    string? ClaimToken = null);

public sealed record WorkerRunIdentity(
    string RepositoryPath,
    string ConfigurationPath,
    string ConfigurationRevision,
    string InvocationSummary,
    WorkerHostKind HostKind);

public sealed record WorkerRunCallbacks(
    Func<WorkerEvent, Task> Emit,
    Func<string, Task>? Warn);

internal sealed record WorkerInstanceEventState(
    string? ItemId,
    string? Agent,
    WorkerInstanceState State);

internal static class WorkerInstanceEventProjection
{
    public static WorkerInstanceEventState? Project(
        WorkerEvent value,
        WorkerRunControl control)
    {
        var running = value.Type is "started" or "resumed" or "running" or "session";
        var terminal = value.Type is "finished" or "needs-attention" or "failed" or "fenced"
            or "timed-out" or "rejected" or "retry-scheduled" or "interrupted";
        if (running && value.ItemId is not null)
        {
            return new WorkerInstanceEventState(
                value.ItemId,
                value.Agent,
                control.IntakeClosed
                    ? WorkerInstanceState.Draining
                    : WorkerInstanceState.RunningItem);
        }
        if (!terminal)
            return null;

        var terminalState = WorkerInstanceState.Idle;
        if (control.IsInterrupted)
            terminalState = WorkerInstanceState.Finalizing;
        else if (control.IntakeClosed)
            terminalState = WorkerInstanceState.Draining;
        return new WorkerInstanceEventState(null, null, terminalState);
    }
}

/// <summary>
/// Separates closing worker intake from cancelling an active agent process. A drain only closes
/// intake; an interruption closes intake and cancels the current run.
/// </summary>
public sealed class WorkerRunControl : IDisposable
{
    private static readonly ConcurrentDictionary<CancellationToken, WorkerRunControl> Controls = [];
    private readonly CancellationTokenSource intake = new();
    private readonly CancellationTokenSource interruption = new();
    private readonly TaskCompletionSource<string> registration = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object preparationGate = new();
    private Action<WorkerInterruptionReason>? interruptionPreparation;
    private string? runId;
    private int state;
    private bool disposed;

    public WorkerRunControl()
    {
        Controls[interruption.Token] = this;
    }

    public string? RunId
    {
        get => Volatile.Read(ref runId);
        internal set
        {
            Volatile.Write(ref runId, value);
            if (value is not null)
                registration.TrySetResult(value);
        }
    }

    /// <summary>
    /// Completes after the worker's exact registry identity has been persisted. Owners that expose
    /// the run immediately can await this instead of rendering a transient, unaddressable worker.
    /// </summary>
    public Task<string> RegistrationCompleted => registration.Task;

    public bool IntakeClosed => Volatile.Read(ref state) != 0;

    public bool IsInterrupted => Volatile.Read(ref state) >= 2;

    public WorkerInterruptionReason InterruptionReason => Volatile.Read(ref state) switch
    {
        2 => WorkerInterruptionReason.OperatorStopNow,
        3 => WorkerInterruptionReason.HostShutdown,
        _ => WorkerInterruptionReason.None
    };

    public CancellationToken IntakeToken => intake.Token;

    public CancellationToken InterruptionToken => interruption.Token;

    internal event Action? StateChanged;

    public bool RequestDrain()
    {
        if (Interlocked.CompareExchange(ref state, 1, 0) != 0)
            return false;
        intake.Cancel();
        StateChanged?.Invoke();
        return true;
    }

    public bool RequestInterrupt(WorkerInterruptionReason reason)
    {
        if (reason is not (WorkerInterruptionReason.OperatorStopNow or
            WorkerInterruptionReason.HostShutdown))
        {
            throw new ArgumentOutOfRangeException(nameof(reason));
        }

        var requestedState = reason == WorkerInterruptionReason.OperatorStopNow ? 2 : 3;
        while (true)
        {
            var current = Volatile.Read(ref state);
            if (current >= 2)
                return false;
            if (Interlocked.CompareExchange(ref state, requestedState, current) == current)
                break;
        }
        lock (preparationGate)
        {
            try
            {
                interruptionPreparation?.Invoke(reason);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                // The durable item finalizer remains authoritative. A journal write must never
                // prevent the requested interruption itself.
            }
        }
        intake.Cancel();
        interruption.Cancel();
        StateChanged?.Invoke();
        return true;
    }

    public static WorkerInterruptionReason ReasonFor(CancellationToken cancellationToken) =>
        Controls.TryGetValue(cancellationToken, out var control)
            ? control.InterruptionReason
            : WorkerInterruptionReason.None;

    internal static WorkerRunControl? For(CancellationToken cancellationToken) =>
        Controls.TryGetValue(cancellationToken, out var control) ? control : null;

    internal IDisposable PrepareInterruption(Action<WorkerInterruptionReason> preparation)
    {
        lock (preparationGate)
            interruptionPreparation = preparation;
        if (IsInterrupted)
            preparation(InterruptionReason);
        return new PreparationScope(this, preparation);
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        Controls.TryRemove(interruption.Token, out _);
        registration.TrySetCanceled(intake.Token);
        intake.Dispose();
        interruption.Dispose();
    }

    private sealed class PreparationScope(
        WorkerRunControl owner,
        Action<WorkerInterruptionReason> preparation) : IDisposable
    {
        public void Dispose()
        {
            lock (owner.preparationGate)
            {
                if (ReferenceEquals(owner.interruptionPreparation, preparation))
                    owner.interruptionPreparation = null;
            }
        }
    }
}

/// <summary>
/// Shared non-interactive worker owner used by the CLI and the web supervisor. It owns registry
/// lifetime, cooperative external-control polling, and the current item/agent projection.
/// </summary>
public sealed class WorkerRunHost(
    WorkerService worker,
    IWorkerInstanceRegistry workerInstances)
{
    private static readonly TimeSpan ControlPollInterval = TimeSpan.FromMilliseconds(250);

    public async Task<WorkerRunSummary> RunAsync(
        TrackerConfig config,
        WorkerOptions options,
        WorkerRunIdentity identity,
        WorkerRunSelection selection,
        WorkerRunControl control,
        WorkerRunCallbacks callbacks,
        CancellationToken hostCancellationToken)
    {
        var registration = await RegisterAsync(
            identity.ConfigurationPath,
            identity.ConfigurationRevision,
            identity.InvocationSummary,
            identity.HostKind,
            callbacks.Warn,
            hostCancellationToken);
        await using var registrationScope = registration;
        control.RunId = registration.RunId;
        var warningState = new RegistryWarningState();
        void ControlStateChanged() => _ = ReflectControlStateAsync(
            registration,
            control,
            warningState,
            callbacks.Warn);
        control.StateChanged += ControlStateChanged;
        using var hostShutdown = hostCancellationToken.Register(
            () => control.RequestInterrupt(WorkerInterruptionReason.HostShutdown));
        using var pollingStop = new CancellationTokenSource();
        var polling = PollControlAsync(
            registration,
            control,
            warningState,
            callbacks.Warn,
            pollingStop.Token);
        try
        {
            Func<WorkerEvent, Task> projected = value => ProjectEventAsync(
                value,
                registration,
                control,
                warningState,
                callbacks.Emit,
                callbacks.Warn);
            return selection.ItemId is null
                ? await worker.RunAsync(
                    config,
                    options,
                    identity.RepositoryPath,
                    projected,
                    control)
                : await worker.RunItemAsync(
                    config,
                    options,
                    identity.RepositoryPath,
                    selection.ItemId.Value,
                    selection.Intent,
                    selection.ClaimToken,
                    projected,
                    control.InterruptionToken);
        }
        finally
        {
            if (control.IsInterrupted)
            {
                await TryUpdateAsync(
                    registration,
                    null,
                    null,
                    WorkerInstanceState.Finalizing,
                    warningState,
                    callbacks.Warn,
                    CancellationToken.None);
            }
            await pollingStop.CancelAsync();
            try
            {
                await polling;
            }
            catch (OperationCanceledException)
            {
                // Expected when the worker run ends before another control poll.
            }
            control.StateChanged -= ControlStateChanged;
        }
    }

    private async Task<IWorkerInstanceRegistration> RegisterAsync(
        string configurationPath,
        string configurationRevision,
        string invocationSummary,
        WorkerHostKind hostKind,
        Func<string, Task>? warn,
        CancellationToken cancellationToken)
    {
        try
        {
            return await workerInstances.RegisterAsync(
                configurationPath,
                configurationRevision,
                invocationSummary,
                new WorkerRegistrationMetadata(hostKind),
                cancellationToken);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            if (warn is not null)
                await warn($"Local worker status could not be registered: {exception.Message}");
            return await NoOpWorkerInstanceRegistry.Instance.RegisterAsync(
                configurationPath,
                configurationRevision,
                invocationSummary,
                cancellationToken);
        }
    }

    private static async Task PollControlAsync(
        IWorkerInstanceRegistration registration,
        WorkerRunControl control,
        RegistryWarningState warningState,
        Func<string, Task>? warn,
        CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(ControlPollInterval);
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            WorkerStopMode? request;
            try
            {
                request = await registration.ReadStopRequestAsync(cancellationToken);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                await WarnOnceAsync(warningState, warn,
                    $"Local worker control could not be read: {exception.Message}");
                continue;
            }

            if (request == WorkerStopMode.Interrupt)
            {
                control.RequestInterrupt(WorkerInterruptionReason.OperatorStopNow);
            }
            else if (request == WorkerStopMode.Drain && control.RequestDrain())
            {
                // StateChanged projects the control transition without clearing item/agent.
            }
        }
    }

    private static Task ReflectControlStateAsync(
        IWorkerInstanceRegistration registration,
        WorkerRunControl control,
        RegistryWarningState warningState,
        Func<string, Task>? warn) =>
        TryUpdateStateAsync(
            registration,
            control.IsInterrupted
                ? WorkerInstanceState.StoppingNow
                : WorkerInstanceState.Draining,
            warningState,
            warn,
            CancellationToken.None);

    private static async Task ProjectEventAsync(
        WorkerEvent value,
        IWorkerInstanceRegistration registration,
        WorkerRunControl control,
        RegistryWarningState warningState,
        Func<WorkerEvent, Task> emit,
        Func<string, Task>? warn)
    {
        var projected = WorkerInstanceEventProjection.Project(value, control);
        if (projected is not null)
        {
            await TryUpdateAsync(
                registration,
                projected.ItemId,
                projected.Agent,
                projected.State,
                warningState,
                warn,
                CancellationToken.None);
        }
        await emit(value);
    }

    private static async Task TryUpdateAsync(
        IWorkerInstanceRegistration registration,
        string? itemId,
        string? agent,
        WorkerInstanceState state,
        RegistryWarningState warningState,
        Func<string, Task>? warn,
        CancellationToken cancellationToken)
    {
        try
        {
            await registration.UpdateAsync(itemId, agent, state, cancellationToken);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            await WarnOnceAsync(warningState, warn,
                $"Local worker status could not be updated: {exception.Message}");
        }
    }

    private static async Task TryUpdateStateAsync(
        IWorkerInstanceRegistration registration,
        WorkerInstanceState state,
        RegistryWarningState warningState,
        Func<string, Task>? warn,
        CancellationToken cancellationToken)
    {
        try
        {
            await registration.UpdateStateAsync(state, cancellationToken);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            await WarnOnceAsync(warningState, warn,
                $"Local worker status could not be updated: {exception.Message}");
        }
    }

    private static async Task WarnOnceAsync(
        RegistryWarningState state,
        Func<string, Task>? warn,
        string message)
    {
        if (warn is null || state.Written)
            return;
        state.Written = true;
        await warn(message);
    }

    private sealed class RegistryWarningState
    {
        public bool Written { get; set; }
    }
}
