using System.Collections.Concurrent;
using ResourceManager.App.Application.RuntimeSpecialization.BusinessBlocks;
using ResourceManager.ReadWrite.Windows.Processes.Etw;

namespace ResourceManager.App.Infrastructure.Telemetry.Etw;

public sealed partial class DxgkrnlVidMmEtwTelemetryZone
{
    private readonly object sessionGate = new();
    private VidMmRun? activeRun;
    private IVidMmEtwSession? session;
    private bool releaseBlocked;
    internal const string AvailabilityFact = "native-session-available:" + BusinessBlockId;
    private bool nativeSessionAvailable = true;
    private TaskCompletionSource<VidMmRun> nextRun = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task RunAsync(BusinessBlockRunContext context)
    {
        VidMmRun run;
        lock (sessionGate)
        {
            if (activeRun is not null) throw new InvalidOperationException("VidMm already has an active run.");
            activeRun = run = new(context.StopNotification);
            nextRun.TrySetResult(run);
            nextRun = new(TaskCreationOptions.RunContinuationsAsynchronously);
            diagnostics.BeginRun(BusinessBlockId);
        }
        return RunSessionAsync(run, context);
    }

    private async Task RunSessionAsync(VidMmRun run, BusinessBlockRunContext context)
    {
        await Task.Yield();
        var stopNotification = context.StopNotification;
        var stop = NewSignal();
        using var notification = stopNotification.Register(() =>
        {
            Interlocked.Exchange(ref run.Accepting, 0);
            stop.TrySetResult();
        });
        var result = BusinessBlockResult.Stopped;
        var stage = BusinessBlockFailureStage.Start;
        try
        {
            bool releaseUnconfirmed;
            lock (sessionGate) releaseUnconfirmed = releaseBlocked || session is not null;
            if (releaseUnconfirmed)
            {
                var error = new InvalidOperationException("The previous VidMm runtime was not released.");
                run.Started.TrySetException(error);
                diagnostics.ReportFailure(BusinessBlockId, error, "session-release-unconfirmed");
                throw error;
            }
            lock (sessionGate)
            {
                using var measurement = diagnostics.MeasureSegment(BusinessBlockId);
                stopNotification.ThrowIfCancellationRequested();
                var evidence = new ConcurrentDictionary<int, DxgkrnlVidMmEtwMemoryEvidence>();
                if (Volatile.Read(ref retainedEvidence) is { } previous)
                    foreach (var value in previous) evidence[value.ProcessId] = value;
                Volatile.Write(ref evidenceByProcess, evidence);
                Volatile.Write(ref retainedEvidence, null);
                Volatile.Write(ref stoppedAtUtcTicks, 0);
                Volatile.Write(ref nextPruneUtcTicks, 0);
                var current = createSession(SessionName);
                run.Session = session = current;
                current.Bind(data =>
                {
                    if (Volatile.Read(ref run.Accepting) != 0) OnDynamicEvent(data, evidence);
                });
                run.Processing = DedicatedEtwConsumerThread.Start("vidmm-etw", SessionName, () =>
                {
                    try { current.Process(); }
                    catch (OperationCanceledException) when (stopNotification.IsCancellationRequested) { }
                }, out run.ConsumerThread, consumerThreadObserver);
                context.ReportRunning();
                run.Started.TrySetResult();
            }
            stage = BusinessBlockFailureStage.Run;
            await Task.WhenAny(run.Processing!, run.Failure.Task, stop.Task).ConfigureAwait(false);
            if (run.Failure.Task.IsCompletedSuccessfully) result = BusinessBlockResult.Failed(run.Failure.Task.Result);
            else if (!stopNotification.IsCancellationRequested)
            {
                await run.Processing!.ConfigureAwait(false);
                result = BusinessBlockResult.Done;
            }
        }
        catch (OperationCanceledException) when (stopNotification.IsCancellationRequested)
        {
            run.Started.TrySetCanceled(stopNotification);
        }
        catch (Exception error)
        {
            ObservePermissionFailure(run, error);
            if (error is KernelEtwSessionCreationCleanupException) releaseBlocked = true;
            if (releaseBlocked) SetAvailability(false, "session-release-unconfirmed");
            run.Started.TrySetException(error);
            Volatile.Write(ref retainedEvidence, []);
            result = BusinessBlockResult.Failed(error, stage);
        }
        finally
        {
            Interlocked.Exchange(ref run.Accepting, 0);
            var released = false;
            try
            {
                using (diagnostics.MeasureSegment(BusinessBlockId)) run.Session?.Dispose();
                released = true;
            }
            catch (Exception error)
            {
                releaseBlocked = true;
                SetAvailability(false, "session-release-unconfirmed");
                diagnostics.ReportFailure(BusinessBlockId, error, "session-stop");
                result = BusinessBlockResult.Failed(error, BusinessBlockFailureStage.Stop);
            }
            try
            {
                if (run.Processing is { } processing) await processing.ConfigureAwait(false);
            }
            catch (Exception error) { ObservePermissionFailure(run, error); result = BusinessBlockResult.Failed(error); }
            run.ConsumerThread?.Join();
            lock (sessionGate)
            {
                using var measurement = diagnostics.MeasureSegment(BusinessBlockId);
                Volatile.Write(ref retainedEvidence, result.Outcome == BusinessBlockOutcome.Failed
                    ? [] : evidenceByProcess.Values.ToArray());
                Interlocked.Exchange(ref evidenceByProcess, new());
                Volatile.Write(ref stoppedAtUtcTicks, DateTimeOffset.UtcNow.UtcTicks);
                if (released && ReferenceEquals(session, run.Session)) session = null;
                activeRun = null;
                diagnostics.RecordResult(BusinessBlockId, result);
                run.Completed.TrySetResult();
            }
            if (released && run.Session is not null)
            {
                releaseBlocked = false;
                if (!Volatile.Read(ref run.PermissionDenied)) SetAvailability(true, "session-release-confirmed");
            }
        }
    }

    internal Task PublishAvailabilityAsync()
        => controller.SetFactAsync(AvailabilityFact, Volatile.Read(ref nativeSessionAvailable),
            new(BusinessBlockRequestKind.Dependency, BusinessBlockId, "native-session-availability", controller.CurrentPlanVersion));

    private void SetAvailability(bool available, string cause)
    {
        Volatile.Write(ref nativeSessionAvailable, available);
        // Publication commits synchronously. Awaiting the reader's decision here
        // could wait for this same run to finish its cleanup.
        _ = controller.SetFactAsync(AvailabilityFact, available,
            new(BusinessBlockRequestKind.Dependency, BusinessBlockId, cause, controller.CurrentPlanVersion));
    }

    private void SignalFailure(Exception error)
    {
        var run = Volatile.Read(ref activeRun);
        if (run is null) return;
        if (error is UnauthorizedAccessException) Volatile.Write(ref run.PermissionDenied, true);
        Interlocked.Exchange(ref run.Accepting, 0);
        var firstFailure = run.Failure.TrySetResult(error);
        ObservePermissionFailure(run, error);
        if (firstFailure)
        {
            Volatile.Write(ref retainedEvidence, []);
            diagnostics.ReportFailure(BusinessBlockId, error, "vidmm-event");
        }
    }

    private void ObservePermissionFailure(VidMmRun run, Exception error)
    {
        if (error is not UnauthorizedAccessException) return;
        Volatile.Write(ref run.PermissionDenied, true);
        SetAvailability(false, "native-session-permission");
    }

    private async Task AwaitReadyAsync(Task dispatch)
    {
        await dispatch.ConfigureAwait(false);
        Task<VidMmRun> pending;
        lock (sessionGate) pending = activeRun is { StopNotification.IsCancellationRequested: false } current
            ? Task.FromResult(current) : nextRun.Task;
        var run = await pending.ConfigureAwait(false);
        await run.Started.Task.ConfigureAwait(false);
    }

    private async Task AwaitReleasedAsync(Task dispatch)
    {
        await dispatch.ConfigureAwait(false);
        Task drain;
        lock (sessionGate) drain = activeRun is { StopNotification.IsCancellationRequested: true } current
            ? current.Completed.Task : Task.CompletedTask;
        await drain.ConfigureAwait(false);
    }

    private sealed class Subscription(DxgkrnlVidMmEtwTelemetryZone owner, IBusinessBlockDependency dependency) : IBusinessBlockDependency
    {
        private Task? released;
        public Task Ready => owner.AwaitReadyAsync(dependency.Ready);
        public Task Released => released ??= owner.AwaitReleasedAsync(dependency.Released);
        public void Dispose() => dependency.Dispose();
        public async ValueTask DisposeAsync() { Dispose(); await Released.ConfigureAwait(false); }
    }

    private sealed class VidMmRun(CancellationToken stopNotification)
    {
        internal bool PermissionDenied;
        internal CancellationToken StopNotification { get; } = stopNotification;
        internal TaskCompletionSource Completed { get; } = NewSignal();
        internal int Accepting = 1;
        internal TaskCompletionSource Started { get; } = NewSignal();
        internal TaskCompletionSource<Exception> Failure { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal IVidMmEtwSession? Session;
        internal Task? Processing;
        internal Thread? ConsumerThread;
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
