using BlockUids = ResourceManager.App.StateItems.BlockUids;
using ResourceManager.App.Application.RuntimeSpecialization.BusinessBlocks;
using ResourceManager.ReadWrite.Windows.Processes.Etw;
using ResourceManager.ReadWrite.Windows.Frames;

namespace ResourceManager.App.Infrastructure.Telemetry.Etw;

public sealed partial class PresentFrameEtwSource
{
    private PresentRun? activeRun;
    internal static readonly string AvailabilityFact = "native-session-available:" + BlockUids.PresentFrameEtwSource.ToString(System.Globalization.CultureInfo.InvariantCulture);
    private bool nativeSessionAvailable = true;
    private TaskCompletionSource<PresentRun> nextRun = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task RunAsync(BusinessBlockRunContext context)
    {
        PresentRun run;
        lock (lifecycle)
        {
            if (activeRun is not null) throw new InvalidOperationException("Present already has an active run.");
            activeRun = run = new PresentRun(context.StopNotification);
            nextRun.TrySetResult(run);
            nextRun = new(TaskCreationOptions.RunContinuationsAsynchronously);
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
                Volatile.Write(ref frameLeases, frameSubscriptions.Count);
                UpdateProviders(current, null);
                run.Processing = DedicatedEtwConsumerThread.Start(BlockUids.PresentFrameEtwSource, name, () =>
                {
                    try { current.Process(); }
                    catch (OperationCanceledException) when (stopNotification.IsCancellationRequested) { }
                }, out run.ConsumerThread, consumerThreadObserver);
                flushTimer = timeProvider.CreateTimer(_ => Flush(current), null, FlushInterval, FlushInterval);
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
            if (error is KernelEtwSessionCreationCleanupException)
                SetAvailability(false, "session-start-unavailable");
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
                    SetAvailability(false, "flush-timer-release-unconfirmed");
                    diagnostics.ReportFailure(BlockUids.PresentFrameEtwSource, error, "flush-timer-stop");
                    result = BusinessBlockResult.Failed(error, BusinessBlockFailureStage.Stop);
                }
            }
            var released = false;
            try
            {
                using (diagnostics.MeasureSegment(BlockUids.PresentFrameEtwSource)) run.Session?.Dispose();
                released = true;
            }
            catch (Exception error)
            {
                SetAvailability(false, "session-release-unconfirmed");
                diagnostics.ReportFailure(BlockUids.PresentFrameEtwSource, error, "session-stop");
                result = BusinessBlockResult.Failed(error, BusinessBlockFailureStage.Stop);
            }
            try
            {
                if (run.Processing is { } processing) await processing.ConfigureAwait(false);
            }
            catch (Exception error) { ObservePermissionFailure(run, error); result = BusinessBlockResult.Failed(error); }
            // Awaiting Process completion alone does not prove that the original
            // dedicated thread has exited. This block owns native thread cleanup.
            run.ConsumerThread?.Join();
            lock (lifecycle)
            {
                using var measurement = diagnostics.MeasureSegment(BlockUids.PresentFrameEtwSource);
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
                diagnostics.RecordResult(BlockUids.PresentFrameEtwSource, result);
                run.Completed.TrySetResult();
            }
            if (released && run.Session is not null && flushTimer is null && !Volatile.Read(ref run.PermissionDenied))
                SetAvailability(true, "session-release-confirmed");
        }
    }

    internal Task PublishAvailabilityAsync()
        => controller.SetFactAsync(AvailabilityFact, Volatile.Read(ref nativeSessionAvailable),
            new(BusinessBlockRequestKind.Dependency, BlockUids.PresentFrameEtwSource.ToString(System.Globalization.CultureInfo.InvariantCulture), "native-session-availability", controller.CurrentPlanVersion));

    private void SetAvailability(bool available, string cause)
    {
        Volatile.Write(ref nativeSessionAvailable, available);
        _ = controller.SetFactAsync(AvailabilityFact, available,
            new(BusinessBlockRequestKind.Dependency, BlockUids.PresentFrameEtwSource.ToString(System.Globalization.CultureInfo.InvariantCulture), cause, controller.CurrentPlanVersion));
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
            InvalidateCurrent();
            diagnostics.ReportFailure(BlockUids.PresentFrameEtwSource, error, "present-output");
        }
    }

    private void ObservePermissionFailure(PresentRun run, Exception error)
    {
        if (error is not UnauthorizedAccessException) return;
        Volatile.Write(ref run.PermissionDenied, true);
        SetAvailability(false, "native-session-permission");
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
            Volatile.Write(ref frameLeases, frameSubscriptions.Count);
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

    private async Task AwaitReadyAsync(Task dispatch)
    {
        await dispatch.ConfigureAwait(false);
        Task<PresentRun> pending;
        lock (lifecycle) pending = activeRun is { StopNotification.IsCancellationRequested: false } current
            ? Task.FromResult(current) : nextRun.Task;
        var run = await pending.ConfigureAwait(false);
        await run.Started.Task.ConfigureAwait(false);
    }

    private async Task AwaitReleasedAsync(Task dispatch)
    {
        await dispatch.ConfigureAwait(false);
        Task drain;
        lock (lifecycle) drain = activeRun is { StopNotification.IsCancellationRequested: true } current
            ? current.Completed.Task : Task.CompletedTask;
        await drain.ConfigureAwait(false);
    }

    private sealed class PresentRun(CancellationToken stopNotification)
    {
        internal bool PermissionDenied;
        internal CancellationToken StopNotification { get; } = stopNotification;
        internal TaskCompletionSource Completed { get; } = NewSignal();
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
        private readonly PresentFrameEtwSource source;
        private Task? released;
        internal FrameSubscription(PresentFrameEtwSource owner, IBusinessBlockDependency dependency)
        {
            this.owner = owner;
            source = owner;
            this.dependency = dependency;
        }
        internal void ObserveRelease() => _ = dependency.Released.ContinueWith(_ => Dispose(), CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        internal IBusinessBlockDependency Dependency => dependency;
        public Task Ready => source.AwaitReadyAsync(dependency.Ready);
        public Task Released => released ??= source.AwaitReleasedAsync(dependency.Released);
        public void Dispose() => Interlocked.Exchange(ref owner, null)?.Release(this);
        public async ValueTask DisposeAsync() { Dispose(); await Released.ConfigureAwait(false); }
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
