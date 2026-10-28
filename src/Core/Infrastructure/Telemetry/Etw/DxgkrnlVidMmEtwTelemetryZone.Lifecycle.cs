using BlockUids = ResourceManager.App.StateItems.BlockUids;
using System.Collections.Concurrent;
using ResourceManager.App.Application.RuntimeSpecialization.BusinessBlocks;
using ResourceManager.ReadWrite.Windows.Processes.Etw;

namespace ResourceManager.App.Infrastructure.Telemetry.Etw;

public sealed partial class DxgkrnlVidMmEtwTelemetryZone
{
    private readonly object sessionGate = new();
    private VidMmRun? activeRun;
    private static Guid[] RequiredProviders => [PresentFrameEtwSource.GraphicsKernelProvider];

    public Task RunAsync(BusinessBlockRunContext context)
    {
        VidMmRun run;
        lock (sessionGate)
        {
            if (activeRun is not null) throw new InvalidOperationException("VidMm already has an active run.");
            activeRun = run = new(context.StopNotification);
            diagnostics.BeginRun(BlockUids.DxgkrnlVidMmEtwTelemetryZone);
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
            lock (sessionGate)
            {
                using var measurement = diagnostics.MeasureSegment(BlockUids.DxgkrnlVidMmEtwTelemetryZone);
                stopNotification.ThrowIfCancellationRequested();
                var evidence = new ConcurrentDictionary<int, DxgkrnlVidMmEtwMemoryEvidence>();
                if (Volatile.Read(ref retainedEvidence) is { } previous)
                    foreach (var value in previous) evidence[value.ProcessId] = value;
                Volatile.Write(ref evidenceByProcess, evidence);
                Volatile.Write(ref retainedEvidence, null);
                Volatile.Write(ref stoppedAtUtcTicks, 0);
                Volatile.Write(ref nextPruneUtcTicks, 0);
                var current = createSession(SessionName);
                run.Session = current;
                current.BindUsage(data =>
                {
                    return Volatile.Read(ref run.Accepting) != 0 && OnDynamicEvent(data, evidence);
                });
                run.Processing = DedicatedEtwConsumerThread.Start(BlockUids.DxgkrnlVidMmEtwTelemetryZone, SessionName, () =>
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
            ObserveSessionFailure(error);
            run.Started.TrySetException(error);
            Volatile.Write(ref retainedEvidence, []);
            result = BusinessBlockResult.Failed(error, stage);
        }
        finally
        {
            Interlocked.Exchange(ref run.Accepting, 0);
            try
            {
                using (diagnostics.MeasureSegment(BlockUids.DxgkrnlVidMmEtwTelemetryZone)) run.Session?.Dispose();
            }
            catch (Exception error)
            {
                availability.Report(BlockUids.DxgkrnlVidMmEtwTelemetryZone, NativeEtwSessionUnavailableCause.ReleaseUnconfirmed, error);
                ObserveSessionFailure(error);
                diagnostics.ReportFailure(BlockUids.DxgkrnlVidMmEtwTelemetryZone, error, "session-stop");
                result = BusinessBlockResult.Failed(error, BusinessBlockFailureStage.Stop);
            }
            try
            {
                if (run.Processing is { } processing) await processing.ConfigureAwait(false);
            }
            catch (Exception error) { ObserveSessionFailure(error); result = BusinessBlockResult.Failed(error); }
            run.ConsumerThread?.Join();
            run.Session = null;
            lock (sessionGate)
            {
                using var measurement = diagnostics.MeasureSegment(BlockUids.DxgkrnlVidMmEtwTelemetryZone);
                Volatile.Write(ref retainedEvidence, result.Outcome == BusinessBlockOutcome.Failed
                    ? [] : evidenceByProcess.Values.ToArray());
                Interlocked.Exchange(ref evidenceByProcess, new());
                Volatile.Write(ref stoppedAtUtcTicks, DateTimeOffset.UtcNow.UtcTicks);
                activeRun = null;
                diagnostics.RecordResult(BlockUids.DxgkrnlVidMmEtwTelemetryZone, result);
                run.Completed.TrySetResult();
            }
        }
    }

    private void SignalFailure(Exception error)
    {
        // A callback's temporary failure report must precede the run's final result.
        lock (sessionGate)
        {
            var run = activeRun;
            if (run is null) return;
            Interlocked.Exchange(ref run.Accepting, 0);
            var firstFailure = run.Failure.TrySetResult(error);
            ObserveSessionFailure(error);
            if (firstFailure)
            {
                Volatile.Write(ref retainedEvidence, []);
                diagnostics.ReportFailure(BlockUids.DxgkrnlVidMmEtwTelemetryZone, error, "vidmm-event");
            }
        }
    }

    private void ObserveSessionFailure(Exception error)
        => availability.ReportFailure(BlockUids.DxgkrnlVidMmEtwTelemetryZone, error, RequiredProviders);

    private async Task AwaitReleasedAsync(Task dispatch)
    {
        await dispatch.ConfigureAwait(false);
        Task drain;
        lock (sessionGate) drain = activeRun is { StopNotification.IsCancellationRequested: true } current
            ? current.Completed.Task : Task.CompletedTask;
        await drain.ConfigureAwait(false);
    }

    private sealed class VidMmRun(CancellationToken stopNotification)
    {
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
