using Microsoft.Extensions.Configuration;
using ResourceManager.App.Application.RuntimeSpecialization.BusinessBlocks;
using ResourceManager.App.Infrastructure.RuntimeSpecialization.BusinessBlocks;
using ResourceManager.ReadWrite.Windows.Processes.Etw;

namespace ResourceManager.App.Infrastructure.Telemetry.Etw;

public sealed partial class GpuAllocationCollector
{
    private const string BusinessBlockId = "gpu-allocation-etw";
    private const string SubscriptionCause = "gpu-allocation-subscription";
    private static readonly string[] Outputs = ["gpu-allocation"];

    private readonly ILogger<GpuAllocationCollector> logger;
    private readonly BusinessBlockController controller;
    private readonly object lifecycle = new();
    private readonly SemaphoreSlim captureGate = new(1, 1);
    private readonly IGpuAllocationSessionFactory sessionFactory;
    private readonly TimeProvider timeProvider;
    private readonly TimeSpan deliveryBudget;
    private readonly HashSet<Lease> leases = [];
    private IGpuAllocationSession? session;
    private string? sessionName;
    private Run? activeRun;
    private TaskCompletionSource startup = NewSignal();
    private bool runtimeReleaseBlocked;
    private BusinessBlockOutcome? lastRunOutcome;

    public GpuAllocationCollector(ILogger<GpuAllocationCollector> logger, IConfiguration configuration,
        BusinessBlockController controller, IEtwConsumerThreadObserver? consumerThreadObserver = null)
    {
        this.logger = logger;
        this.controller = controller;
        plan = GpuAllocationCapturePlan.Compile(configuration);
        sessionFactory = new TraceEventGpuAllocationSessionFactory(logger, consumerThreadObserver);
        timeProvider = TimeProvider.System;
        deliveryBudget = TimeSpan.FromSeconds(5);
    }

    internal GpuAllocationCollector(ILogger<GpuAllocationCollector> logger, IConfiguration configuration,
        BusinessBlockController controller, IGpuAllocationSessionFactory sessionFactory,
        TimeProvider timeProvider, TimeSpan? deliveryBudget = null,
        IEtwConsumerThreadObserver? consumerThreadObserver = null)
        : this(logger, configuration, controller, consumerThreadObserver)
    {
        this.sessionFactory = sessionFactory;
        this.timeProvider = timeProvider;
        this.deliveryBudget = deliveryBudget ?? TimeSpan.FromSeconds(5);
    }

    public Task Started
    {
        get { lock (lifecycle) return startup.Task; }
    }

    // Native resource subscriptions still own due times. Held intervals select
    // the execution mechanism only; acquisition and release never collect.
    public IBusinessBlockDependency AcquireSubscription(TimeSpan refreshInterval, string owner = "resource-sampling")
    {
        if (refreshInterval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(refreshInterval));
        var reason = new BusinessBlockReason(BusinessBlockRequestKind.Dependency, owner, SubscriptionCause,
            controller.CurrentPlanVersion);
        lock (lifecycle)
        {
            var dependency = controller.AcquireDependency(BusinessBlockId, reason, refreshInterval, Outputs);
            var lease = new Lease(this, refreshInterval, dependency);
            leases.Add(lease);
            lease.ObserveRelease();
            return lease;
        }
    }

    public Task<BusinessBlockResult> RunAsync(CancellationToken stopNotification)
    {
        Run run;
        lock (lifecycle)
        {
            if (activeRun is not null)
                throw new InvalidOperationException("The GPU allocation block already has an active run.");
            run = new Run(stopNotification);
            activeRun = run;
            startup = run.Started;
            lastRunOutcome = null;
        }
        return RunSessionAsync(run, stopNotification);
    }

    private async Task<BusinessBlockResult> RunSessionAsync(Run run, CancellationToken stopNotification)
    {
        var result = BusinessBlockResult.Stopped;
        Exception? cleanupFailure = null;
        var started = false;
        try
        {
            lock (lifecycle)
                if (runtimeReleaseBlocked)
                    throw new InvalidOperationException("The previous GPU allocation session was not released.");

            using (controller.MeasureSegment(BusinessBlockId)) StartSession(run);
            started = true;
            run.Started.TrySetResult();

            var stopSignal = NewSignal();
            using var stopRegistration = stopNotification.Register(static state => ((TaskCompletionSource)state!).TrySetResult(), stopSignal);
            await Task.WhenAny(run.ProcessingCompletion.Task, stopSignal.Task).ConfigureAwait(false);
            if (stopNotification.IsCancellationRequested)
            {
                result = BusinessBlockResult.Stopped;
            }
            else
            {
                var error = await run.ProcessingCompletion.Task.ConfigureAwait(false);
                result = error is null ? BusinessBlockResult.Done : BusinessBlockResult.Failed(error);
            }
        }
        catch (Exception error)
        {
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
                using (controller.MeasureSegment(BusinessBlockId)) cleanupFailure = StopSession(run);
            }
            finally
            {
                captureGate.Release();
                lock (lifecycle)
                {
                    if (ReferenceEquals(activeRun, run)) activeRun = null;
                    lastRunOutcome = cleanupFailure is null ? result.Outcome : BusinessBlockOutcome.Failed;
                }
                run.CaptureAdmission.Dispose();
            }
        }

        return cleanupFailure is null
            ? result
            : BusinessBlockResult.Failed(cleanupFailure, BusinessBlockFailureStage.Stop);
    }

    private void StartSession(Run run)
    {
        var name = ResourceManagerEtwSessionNames.Create(ResourceManagerEtwSessionNames.GpuAllocationsPrefix,
            ResourceManagerEtwSessionNames.CurrentOwner);
        IGpuAllocationSession current;
        try { current = sessionFactory.Create(name); }
        catch (KernelEtwSessionCreationCleanupException)
        {
            lock (lifecycle) runtimeReleaseBlocked = true;
            throw;
        }

        lock (lifecycle)
        {
            session = current;
            sessionName = name;
            run.Session = current;
        }

        current.Bind((provider, id, payload, timestamp) =>
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
                        }
                        else if (id == 3 && pending.Candidate is { } candidate)
                        {
                            if (SessionControl(run, () => current.EventsLost) != pending.EventsLost)
                                throw new InvalidDataException("GPU allocation events were lost between rundown boundaries.");
                            if (pending.KeepLive) { liveLedger = candidate; liveEventsLost = pending.EventsLost; }
                            pending.Candidate = null;
                            pending.Completion.TrySetResult(candidate);
                        }
                        return;
                    }
                    if (provider != Provider || !EventIds.Contains(id)) return;
                    var target = scan?.Candidate ?? (liveFailure is null ? liveLedger : null);
                    if (target is not null) GpuAllocationEventDecoder.Apply(target, id, payload, timestamp);
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
        });

        // Enabling one provider forces ETW session creation before the first due capture.
        // GPU allocation providers remain disabled until CaptureAsync is admitted.
        current.EnableProvider(BoundaryProvider);
        current.StartProcessing(error => OnSessionCompleted(run, current, error));
        if (!current.IsProcessing && !run.ProcessingCompletion.Task.IsCompleted)
            run.ProcessingCompletion.TrySetResult(null);
    }

    private void OnSessionCompleted(Run run, IGpuAllocationSession current, Exception? error)
    {
        if (run.StopNotification.IsCancellationRequested)
        {
            run.ProcessingCompletion.TrySetResult(null);
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
        run.ProcessingCompletion.TrySetResult(error);
        if (error is null)
            logger.LogInformation("GPU allocation event processing completed; the controller will release the owned session.");
        else
            logger.LogWarning(error, "GPU allocation event processing failed; the controller will apply the configured failure policy.");
    }

    private Exception? StopSession(Run run)
    {
        lock (gate)
        {
            scan?.Completion.TrySetException(new OperationCanceledException("GPU allocation block is stopping."));
            scan = null;
            liveLedger = null;
            liveFailure = null;
        }

        IGpuAllocationSession? current;
        lock (lifecycle)
        {
            current = run.Session;
            if (current is null) return null;
            if (runtimeReleaseBlocked) return new InvalidOperationException("The GPU allocation session release is unconfirmed.");
        }

        KernelEtwSessionStopResult released;
        try { released = current.Stop(Timeout.InfiniteTimeSpan); }
        catch (Exception error)
        {
            lock (lifecycle) runtimeReleaseBlocked = true;
            logger.LogError(error, "GPU allocation session release failed; the owned session handle remains retained.");
            return error;
        }

        if (!released.IsReleased)
        {
            lock (lifecycle) runtimeReleaseBlocked = true;
            var error = new IOException("GPU allocation session release was not confirmed: " + string.Join(" | ", released.Failures));
            logger.LogError(error, "GPU allocation session release failed; the owned session handle remains retained.");
            return error;
        }

        lock (lifecycle)
        {
            if (ReferenceEquals(session, current))
            {
                session = null;
                sessionName = null;
            }
        }
        return null;
    }

    private void Release(Lease lease)
    {
        lock (lifecycle)
        {
            if (!leases.Remove(lease)) return;
        }
        lease.Dependency.Dispose();
    }

    private Lease[] ActiveSubscriptionsLocked()
    {
        leases.RemoveWhere(static lease => lease.Released.IsCompleted);
        return leases.ToArray();
    }

    private sealed class Run(CancellationToken stopNotification)
    {
        internal CancellationToken StopNotification { get; } = stopNotification;
        internal CancellationTokenSource CaptureAdmission { get; } = new();
        internal TaskCompletionSource Started { get; } = NewSignal();
        internal TaskCompletionSource<Exception?> ProcessingCompletion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal IGpuAllocationSession? Session;
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

    private sealed class Lease : IBusinessBlockDependency
    {
        private readonly GpuAllocationCollector owner;
        private int disposed;
        internal TimeSpan Interval { get; }
        internal IBusinessBlockDependency Dependency { get; }
        public Task Ready => Dependency.Ready;
        public Task Released => Dependency.Released;

        internal Lease(GpuAllocationCollector owner, TimeSpan interval, IBusinessBlockDependency dependency)
        {
            this.owner = owner;
            Interval = interval;
            Dependency = dependency;
        }

        internal void ObserveRelease()
            => _ = Dependency.Released.ContinueWith(static (_, state) => ((Lease)state!).Dispose(), this, CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0) owner.Release(this);
        }
        public async ValueTask DisposeAsync()
        {
            Dispose();
            await Dependency.Released.ConfigureAwait(false);
        }
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
