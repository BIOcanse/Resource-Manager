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
    private const KernelTraceEventParser.Keywords RequiredKeywords = KernelTraceEventParser.Keywords.Interrupt
        | KernelTraceEventParser.Keywords.DeferedProcedureCalls | KernelTraceEventParser.Keywords.ImageLoad;

    private const string InterruptItemId = "system-interrupts";
    private static readonly ulong ZoneObjectKey = AdapterResourceKey.FromString($"resource-manager:zone:{MonitoringSourceZoneIds.EtwSystemInterrupts}");

    private readonly object subscriptionGate = new();
    private readonly object resourceDisposeGate = new();
    private readonly object eventGate = new();
    private readonly Func<DateTimeOffset> readUtcNow = static () => DateTimeOffset.UtcNow;
    private readonly Func<long> readTickMilliseconds = static () => Environment.TickCount64;
    private readonly SemaphoreSlim demandSignal = new(0, 1);
    private KernelModuleAddressMap modules = new();
    private SystemInterruptWindowAggregator aggregator = new();
    private readonly NativeItemSamplingSubscriptionTracker<InterruptSampleRequest> subscriptions =
        CreateSubscriptions(subscriptionOwner, null);
    private IKernelEtwSubscription? kernelSubscription;
    private long sessionGeneration;
    private int observedBrokerLoss;
    private bool disposed;
    private bool ownedResourcesDisposed;
    private long nextLeaseId;
    private long sessionStartedTick = long.MinValue;
    private ResourceManagerComputeZoneMode currentComputeMode = ResourceManagerComputeZoneMode.Normal;
    private volatile string state = "Idle";
    private volatile string message = "ETW 系统中断监控按报告需求启动。";
    private SystemInterruptSnapshot latestSnapshot =
        SystemInterruptSnapshot.Unavailable(
            "Idle",
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
        currentComputeMode = mode;
        if (mode == ResourceManagerComputeZoneMode.Freeze)
        {
            StopSession(
                "ETW 系统中断监控已按功能区冻结停止。",
                "Frozen");
        }
        else
        {
            SignalDemand();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var worker = new NativeSamplingSubscriptionWorker<InterruptSampleRequest, SamplingOwnerToken>(
            subscriptions, demandSignal,
            items =>
            {
                if (items.Count == 0 && HasSessionLease)
                    StopSession("ETW 系统中断监控已因无订阅而停止。", publishStoppedValue: false);
            },
            static failure => System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure.Error).Throw(),
            TimeSpan.Zero);
        try
        {
            await worker.RunAsync((plan, _) =>
            {
                EnsureStarted();
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
        if (currentComputeMode == ResourceManagerComputeZoneMode.Freeze)
        {
            return;
        }

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
                    RequiredKeywords,
                    BindKernelEvents));
            }
            catch (Exception ex)
            {
                state = "Unavailable";
                message = $"ETW 系统中断监控启动失败：{ex.Message}";
                PublishUnavailable(state, message);
                logger.LogWarning(ex, "Failed to start ETW system interrupt source.");
            }

            RefreshBrokerState();
        }
    }

    private void BindKernelEvents(
        KernelEtwEventSource kernel,
        KernelEtwSessionContext context)
    {
        var sessionModules = new KernelModuleAddressMap();
        var sessionAggregator = new SystemInterruptWindowAggregator();
        lock (eventGate)
        {
            Volatile.Write(ref modules, sessionModules);
            Volatile.Write(ref aggregator, sessionAggregator);
            sessionStartedTick = readTickMilliseconds();
            observedBrokerLoss = 0;
            Volatile.Write(ref sessionGeneration, context.Generation);
            state = "Running";
            message = "ETW 系统中断监控正在采集 ISR/DPC，并解析驱动模块。";
            PublishCurrentWindow(readUtcNow());
        }
        kernel.PerfInfoISR += data => ObserveBoundInterrupt(
            kernel, context, sessionModules, sessionAggregator,
            SystemInterruptEventKinds.Isr,
            data.ElapsedTimeMSec,
            data.Routine, readTickMilliseconds());
        kernel.PerfInfoDPC += data => ObserveBoundInterrupt(
            kernel, context, sessionModules, sessionAggregator,
            SystemInterruptEventKinds.Dpc,
            data.ElapsedTimeMSec,
            data.Routine, readTickMilliseconds());
        kernel.PerfInfoThreadedDPC += data => ObserveBoundInterrupt(
            kernel, context, sessionModules, sessionAggregator,
            SystemInterruptEventKinds.ThreadedDpc,
            data.ElapsedTimeMSec,
            data.Routine, readTickMilliseconds());
        kernel.PerfInfoTimerDPC += data => ObserveBoundInterrupt(
            kernel, context, sessionModules, sessionAggregator,
            SystemInterruptEventKinds.TimerDpc,
            data.ElapsedTimeMSec,
            data.Routine, readTickMilliseconds());
        kernel.ImageDCStart += data => ObserveBoundImage(kernel, context, sessionModules,
            data.ImageBase, data.ImageSize, data.FileName, false, readTickMilliseconds());
        kernel.ImageLoad += data => ObserveBoundImage(kernel, context, sessionModules,
            data.ImageBase, data.ImageSize, data.FileName, false, readTickMilliseconds());
        kernel.ImageUnload += data => ObserveBoundImage(kernel, context, sessionModules,
            data.ImageBase, data.ImageSize, data.FileName, true, readTickMilliseconds());
    }

    private void RefreshBrokerState(KernelEtwSessionSnapshot? observedSnapshot = null)
    {
        var snapshot = (observedSnapshot ?? (Volatile.Read(ref kernelSubscription)?.GetSnapshot() ?? kernelEtwBroker.GetSnapshot()));
        if (snapshot.IsRunning
            && snapshot.Generation == Volatile.Read(ref sessionGeneration))
        {
            lock (eventGate)
            {
                if (snapshot.Generation != Volatile.Read(ref sessionGeneration)) return;
                state = "Running";
                message = "ETW 系统中断监控正在采集 ISR/DPC，并解析驱动模块。";
                if (snapshot.EventsLost == observedBrokerLoss) return;
                // A missed unload invalidates address ownership as well as totals.
                modules.Clear();
                aggregator.Reset();
                sessionStartedTick = readTickMilliseconds();
                observedBrokerLoss = snapshot.EventsLost;
                PublishCurrentWindow(readUtcNow());
            }
            return;
        }

        state = snapshot.State;
        message = snapshot.Message;
        PublishUnavailable(state, message);
    }

    private bool TryPublishCurrentSnapshot(
        DateTimeOffset now,
        SamplingOwnerToken ownerToken)
    {
        lock (subscriptionGate)
        {
            var generation = Volatile.Read(ref sessionGeneration);
            var brokerSnapshot = (Volatile.Read(ref kernelSubscription)?.GetSnapshot() ?? kernelEtwBroker.GetSnapshot());
            RefreshBrokerState(brokerSnapshot);
            if (kernelSubscription is null
                || generation <= 0
                || !brokerSnapshot.IsRunning
                || brokerSnapshot.Generation != generation)
            {
                _ = ownerToken.TryPublish(() => PublishUnavailable(state, message));
                return false;
            }

            SystemInterruptWindowAggregator sampledAggregator;
            SystemInterruptSnapshot candidate;
            lock (eventGate)
            {
                if (generation != Volatile.Read(ref sessionGeneration)
                    || observedBrokerLoss != brokerSnapshot.EventsLost) return false;
                sampledAggregator = aggregator;
                candidate = sampledAggregator.CreateSnapshot(
                    now,
                    readTickMilliseconds() - sessionStartedTick,
                    generation,
                    0,
                    Environment.ProcessorCount,
                    new SystemInterruptProviderState(
                        "etw-system-interrupts",
                        state,
                        message,
                        brokerSnapshot.EventsLost));
            }
            // Never call the broker from inside eventGate: binding may need that lock.
            var after = (Volatile.Read(ref kernelSubscription)?.GetSnapshot() ?? kernelEtwBroker.GetSnapshot());
            if (!after.IsRunning || after.Generation != generation
                || after.EventsLost != brokerSnapshot.EventsLost)
            {
                RefreshBrokerState(after);
                return false;
            }
            lock (eventGate)
            {
                if (generation != Volatile.Read(ref sessionGeneration)
                    || !ReferenceEquals(sampledAggregator, aggregator)
                    || observedBrokerLoss != after.EventsLost) return false;
                return ownerToken.TryPublish(() => Volatile.Write(ref latestSnapshot, candidate));
            }
        }
    }

    // Call only while holding eventGate.
    private void PublishCurrentWindow(DateTimeOffset now)
        => Volatile.Write(ref latestSnapshot, aggregator.CreateSnapshot(now, readTickMilliseconds() - sessionStartedTick, sessionGeneration,
            0, Environment.ProcessorCount,
            new SystemInterruptProviderState("etw-system-interrupts", state, message, observedBrokerLoss)));

    private void PublishUnavailable(string snapshotState, string snapshotMessage)
        => Volatile.Write(
            ref latestSnapshot,
            SystemInterruptSnapshot.Unavailable(
                snapshotState,
                snapshotMessage));

    private void ObserveBoundInterrupt(KernelEtwEventSource source, KernelEtwSessionContext context,
        KernelModuleAddressMap sessionModules,
        SystemInterruptWindowAggregator sessionAggregator,
        string kind, double durationMilliseconds, ulong routineAddress, long observedTick)
    {
        if (!double.IsFinite(durationMilliseconds) || durationMilliseconds <= 0)
        {
            return;
        }

        lock (eventGate)
        {
            if (!source.IsActive || context.Generation != sessionGeneration
                || !ReferenceEquals(sessionModules, modules)
                || !ReferenceEquals(sessionAggregator, aggregator)
                || observedTick < sessionStartedTick) return;
            var module = sessionModules.Resolve(routineAddress);
            sessionAggregator.Observe(new SystemInterruptEventSample(
                observedTick - sessionStartedTick, kind, durationMilliseconds, routineAddress, module.Name, module.Path));
        }
    }

    private void ObserveBoundImage(KernelEtwEventSource source, KernelEtwSessionContext context,
        KernelModuleAddressMap sessionModules, ulong imageBase, int imageSize,
        string? fileName, bool unload, long observedTick)
    {
        lock (eventGate)
        {
            if (!source.IsActive || context.Generation != sessionGeneration
                || !ReferenceEquals(sessionModules, modules)
                || observedTick < sessionStartedTick) return;
            if (unload) sessionModules.Remove(imageBase);
            else sessionModules.Upsert(imageBase, imageSize, fileName);
        }
    }

    private void StopSession(string idleMessage, string idleState = "Idle", bool publishStoppedValue = true)
    {
        IKernelEtwSubscription? subscriptionToRelease;
        SystemInterruptWindowAggregator aggregatorToReset;
        KernelModuleAddressMap modulesToClear;
        lock (subscriptionGate)
        {
            subscriptionToRelease = kernelSubscription;
            kernelSubscription = null;
            lock (eventGate)
            {
                aggregatorToReset = Volatile.Read(ref aggregator);
                modulesToClear = Volatile.Read(ref modules);
                sessionStartedTick = long.MinValue;
                Volatile.Write(ref sessionGeneration, 0);
            }
        }

        subscriptionToRelease?.Dispose();
        aggregatorToReset.Reset();
        modulesToClear.Clear();
        lock (subscriptionGate)
        {
            if (kernelSubscription is not null) return;
            lock (eventGate)
            {
                sessionStartedTick = long.MinValue;
                Volatile.Write(ref sessionGeneration, 0);
                state = idleState;
                message = idleMessage;
                if (publishStoppedValue) PublishUnavailable(state, message);
            }
        }
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
