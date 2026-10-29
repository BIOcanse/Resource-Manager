using BlockUids = ResourceManager.App.StateItems.BlockUids;
using Microsoft.Extensions.Configuration;
using ResourceManager.App.Application.RuntimeSpecialization.BusinessBlocks;
using ResourceManager.App.Infrastructure.RuntimeSpecialization.BusinessBlocks;
using ResourceManager.ReadWrite.Windows.Processes.Etw;

namespace ResourceManager.App.Infrastructure.Telemetry.Etw;

public sealed partial class GpuAllocationCollector
{
    private static Guid[] RequiredProviders => [Provider, BoundaryProvider];

    private readonly ILogger<GpuAllocationCollector> logger;
    private readonly BusinessBlockDiagnosticsRegistry diagnostics;
    private readonly object lifecycle = new();
    private readonly SemaphoreSlim captureGate = new(1, 1);
    private readonly IGpuAllocationSessionFactory sessionFactory;
    private readonly TimeProvider timeProvider;
    private readonly TimeSpan deliveryBudget;
    private IGpuAllocationSession? session;
    private string? sessionName;
    private Run? activeRun;
    private readonly NativeEtwSessionAvailability availability;

    public GpuAllocationCollector(ILogger<GpuAllocationCollector> logger, IConfiguration configuration,
        BusinessBlockDiagnosticsRegistry diagnostics, NativeEtwSessionAvailability availability, IEtwConsumerThreadObserver? consumerThreadObserver = null)
    {
        this.logger = logger;
        this.diagnostics = diagnostics;
        this.availability = availability;
        plan = GpuAllocationCapturePlan.Compile(configuration);
        sessionFactory = new TraceEventGpuAllocationSessionFactory(logger, consumerThreadObserver);
        timeProvider = TimeProvider.System;
        deliveryBudget = TimeSpan.FromSeconds(5);
    }

    internal GpuAllocationCollector(ILogger<GpuAllocationCollector> logger, IConfiguration configuration,
        BusinessBlockDiagnosticsRegistry diagnostics, NativeEtwSessionAvailability availability, IGpuAllocationSessionFactory sessionFactory,
        TimeProvider timeProvider, TimeSpan? deliveryBudget = null,
        IEtwConsumerThreadObserver? consumerThreadObserver = null)
        : this(logger, configuration, diagnostics, availability, consumerThreadObserver)
    {
        this.sessionFactory = sessionFactory;
        this.timeProvider = timeProvider;
        this.deliveryBudget = deliveryBudget ?? TimeSpan.FromSeconds(5);
    }

    public Task RunAsync(BusinessBlockRunContext context)
    {
        Run run;
        lock (lifecycle)
        {
            if (activeRun is not null)
                throw new InvalidOperationException("The GPU allocation block already has an active run.");
            run = new Run(context.StopNotification);
            activeRun = run;
            diagnostics.BeginRun(BlockUids.GpuAllocationCollector);
        }
        return RunSessionAsync(run, context);
    }

    private async Task RunSessionAsync(Run run, BusinessBlockRunContext context)
    {
        await Task.Yield();
        var stopNotification = context.StopNotification;
        var result = BusinessBlockResult.Stopped;
        Exception? cleanupFailure = null;
        var started = false;
        var stopSignal = NewSignal();
        using var stopRegistration = stopNotification.Register(static state => ((TaskCompletionSource)state!).TrySetResult(), stopSignal);
        try
        {
            stopNotification.ThrowIfCancellationRequested();
            started = true;
            context.ReportRunning();
            run.Started.TrySetResult();

            await Task.WhenAny(run.ProcessingCompletion.Task, stopSignal.Task).ConfigureAwait(false);
            if (run.ProcessingCompletion.Task.IsCompletedSuccessfully && run.ProcessingCompletion.Task.Result is { } failure)
            {
                result = BusinessBlockResult.Failed(failure, run.FailureStage);
            }
            else if (stopNotification.IsCancellationRequested)
            {
                result = BusinessBlockResult.Stopped;
            }
            else
            {
                var error = await run.ProcessingCompletion.Task.ConfigureAwait(false);
                result = error is null ? BusinessBlockResult.Done : BusinessBlockResult.Failed(error);
            }
        }
        catch (OperationCanceledException) when (stopNotification.IsCancellationRequested)
        {
            run.Started.TrySetCanceled(stopNotification);
        }
        catch (Exception error)
        {
            ObserveSessionFailure(run, error);
            run.Started.TrySetException(error);
            result = BusinessBlockResult.Failed(error, started ? BusinessBlockFailureStage.Run : BusinessBlockFailureStage.Start);
        }
        finally
        {
            run.CaptureAdmission.Cancel();
            await captureGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                Task captureRequests;
                lock (lifecycle) captureRequests = run.CaptureRequestsDrained;
                await captureRequests.ConfigureAwait(false);
                using (diagnostics.MeasureSegment(BlockUids.GpuAllocationCollector)) cleanupFailure = StopSession(run);
            }
            finally
            {
                captureGate.Release();
                lock (lifecycle)
                {
                    // A failed release remains owned; do not lose its only reference.
                    if (ReferenceEquals(activeRun, run)) activeRun = null;
                }
                run.CaptureAdmission.Dispose();
            }
        }

        var finalResult = cleanupFailure is null
            ? result
            : BusinessBlockResult.Failed(cleanupFailure, BusinessBlockFailureStage.Stop);
        diagnostics.RecordResult(BlockUids.GpuAllocationCollector, finalResult);
        run.Completed.TrySetResult();
    }

    // The original source planner calls this on membership/cadence changes,
    // including plans with no due capture. It never opens a session.
    internal void SynchronizeCaptureInterval(TimeSpan? refreshInterval, CancellationToken cancellationToken = default)
    {
        if (refreshInterval is { } interval)
        {
            if (interval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(refreshInterval));
            if (plan.UseLive(interval)) return;
        }
        captureGate.Wait(cancellationToken);
        try
        {
            Run? run;
            lock (lifecycle) run = activeRun;
            if (run is null || Volatile.Read(ref run.Failed)) return;
            StopSession(run);
        }
        finally { captureGate.Release(); }
    }

    private void ObserveSessionFailure(Run run, Exception? error)
    {
        if (error is null) return;
        Volatile.Write(ref run.Failed, true);
        availability.ReportFailure(BlockUids.GpuAllocationCollector, error, RequiredProviders);
    }

    private void ReportSessionFailure(Run run, Exception error)
    {
        Volatile.Write(ref run.Failed, true);
        // Failure is ready before availability can make the parent send stop.
        run.ProcessingCompletion.TrySetResult(error);
        ObserveSessionFailure(run, error);
    }

    private void StartSession(Run run)
    {
        lock (lifecycle)
            if (session is not null)
                throw new InvalidOperationException("The prior GPU allocation session has not been released.");
        var name = ResourceManagerEtwSessionNames.Create(ResourceManagerEtwSessionNames.GpuAllocationsPrefix,
            ResourceManagerEtwSessionNames.CurrentOwner);
        var current = sessionFactory.Create(name);

        lock (lifecycle)
        {
            session = current;
            sessionName = name;
            run.Session = current;
        }

        current.Bind((provider, id, payload, timestamp) =>
        {
            var accepted = false;
            try
            {
            lock (gate)
            {
                if (!ReferenceEquals(Volatile.Read(ref session), current)) return;
                try
                {
                    if (provider == BoundaryProvider)
                    {
                        // Only BeginScan (2) and EndScan (3) carry boundary fields. The EventSource also writes
                        // its self-describing manifest (event 65534) each time a capture enables the provider.
                        if (id is not (2 or 3)) return;
                        if (scan is not { } pending || pending.Completion.Task.IsCompleted
                            || payload.Get(GpuAllocationField.session)?.ToString() != name
                            || Convert.ToInt64(payload.Get(GpuAllocationField.scan)) != pending.Id) return;
                        if (id == 2)
                        {
                            pending.Candidate = new();
                            pending.EventsLost = Convert.ToInt64(payload.Get(GpuAllocationField.eventsLost));
                            accepted = true;
                        }
                        else if (id == 3 && pending.Candidate is { } candidate)
                        {
                            if (SessionControl(run, () => current.EventsLost) != pending.EventsLost)
                                throw new InvalidDataException("GPU allocation events were lost between rundown boundaries.");
                            if (pending.KeepLive) { liveLedger = candidate; liveEventsLost = pending.EventsLost; }
                            pending.Candidate = null;
                            pending.Completion.TrySetResult(candidate);
                            accepted = true;
                        }
                        return;
                    }
                    if (provider != Provider || !EventIds.Contains(id)) return;
                    var target = scan?.Candidate ?? (liveFailure is null ? liveLedger : null);
                    if (target is not null)
                    {
                        GpuAllocationEventDecoder.Apply(target, id, payload, timestamp);
                        accepted = true;
                    }
                }
                catch (Exception error)
                {
                    if (scan is { } pending && !pending.Completion.Task.IsCompleted)
                    {
                        pending.Candidate = null;
                        pending.Completion.TrySetException(error);
                    }
                    else if (liveLedger is not null) liveFailure = error;
                }
            }
            }
            finally
            {
                // The native dispatcher already counted this delivery. Attribute
                // rejection to that exact provider/ID/opcode on the same session.
                if (!accepted) payload.DropCounter?.Drop();
                GpuAllocationObservation.RecordDelivery(name, provider, id, timestamp, accepted);
            }
        });

        // Creation occurs only for an admitted capture. This provider starts
        // the native session and is consumed by that capture's rundown.
        current.EnableProvider(BoundaryProvider);
        current.StartProcessing(error => OnSessionCompleted(run, current, error));
        if (!current.IsProcessing && !run.ProcessingCompletion.Task.IsCompleted)
            run.ProcessingCompletion.TrySetResult(null);
    }

    private void OnSessionCompleted(Run run, IGpuAllocationSession current, Exception? error)
    {
        if (!ReferenceEquals(Volatile.Read(ref session), current)) return;
        if (error is null && ReferenceEquals(Volatile.Read(ref run.ClosingSession), current)) return;
        if (run.StopNotification.IsCancellationRequested)
        {
            if (error is null) run.ProcessingCompletion.TrySetResult(null);
            else ReportSessionFailure(run, error);
            return;
        }

        lock (gate)
        {
            if (!ReferenceEquals(Volatile.Read(ref session), current)) return;
            if (error is null)
                scan?.Completion.TrySetException(new IOException("GPU allocation event processing completed during a due rundown."));
            else
                scan?.Completion.TrySetException(error);
        }
        if (error is null) run.ProcessingCompletion.TrySetResult(null);
        else ReportSessionFailure(run, error);
        if (error is null)
            logger.LogInformation("GPU allocation event processing completed; releasing the owned session.");
        else
            logger.LogWarning(error, "GPU allocation event processing failed; releasing the owned session.");
    }

    // captureGate owns native lifetime. Neither Stop nor Join runs under a
    // callback/publication lock, and an intentional finite close is not a run exit.
    private Exception? StopSession(Run run)
    {
        if (run.CleanupFailure is { } previousFailure) return previousFailure;
        lock (gate)
        {
            scan?.Completion.TrySetException(new OperationCanceledException("GPU allocation session is closing."));
            scan = null;
            liveLedger = null;
            liveFailure = null;
        }

        IGpuAllocationSession? current;
        lock (lifecycle)
        {
            current = run.Session;
            if (current is null) return null;
            run.ClosingSession = current;
        }

        Exception? failure = null;
        try
        {
            var released = current.Stop(Timeout.InfiniteTimeSpan);
            if (!released.IsReleased)
                failure = new IOException("GPU allocation session release was not confirmed: " + string.Join(" | ", released.Failures));
        }
        catch (Exception error) { failure = error; }
        // Publish failed release before joining, so unavailable state cannot
        // be mistaken for an available session while its consumer is draining.
        if (failure is not null) ReportReleaseFailure(run, failure);
        try { current.JoinProcessing(); }
        catch (Exception error)
        {
            if (failure is null) { failure = error; ReportReleaseFailure(run, failure); }
        }
        // Retain this exact failed owner; outer cleanup reports the same
        // obligation without a second native stop or a replacement session.
        if (failure is not null) return failure;

        lock (lifecycle)
        {
            if (ReferenceEquals(session, current)) { session = null; sessionName = null; }
            if (ReferenceEquals(run.Session, current)) run.Session = null;
            run.ClosingSession = null;
        }
        return null;
    }

    private void ReportReleaseFailure(Run run, Exception failure)
    {
        run.CleanupFailure = failure;
        Volatile.Write(ref run.Failed, true);
        run.ProcessingCompletion.TrySetResult(failure);
        availability.Report(BlockUids.GpuAllocationCollector, NativeEtwSessionUnavailableCause.ReleaseUnconfirmed, failure);
        ObserveSessionFailure(run, failure);
        logger.LogError(failure, "GPU allocation session release failed.");
    }

    private sealed class Run(CancellationToken stopNotification)
    {
        internal bool Failed;
        internal BusinessBlockFailureStage FailureStage = BusinessBlockFailureStage.Run;
        internal TaskCompletionSource Completed { get; } = NewSignal();
        internal CancellationToken StopNotification { get; } = stopNotification;
        internal CancellationTokenSource CaptureAdmission { get; } = new();
        internal TaskCompletionSource Started { get; } = NewSignal();
        internal TaskCompletionSource<Exception?> ProcessingCompletion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal IGpuAllocationSession? Session;
        internal IGpuAllocationSession? ClosingSession;
        internal Exception? CleanupFailure;
        private int captureRequests;
        private TaskCompletionSource? captureRequestsDrained;

        internal Task CaptureRequestsDrained => captureRequests == 0
            ? Task.CompletedTask
            : captureRequestsDrained!.Task;

        internal void RegisterCapture()
        {
            if (captureRequests++ == 0) captureRequestsDrained = NewSignal();
        }

        internal void ReleaseCapture()
        {
            if (--captureRequests == 0) captureRequestsDrained!.TrySetResult();
        }
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
