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
    private TaskCompletionSource startup = NewSignal();

    public Task Started { get { lock (sessionGate) return startup.Task; } }

    public Task<BusinessBlockResult> RunAsync(CancellationToken stopNotification)
    {
        VidMmRun run;
        lock (sessionGate)
        {
            if (activeRun is not null) throw new InvalidOperationException("VidMm already has an active run.");
            activeRun = run = new();
            startup = run.Started;
        }
        return RunSessionAsync(run, stopNotification);
    }

    private async Task<BusinessBlockResult> RunSessionAsync(VidMmRun run, CancellationToken stopNotification)
    {
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
                using var measurement = controller.MeasureSegment(BusinessBlockId);
                if (releaseBlocked || session is not null) throw new InvalidOperationException("The previous VidMm runtime was not released.");
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
        catch (Exception error)
        {
            if (error is KernelEtwSessionCreationCleanupException) releaseBlocked = true;
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
                using (controller.MeasureSegment(BusinessBlockId)) run.Session?.Dispose();
                released = true;
            }
            catch (Exception error)
            {
                controller.ReportFailure(BusinessBlockId, error, "session-stop");
                result = BusinessBlockResult.Failed(error, BusinessBlockFailureStage.Stop);
            }
            try
            {
                if (run.Processing is { } processing) await processing.ConfigureAwait(false);
            }
            catch (Exception error) { result = BusinessBlockResult.Failed(error); }
            run.ConsumerThread?.Join();
            lock (sessionGate)
            {
                using var measurement = controller.MeasureSegment(BusinessBlockId);
                Volatile.Write(ref retainedEvidence, result.Outcome == BusinessBlockOutcome.Failed
                    ? [] : evidenceByProcess.Values.ToArray());
                Interlocked.Exchange(ref evidenceByProcess, new());
                Volatile.Write(ref stoppedAtUtcTicks, DateTimeOffset.UtcNow.UtcTicks);
                if (released && ReferenceEquals(session, run.Session)) session = null;
                activeRun = null;
            }
        }
        return result;
    }

    private void SignalFailure(Exception error)
    {
        var run = Volatile.Read(ref activeRun);
        if (run is null) return;
        Interlocked.Exchange(ref run.Accepting, 0);
        if (run.Failure.TrySetResult(error))
        {
            Volatile.Write(ref retainedEvidence, []);
            controller.ReportFailure(BusinessBlockId, error, "vidmm-event");
        }
    }

    private sealed class VidMmRun
    {
        internal int Accepting = 1;
        internal TaskCompletionSource Started { get; } = NewSignal();
        internal TaskCompletionSource<Exception> Failure { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal IVidMmEtwSession? Session;
        internal Task? Processing;
        internal Thread? ConsumerThread;
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
