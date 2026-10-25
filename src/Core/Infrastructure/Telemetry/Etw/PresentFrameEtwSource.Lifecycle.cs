using ResourceManager.App.Application.RuntimeSpecialization.BusinessBlocks;
using ResourceManager.ReadWrite.Windows.Processes.Etw;
using ResourceManager.ReadWrite.Windows.Frames;

namespace ResourceManager.App.Infrastructure.Telemetry.Etw;

public sealed partial class PresentFrameEtwSource
{
    private PresentRun? activeRun;
    private TaskCompletionSource startup = NewSignal();

    public Task Started { get { lock (lifecycle) return startup.Task; } }

    public Task<BusinessBlockResult> RunAsync(CancellationToken stopNotification)
    {
        PresentRun run;
        lock (lifecycle)
        {
            if (activeRun is not null) throw new InvalidOperationException("Present already has an active run.");
            activeRun = run = new PresentRun();
            startup = run.Started;
        }
        return RunSessionAsync(run, stopNotification);
    }

    private async Task<BusinessBlockResult> RunSessionAsync(PresentRun run, CancellationToken stopNotification)
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
            lock (lifecycle)
            {
                using var measurement = controller.MeasureSegment("present-etw");
                ObjectDisposedException.ThrowIf(disposed, this);
                if (session is not null || flushTimer is not null)
                    throw new InvalidOperationException("The previous Present runtime has not been released.");
                var name = ResourceManagerEtwSessionNames.Create(
                    ResourceManagerEtwSessionNames.PresentFramesPrefix, ResourceManagerEtwSessionNames.CurrentOwner);
                var current = createSession(name);
                run.Session = session = current;
                lock (gate)
                {
                    observedEventsLost = 0;
                    foreach (var subscription in graphicsApiSubscriptions) subscription.Api = null;
                }
                current.Bind(data =>
                {
                    if (Volatile.Read(ref run.Accepting) != 0 && ReferenceEquals(Volatile.Read(ref session), current)) OnEvent(data);
                });
                Volatile.Write(ref frameLeases, frameSubscriptions.Count(subscription => !subscription.Released.IsCompleted));
                UpdateProviders(current, null);
                run.Processing = DedicatedEtwConsumerThread.Start("present-etw", name, () =>
                {
                    try { current.Process(); }
                    catch (OperationCanceledException) when (stopNotification.IsCancellationRequested) { }
                }, out run.ConsumerThread, consumerThreadObserver);
                flushTimer = timeProvider.CreateTimer(_ => Flush(current), null, FlushInterval, FlushInterval);
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
            run.Started.TrySetException(error);
            InvalidateCurrent();
            result = BusinessBlockResult.Failed(error, stage);
        }
        finally
        {
            Interlocked.Exchange(ref run.Accepting, 0);
            if (flushTimer is { } timer)
            {
                try
                {
                    await timer.DisposeAsync().ConfigureAwait(false);
                    flushTimer = null;
                }
                catch (Exception error)
                {
                    controller.ReportFailure("present-etw", error, "flush-timer-stop");
                    result = BusinessBlockResult.Failed(error, BusinessBlockFailureStage.Stop);
                }
            }
            var released = false;
            try
            {
                using (controller.MeasureSegment("present-etw")) run.Session?.Dispose();
                released = true;
            }
            catch (Exception error)
            {
                controller.ReportFailure("present-etw", error, "session-stop");
                result = BusinessBlockResult.Failed(error, BusinessBlockFailureStage.Stop);
            }
            try
            {
                if (run.Processing is { } processing) await processing.ConfigureAwait(false);
            }
            catch (Exception error) { result = BusinessBlockResult.Failed(error); }
            // Awaiting Process completion alone does not prove that the original
            // dedicated thread has exited. The controller owns any stop deadline.
            run.ConsumerThread?.Join();
            lock (lifecycle)
            {
                using var measurement = controller.MeasureSegment("present-etw");
                if (released && ReferenceEquals(session, run.Session)) session = null;
                lock (gate)
                {
                    if (result.Outcome == BusinessBlockOutcome.Failed)
                    {
                        hasFrameCurrent = false;
                        ledger = new PresentFrameLedger(ReadProcessStartKey);
                        foreach (var subscription in graphicsApiSubscriptions) subscription.Api = null;
                    }
                    else
                    {
                        if (hasFrameCurrent && frameStoppedAt == 0) frameStoppedAt = timeProvider.GetUtcNow().UtcTicks;
                        ledger.ReleaseCorrelationState();
                    }
                }
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
            InvalidateCurrent();
            controller.ReportFailure("present-etw", error, "present-output");
        }
    }

    private void InvalidateCurrent()
    {
        lock (gate)
        {
            hasFrameCurrent = false;
            foreach (var subscription in graphicsApiSubscriptions) subscription.Api = null;
        }
    }

    private void UpdateOutputDemand(int previousFrameLeases)
    {
        if (activeRun is not { } run || Volatile.Read(ref run.Accepting) == 0 || session is null) return;
        try { UpdateProviders(session, previousFrameLeases); }
        catch (Exception error) { SignalFailure(error); }
    }

    private void Release(FrameSubscription subscription)
    {
        lock (lifecycle)
        {
            if (!frameSubscriptions.Remove(subscription)) return;
            var previous = frameLeases;
            Volatile.Write(ref frameLeases, frameSubscriptions.Count(value => !value.Released.IsCompleted));
            UpdateOutputDemand(previous);
        }
        subscription.Dependency.Dispose();
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

    private sealed class PresentRun
    {
        internal int Accepting = 1;
        internal TaskCompletionSource Started { get; } = NewSignal();
        internal TaskCompletionSource<Exception> Failure { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal IPresentFrameEtwSession? Session;
        internal Task? Processing;
        internal Thread? ConsumerThread;
    }

    private sealed class FrameSubscription : IBusinessBlockDependency
    {
        private PresentFrameEtwSource? owner;
        private readonly IBusinessBlockDependency dependency;
        internal FrameSubscription(PresentFrameEtwSource owner, IBusinessBlockDependency dependency)
        {
            this.owner = owner;
            this.dependency = dependency;
        }
        internal void ObserveRelease() => _ = dependency.Released.ContinueWith(_ => Dispose(), CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        internal IBusinessBlockDependency Dependency => dependency;
        public Task Ready => dependency.Ready;
        public Task Released => dependency.Released;
        public void Dispose() => Interlocked.Exchange(ref owner, null)?.Release(this);
        public async ValueTask DisposeAsync() { Dispose(); await Released.ConfigureAwait(false); }
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
