using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;
using ResourceManager.Adapter;
using ResourceManager.App.Application.Adaptation;
using ResourceManager.App.Application.Monitoring;
using ResourceManager.App.Application.SystemHealth;
using ResourceManager.App.Domain.Adaptation;
using ResourceManager.App.Domain.SystemHealth;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.Telemetry.Etw;
using ResourceManager.App.Infrastructure.Adaptation;

namespace ResourceManager.App.Infrastructure.SystemHealth.Interrupts;

public sealed class EtwSystemInterruptSnapshotSource(
    IKernelEtwSessionBroker kernelEtwBroker,
    HostManagerSamplingSubscriptionOwner subscriptionOwner,
    ILogger<EtwSystemInterruptSnapshotSource> logger) : BackgroundService, ISystemInterruptSnapshotSource, IResourceManagerSelfComputeZone
{
    private const KernelTraceEventParser.Keywords EventKeywords = KernelTraceEventParser.Keywords.Interrupt
        | KernelTraceEventParser.Keywords.DeferedProcedureCalls;

    private const string InterruptItemId = "system-interrupts";
    private static readonly ulong ZoneObjectKey = AdapterResourceKey.FromString($"resource-manager:zone:{MonitoringSourceZoneIds.EtwSystemInterrupts}");

    private readonly object subscriptionGate = new();
    private readonly object resourceDisposeGate = new();
    private readonly object eventGate = new();
    private readonly Func<DateTimeOffset> readUtcNow = static () => DateTimeOffset.UtcNow;
    private readonly Func<long> readTickMilliseconds = static () => Environment.TickCount64;
    private readonly SemaphoreSlim demandSignal = new(0, 1);
    private InterruptSessionState? currentSession;
    private readonly NativeItemSamplingSubscriptionTracker<InterruptSampleRequest> subscriptions =
        CreateSubscriptions(subscriptionOwner, null);
    private IKernelEtwSubscription? kernelSubscription;
    private IKernelEtwSubscription? interruptSubscription;
    private long interruptEpoch;
    private bool acquiringInterrupts;
    private bool disposed;
    private bool ownedResourcesDisposed;
    private long nextLeaseId;
    private volatile ResourceManagerComputeZoneMode currentComputeMode = ResourceManagerComputeZoneMode.Normal;
    private volatile string state = SystemInterruptProviderStates.Idle;
    private volatile string message = "ETW 系统中断监控按报告需求启动。";
    private SystemInterruptSnapshot latestSnapshot =
        SystemInterruptSnapshot.Unavailable(
            SystemInterruptProviderStates.Idle,
            "ETW 系统中断监控按报告需求启动。");

    private static NativeItemSamplingSubscriptionTracker<InterruptSampleRequest> CreateSubscriptions(
        HostManagerSamplingSubscriptionOwner owner,
        Func<ulong, uint, ResourceManager.Subscription.INativeSamplingOutputScratch>? createOutputScratch)
        => new(owner, roleId: 5, static request => request.SourceKey,
            static _ => [InterruptItemId],
            static (itemIds, _) => itemIds.Contains(InterruptItemId, StringComparer.OrdinalIgnoreCase)
                ? new InterruptSampleRequest("provider") : null,
            coalesceItemsIntoLatestCapture: true, createOutputScratch);

    internal EtwSystemInterruptSnapshotSource(
        IKernelEtwSessionBroker kernelEtwBroker,
        HostManagerSamplingSubscriptionOwner subscriptionOwner,
        ILogger<EtwSystemInterruptSnapshotSource> logger,
        ResourceManagerLedgerOwner ledgerOwner)
        : this(kernelEtwBroker, subscriptionOwner, logger)
    {
        ArgumentNullException.ThrowIfNull(ledgerOwner);
        subscriptions = CreateSubscriptions(subscriptionOwner,
            (length, alignment) => SamplingOutputScratch.Create(ledgerOwner.CreateScratch, length, alignment));
    }

    internal EtwSystemInterruptSnapshotSource(IKernelEtwSessionBroker kernelEtwBroker,
        HostManagerSamplingSubscriptionOwner subscriptionOwner,
        ILogger<EtwSystemInterruptSnapshotSource> logger, Func<DateTimeOffset> readUtcNow,
        Func<long> readTickMilliseconds)
        : this(kernelEtwBroker, subscriptionOwner, logger)
    {
        this.readUtcNow = readUtcNow ?? throw new ArgumentNullException(nameof(readUtcNow));
        this.readTickMilliseconds = readTickMilliseconds ?? throw new ArgumentNullException(nameof(readTickMilliseconds));
    }

    public ulong ZoneKey => ZoneObjectKey;
    public string DisplayName => MonitoringSourceZoneIds.EtwSystemInterrupts;
    public ResourceManagerComputeZoneMode CurrentMode => currentComputeMode;

    public SystemInterruptSnapshot Read()
        => Volatile.Read(ref latestSnapshot);

    public IDisposable AcquireSubscription(
        string subscriptionId,
        TimeSpan refreshInterval)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subscriptionId);
        var leaseId = Interlocked.Increment(ref nextLeaseId);
        var sourceKey = $"lease:{leaseId}:{subscriptionId.Trim()}";
        lock (subscriptionGate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            subscriptions.TrackPersistent(
                new InterruptSampleRequest(sourceKey),
                DateTimeOffset.UtcNow,
                refreshInterval);
        }
        SignalDemand();
        return new InterruptSubscriptionLease(this, sourceKey);
    }

    public void ApplyMode(ResourceManagerComputeZoneMode mode)
    {
        IKernelEtwSubscription? held = null;
        lock (subscriptionGate)
        {
            if (disposed || currentComputeMode == mode) return;
            lock (eventGate)
            {
                if (currentComputeMode == ResourceManagerComputeZoneMode.Freeze
                    && Volatile.Read(ref currentSession) is { } session)
                    session.ResetWindow(readTickMilliseconds(), session.Window.EventsLost, preserveModules: true);
                currentComputeMode = mode;
                if (mode == ResourceManagerComputeZoneMode.Freeze)
                {
                    PublishFrozen();
                    held = DetachInterruptsCore();
                }
            }
        }
        held?.Dispose();
        if (mode != ResourceManagerComputeZoneMode.Freeze) EnsureInterruptsHeld();
        SignalDemand();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var worker = new NativeSamplingSubscriptionWorker<InterruptSampleRequest, SamplingOwnerToken>(
            subscriptions, demandSignal,
            static _ => { },
            static failure => System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure.Error).Throw());
        try
        {
            EnsureStarted();
            await worker.RunAsync((plan, _) =>
            {
                var sampled = TryPublishCurrentSnapshot(readUtcNow(), plan.OwnerToken);
                return Task.FromResult(!plan.OwnerToken.IsActive ? SubscriptionSampleCompletion.Skipped
                    : sampled ? SubscriptionSampleCompletion.Sampled : SubscriptionSampleCompletion.Failed);
            }, stoppingToken);
        }
        finally { StopSession("ETW 系统中断监控正在随程序停止。", publishStoppedValue: false); }
    }

    public override void Dispose()
    {
        lock (subscriptionGate)
        {
            disposed = true;
        }
        try
        {
            base.StopAsync(CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException) when (ExecuteTask?.IsCompleted == true)
        {
        }
        catch (Exception error)
        {
            logger.LogError(error, "ETW system-interrupt worker failed while draining for disposal.");
        }
        lock (resourceDisposeGate)
        {
            if (ownedResourcesDisposed) return;
            if (ExecuteTask is null || ExecuteTask.IsCompleted)
                StopSession("ETW 系统中断监控正在随程序停止。");
            subscriptions.Clear(DateTimeOffset.UtcNow);
            subscriptions.CloseOutputScratch();
            demandSignal.Dispose();
            base.Dispose();
            ownedResourcesDisposed = true;
        }
    }



    private void ReleaseLease(string sourceKey)
    {
        lock (subscriptionGate)
        {
            if (disposed) return;
            subscriptions.Remove(sourceKey, DateTimeOffset.UtcNow);
        }
        SignalDemand();
    }

    private void SignalDemand()
    {
        try
        {
            demandSignal.Release();
        }
        catch (SemaphoreFullException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private bool HasSessionLease
    {
        get
        {
            lock (subscriptionGate)
                return kernelSubscription is not null;
        }
    }

    private void EnsureStarted()
    {
        lock (subscriptionGate)
        {
            if (disposed) return;
            if (kernelSubscription is not null)
            {
                RefreshBrokerState();
                return;
            }

            try
            {
                kernelSubscription = kernelEtwBroker.Subscribe(new KernelEtwSubscriptionRequest(
                    "system-interrupts",
                    // Load/unload callbacks keep routine ownership current even while ISR/DPC is frozen.
                    KernelTraceEventParser.Keywords.ImageLoad,
                    BindKernelEvents));
            }
            catch (Exception ex)
            {
                if (currentComputeMode == ResourceManagerComputeZoneMode.Freeze)
                    PublishFrozen();
                else
                {
                    state = SystemInterruptProviderStates.Unavailable;
                    message = $"ETW 系统中断监控启动失败：{ex.Message}";
                    PublishUnavailable(state, message);
                }
                logger.LogWarning(ex, "Failed to start ETW system interrupt source.");
            }

        }
        EnsureInterruptsHeld();
        lock (subscriptionGate) RefreshBrokerState();
    }

    private void EnsureInterruptsHeld()
    {
        long epoch;
        lock (subscriptionGate)
        {
            if (disposed || kernelSubscription is null || currentComputeMode == ResourceManagerComputeZoneMode.Freeze
                || interruptSubscription is not null || acquiringInterrupts) return;
            acquiringInterrupts = true;
            epoch = ++interruptEpoch;
        }
        IKernelEtwSubscription? created = null;
        try
        {
            created = kernelEtwBroker.Subscribe(new("system-interrupts-events", EventKeywords, static (_, _) => { }));
            lock (subscriptionGate)
            {
                if (interruptEpoch == epoch && !disposed && kernelSubscription is not null
                    && currentComputeMode != ResourceManagerComputeZoneMode.Freeze)
                {
                    interruptSubscription = created;
                    created = null;
                }
            }
        }
        catch (Exception error) { logger.LogWarning(error, "Failed to hold ETW interrupt keywords."); }
        finally
        {
            lock (subscriptionGate)
                if (interruptEpoch == epoch) acquiringInterrupts = false;
            created?.Dispose();
        }
    }

    private IKernelEtwSubscription? DetachInterruptsCore()
    {
        ++interruptEpoch;
        acquiringInterrupts = false;
        var held = interruptSubscription;
        interruptSubscription = null;
        return held;
    }

    private KernelEtwSessionSnapshot CaptureBrokerSnapshot()
    {
        var events = Volatile.Read(ref interruptSubscription)?.GetSnapshot();
        var metadata = Volatile.Read(ref kernelSubscription)?.GetSnapshot() ?? kernelEtwBroker.GetSnapshot();
        if (!metadata.IsRunning) return metadata;
        return events
            ?? metadata with { State = "Unavailable", Message = "ETW interrupt keywords are unavailable." };
    }

    private void BindKernelEvents(
        KernelEtwEventSource kernel,
        KernelEtwSessionContext context)
    {
        var session = new InterruptSessionState(context.Generation, readTickMilliseconds(), 0);
        lock (eventGate)
        {
            Volatile.Write(ref currentSession, session);
            if (currentComputeMode == ResourceManagerComputeZoneMode.Freeze)
                PublishFrozen();
            else
            {
                state = SystemInterruptProviderStates.Running;
                message = "ETW 系统中断监控正在采集 ISR/DPC，并解析驱动模块。";
                PublishCurrentWindow(readUtcNow(), session);
            }
        }
        kernel.PerfInfoISR += data => ObserveBoundInterrupt(
            kernel, context, session,
            SystemInterruptEventKinds.Isr,
            data.ElapsedTimeMSec,
            data.Routine, readTickMilliseconds());
        kernel.PerfInfoDPC += data => ObserveBoundInterrupt(
            kernel, context, session,
            SystemInterruptEventKinds.Dpc,
            data.ElapsedTimeMSec,
            data.Routine, readTickMilliseconds());
        kernel.PerfInfoThreadedDPC += data => ObserveBoundInterrupt(
            kernel, context, session,
            SystemInterruptEventKinds.ThreadedDpc,
            data.ElapsedTimeMSec,
            data.Routine, readTickMilliseconds());
        kernel.PerfInfoTimerDPC += data => ObserveBoundInterrupt(
            kernel, context, session,
            SystemInterruptEventKinds.TimerDpc,
            data.ElapsedTimeMSec,
            data.Routine, readTickMilliseconds());
        kernel.ImageDCStart += data => ObserveBoundImage(kernel, context, session,
            data.ImageBase, data.ImageSize, data.FileName, false, readTickMilliseconds());
        kernel.ImageLoad += data => ObserveBoundImage(kernel, context, session,
            data.ImageBase, data.ImageSize, data.FileName, false, readTickMilliseconds());
        kernel.ImageUnload += data => ObserveBoundImage(kernel, context, session,
            data.ImageBase, data.ImageSize, data.FileName, true, readTickMilliseconds());
    }

    private void RefreshBrokerState(KernelEtwSessionSnapshot? observedSnapshot = null)
    {
        if (currentComputeMode == ResourceManagerComputeZoneMode.Freeze) return;
        var snapshot = observedSnapshot ?? CaptureBrokerSnapshot();
        var session = Volatile.Read(ref currentSession);
        if (snapshot.IsRunning
            && session is not null
            && snapshot.Generation == session.Generation)
        {
            lock (eventGate)
            {
                if (!ReferenceEquals(session, Volatile.Read(ref currentSession))) return;
                state = SystemInterruptProviderStates.Running;
                message = "ETW 系统中断监控正在采集 ISR/DPC，并解析驱动模块。";
                if (snapshot.EventsLost == session.Window.EventsLost) return;
                // A missed unload invalidates both address ownership and totals. Replacing the
                // immutable window reference lets callbacks finish against an unreachable old window.
                session.ResetWindow(readTickMilliseconds(), snapshot.EventsLost);
                PublishCurrentWindow(readUtcNow(), session);
            }
            return;
        }

        state = snapshot.State switch
        {
            "Running" => SystemInterruptProviderStates.Running,
            "Idle" => SystemInterruptProviderStates.Idle,
            "Starting" => SystemInterruptProviderStates.Starting,
            "Stopped" => SystemInterruptProviderStates.Stopped,
            "StopFailed" => SystemInterruptProviderStates.StopFailed,
            "Available" => SystemInterruptProviderStates.Available,
            "Unavailable" => SystemInterruptProviderStates.Unavailable,
            _ => throw new InvalidDataException($"Unknown kernel ETW state '{snapshot.State}'.")
        };
        message = snapshot.Message;
        PublishUnavailable(state, message);
    }

    private bool TryPublishCurrentSnapshot(
        DateTimeOffset now,
        SamplingOwnerToken ownerToken)
    {
        lock (subscriptionGate)
        {
            if (currentComputeMode == ResourceManagerComputeZoneMode.Freeze) return false;
            var brokerSnapshot = CaptureBrokerSnapshot();
            RefreshBrokerState(brokerSnapshot);
            var session = Volatile.Read(ref currentSession);
            if (kernelSubscription is null
                || session is null
                || session.Generation <= 0
                || !brokerSnapshot.IsRunning
                || brokerSnapshot.Generation != session.Generation)
            {
                _ = ownerToken.TryPublish(() => PublishUnavailable(state, message));
                return false;
            }

            var sampledWindow = session.Window;
            SystemInterruptSnapshot candidate;
            if (sampledWindow.EventsLost != brokerSnapshot.EventsLost) return false;
            candidate = sampledWindow.Aggregator.CreateSnapshot(
                now,
                readTickMilliseconds() - sampledWindow.StartedTick,
                session.Generation,
                0,
                Environment.ProcessorCount,
                new SystemInterruptProviderState(
                    "etw-system-interrupts",
                    state,
                    message,
                    brokerSnapshot.EventsLost));
            // Never call the broker from inside eventGate: binding may need that lock.
            var after = CaptureBrokerSnapshot();
            if (!after.IsRunning || after.Generation != session.Generation
                || after.EventsLost != brokerSnapshot.EventsLost)
            {
                RefreshBrokerState(after);
                return false;
            }
            lock (eventGate)
            {
                if (!ReferenceEquals(session, Volatile.Read(ref currentSession))
                    || !ReferenceEquals(sampledWindow, session.Window)
                    || sampledWindow.EventsLost != after.EventsLost) return false;
                return ownerToken.TryPublish(() => Volatile.Write(ref latestSnapshot, candidate));
            }
        }
    }

    // Call only while holding eventGate.
    private void PublishCurrentWindow(DateTimeOffset now, InterruptSessionState session)
    {
        var window = session.Window;
        Volatile.Write(ref latestSnapshot, window.Aggregator.CreateSnapshot(now,
            readTickMilliseconds() - window.StartedTick, session.Generation,
            0, Environment.ProcessorCount,
            new SystemInterruptProviderState("etw-system-interrupts", state, message, window.EventsLost)));
    }

    private void PublishUnavailable(string snapshotState, string snapshotMessage)
        => Volatile.Write(
            ref latestSnapshot,
            SystemInterruptSnapshot.Unavailable(
                snapshotState,
                snapshotMessage));

    private void PublishFrozen()
    {
        state = SystemInterruptProviderStates.Frozen;
        message = "ETW 系统中断监控已按功能区冻结暂停采集。";
        PublishUnavailable(state, message);
    }

    private void ObserveBoundInterrupt(KernelEtwEventSource source, KernelEtwSessionContext context,
        InterruptSessionState session,
        string kind, double durationMilliseconds, ulong routineAddress, long observedTick)
    {
        var counter = source.ConsumerCounter(kind);
        counter.Receive();
        void Reject(string reason)
        {
            counter.Drop();
            source.RecordConsumerRejection(kind, reason);
        }
        if (!double.IsFinite(durationMilliseconds) || durationMilliseconds <= 0)
        {
            Reject("invalid-duration");
            return;
        }

        if (currentComputeMode == ResourceManagerComputeZoneMode.Freeze) { Reject("frozen"); return; }
        if (!source.IsActive) { Reject("inactive-binding"); return; }
        if (context.Generation != session.Generation
            || !ReferenceEquals(session, Volatile.Read(ref currentSession))) { Reject("generation-mismatch"); return; }
        var window = session.Window;
        if (observedTick < window.StartedTick) { Reject("before-window"); return; }
        var module = window.Modules.Resolve(routineAddress);
        window.Aggregator.Observe(new SystemInterruptEventSample(
            observedTick - window.StartedTick, kind, durationMilliseconds, routineAddress, module.Name, module.Path));
    }

    private void ObserveBoundImage(KernelEtwEventSource source, KernelEtwSessionContext context,
        InterruptSessionState session, ulong imageBase, int imageSize,
        string? fileName, bool unload, long observedTick)
    {
        var kind = unload ? "ImageUnload" : "ImageLoadOrRundown";
        var counter = source.ConsumerCounter(kind);
        counter.Receive();
        void Reject(string reason)
        {
            counter.Drop();
            source.RecordConsumerRejection(kind, reason);
        }
        // Keep image ownership current while ISR/DPC intake is paused, just as CPU
        // residency retains process/thread metadata on its held kernel session.
        if (!source.IsActive) { Reject("inactive-binding"); return; }
        if (context.Generation != session.Generation
            || !ReferenceEquals(session, Volatile.Read(ref currentSession))) { Reject("generation-mismatch"); return; }
        var window = session.Window;
        if (observedTick < window.StartedTick) { Reject("before-window"); return; }
        if (unload) window.Modules.Remove(imageBase);
        else window.Modules.Upsert(imageBase, imageSize, fileName);
    }

    private void StopSession(string idleMessage, string idleState = SystemInterruptProviderStates.Idle, bool publishStoppedValue = true)
    {
        IKernelEtwSubscription? subscriptionToRelease;
        IKernelEtwSubscription? eventsToRelease;
        lock (subscriptionGate)
        {
            subscriptionToRelease = kernelSubscription;
            eventsToRelease = DetachInterruptsCore();
            kernelSubscription = null;
            lock (eventGate)
            {
                Volatile.Write(ref currentSession, null);
            }
        }

        try { eventsToRelease?.Dispose(); }
        finally { subscriptionToRelease?.Dispose(); }
        lock (subscriptionGate)
        {
            if (kernelSubscription is not null) return;
            lock (eventGate)
            {
                state = idleState;
                message = idleMessage;
                if (publishStoppedValue) PublishUnavailable(state, message);
            }
        }
    }

    private sealed record InterruptObservationWindow(
        long StartedTick,
        int EventsLost,
        KernelModuleAddressMap Modules,
        SystemInterruptWindowAggregator Aggregator);

    private sealed class InterruptSessionState(long generation, long startedTick, int eventsLost)
    {
        private InterruptObservationWindow window = new(startedTick, eventsLost,
            new KernelModuleAddressMap(), new SystemInterruptWindowAggregator());

        public long Generation { get; } = generation;
        public InterruptObservationWindow Window => Volatile.Read(ref window);
        public void ResetWindow(long atTick, int lost, bool preserveModules = false)
            => Volatile.Write(ref window, new InterruptObservationWindow(atTick, lost,
                preserveModules ? Window.Modules : new KernelModuleAddressMap(), new SystemInterruptWindowAggregator()));
    }

    private sealed record InterruptSampleRequest(string SourceKey);

    private sealed class InterruptSubscriptionLease(
        EtwSystemInterruptSnapshotSource owner,
        string sourceKey) : IDisposable
    {
        private int disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0)
            {
                owner.ReleaseLease(sourceKey);
            }
        }
    }
}
