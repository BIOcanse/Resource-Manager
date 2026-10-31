using BlockUids = ResourceManager.App.StateItems.BlockUids;
using ResourceManager.App.Application.RuntimeSpecialization.BusinessBlocks;
using ResourceManager.ReadWrite.Windows.Processes.Etw;
using ResourceManager.ReadWrite.Windows.Frames;

namespace ResourceManager.App.Infrastructure.Telemetry.Etw;

public sealed partial class PresentFrameEtwSource
{
    private PresentRun? activeRun;
    private static Guid[] RequiredProviders => [DxgiProvider, Direct3D9Provider, GraphicsKernelProvider, KernelProcessProvider, Win32kProvider];

    public Task RunAsync(BusinessBlockRunContext context)
    {
        PresentRun run;
        lock (lifecycle)
        {
            if (activeRun is not null) throw new InvalidOperationException("Present already has an active run.");
            activeRun = run = new PresentRun(context.StopNotification);
            diagnostics.BeginRun(BlockUids.PresentFrameEtwSource);
        }
        return RunSessionAsync(run, context);
    }

    private async Task RunSessionAsync(PresentRun run, BusinessBlockRunContext context)
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
            lock (lifecycle)
            {
                using var measurement = diagnostics.MeasureSegment(BlockUids.PresentFrameEtwSource);
                ObjectDisposedException.ThrowIf(disposed, this);
                stopNotification.ThrowIfCancellationRequested();
                if (!HasActiveSubscriptions)
                {
                    context.ReportRunning();
                    run.Started.TrySetResult();
                    return;
                }
                else
                {
                    var name = ResourceManagerEtwSessionNames.Create(
                        ResourceManagerEtwSessionNames.PresentFramesPrefix, ResourceManagerEtwSessionNames.CurrentOwner);
                    var current = createSession(name);
                    run.Session = current;
                    lock (gate)
                    {
                        observedEventsLost = 0;
                    }
                    current.BindUsage(data =>
                    {
                        return Volatile.Read(ref run.Accepting) != 0
                            && ReferenceEquals(Volatile.Read(ref activeRun), run) && OnEvent(data);
                    });
                    Volatile.Write(ref frameLeases, frameSubscriptions.Count);
                    UpdateProviders(current, null);
                    run.Processing = DedicatedEtwConsumerThread.Start(BlockUids.PresentFrameEtwSource, name, () =>
                    {
                        try { current.Process(); }
                        catch (OperationCanceledException) when (stopNotification.IsCancellationRequested) { }
                    }, out run.ConsumerThread, consumerThreadObserver);
                    var flushInterval = Pipeline.FlushInterval;
                    run.FlushTimer = timeProvider.CreateTimer(_ => Flush(current), null, flushInterval, flushInterval);
                    context.ReportRunning();
                    run.Started.TrySetResult();
                    }
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
            lock (gate) captureFailure = GpuFailureReason(error);
            ObserveSessionFailure(error);
            run.Started.TrySetException(error);
            InvalidateCurrent();
            result = BusinessBlockResult.Failed(error, stage);
        }
        finally
        {
            Interlocked.Exchange(ref run.Accepting, 0);
            if (run.FlushTimer is { } timer)
            {
                try
                {
                    await timer.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception error)
                {
                    diagnostics.ReportFailure(BlockUids.PresentFrameEtwSource, error, "flush-timer-stop");
                    result = BusinessBlockResult.Failed(error, BusinessBlockFailureStage.Stop);
                }
            }
            try
            {
                using (diagnostics.MeasureSegment(BlockUids.PresentFrameEtwSource)) run.Session?.Dispose();
            }
            catch (Exception error)
            {
                availability.Report(BlockUids.PresentFrameEtwSource, NativeEtwSessionUnavailableCause.ReleaseUnconfirmed, error);
                ObserveSessionFailure(error);
                diagnostics.ReportFailure(BlockUids.PresentFrameEtwSource, error, "session-stop");
                result = BusinessBlockResult.Failed(error, BusinessBlockFailureStage.Stop);
            }
            try
            {
                if (run.Processing is { } processing) await processing.ConfigureAwait(false);
            }
            catch (Exception error) { ObserveSessionFailure(error); result = BusinessBlockResult.Failed(error); }
            // Awaiting Process completion alone does not prove that the original
            // dedicated thread has exited. This block owns native thread cleanup.
            run.ConsumerThread?.Join();
            run.Session = null;
            run.FlushTimer = null;
            lock (lifecycle)
            {
                using var measurement = diagnostics.MeasureSegment(BlockUids.PresentFrameEtwSource);
                lock (gate)
                {
                    if (result.Outcome == BusinessBlockOutcome.Failed)
                    {
                        hasFrameCurrent = false;
                        ledger = null;
                        foreach (var subscription in graphicsApiSubscriptions) subscription.Api = null;
                    }
                    else
                    {
                        if (hasFrameCurrent && frameStoppedAt == 0) frameStoppedAt = timeProvider.GetUtcNow().UtcTicks;
                        ledger?.ReleaseCorrelationState();
                    }
                    if (ledger is not null) ledger.GpuFrames = null;
                }
                activeRun = null;
                gpuTracker = null;
                gpuEventsEnabled = false;
                diagnostics.RecordResult(BlockUids.PresentFrameEtwSource, result);
                run.Completed.TrySetResult();
            }
        }
    }

    private void SignalFailure(Exception error)
    {
        var run = Volatile.Read(ref activeRun);
        if (run is null) return;
        Interlocked.Exchange(ref run.Accepting, 0);
        var firstFailure = run.Failure.TrySetResult(error);
        ObserveSessionFailure(error);
        if (firstFailure)
        {
            InvalidateCurrent();
            diagnostics.ReportFailure(BlockUids.PresentFrameEtwSource, error, "present-output");
        }
    }

    private void ObserveSessionFailure(Exception error)
        => availability.ReportFailure(BlockUids.PresentFrameEtwSource, error, RequiredProviders);

    private void InvalidateCurrent()
    {
        lock (gate)
        {
            hasFrameCurrent = false;
            foreach (var subscription in graphicsApiSubscriptions) subscription.Api = null;
        }
    }

    private void UpdateOutputProviders(int previousFrameLeases)
    {
        if (activeRun is not { } run || Volatile.Read(ref run.Accepting) == 0 || run.Session is null) return;
        try { UpdateProviders(run.Session, previousFrameLeases); }
        catch (Exception error) { SignalFailure(error); }
    }

    private void Release(FrameSubscription subscription)
    {
        lock (lifecycle)
        {
            var wasActive = HasActiveSubscriptions;
            lock (gate)
                if (!frameSubscriptions.Remove(subscription)) return;
            var previous = frameLeases;
            Volatile.Write(ref frameLeases, frameSubscriptions.Count);
            UpdateOutputProviders(previous);
            PublishSubscriptionActivity(wasActive);
        }
    }

    public void Dispose()
    {
        IDisposable[] subscriptions;
        lock (lifecycle)
        {
            if (disposed) return;
            disposed = true;
            lock (gate) subscriptions = frameSubscriptions.Cast<IDisposable>().Concat(graphicsApiSubscriptions).ToArray();
        }
        foreach (var subscription in subscriptions) subscription.Dispose();
    }

    private sealed class PresentRun(CancellationToken stopNotification)
    {
        internal CancellationToken StopNotification { get; } = stopNotification;
        internal TaskCompletionSource Completed { get; } = NewSignal();
        internal int Accepting = 1;
        internal TaskCompletionSource Started { get; } = NewSignal();
        internal TaskCompletionSource<Exception> Failure { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal IPresentFrameEtwSession? Session;
        internal ITimer? FlushTimer;
        internal Task? Processing;
        internal Thread? ConsumerThread;
        internal Dictionary<Guid, ProviderSelection> Providers { get; } = [];
    }

    private void Update(FrameSubscription subscription, FrameSubscriptionRequest request)
    {
        request = SnapshotRequest(request);
        lock (lifecycle)
        {
            lock (gate)
            {
                if (!frameSubscriptions.Contains(subscription)) return;
                subscription.Request = request;
            }
            UpdateOutputProviders(frameLeases);
            HoldersChanged?.Invoke();
        }
    }

    private sealed class FrameSubscription(PresentFrameEtwSource source, long id, FrameSubscriptionRequest request) : IFrameSubscriptionLease
    {
        internal string Id { get; } = $"present-frame:{id}";
        private PresentFrameEtwSource? owner = source;
        // Written under the source's gate; read under it by provider selection and Read.
        internal FrameSubscriptionRequest Request { get; set; } = request;
        public void Update(FrameSubscriptionRequest request)
        {
            ArgumentNullException.ThrowIfNull(request);
            Volatile.Read(ref owner)?.Update(this, request);
        }
        public void Dispose() => Interlocked.Exchange(ref owner, null)?.Release(this);
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
