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

namespace ResourceManager.App.Infrastructure.SystemHealth.Interrupts;

public sealed class EtwSystemInterruptSnapshotSource(
    IKernelEtwSessionBroker kernelEtwBroker,
    HostManagerSamplingSubscriptionOwner subscriptionOwner,
    ILogger<EtwSystemInterruptSnapshotSource> logger) : BackgroundService, ISystemInterruptSnapshotSource, IResourceManagerSelfComputeZone
{
    private const string InterruptItemId = "system-interrupts";
    private static readonly ulong ZoneObjectKey = AdapterResourceKey.FromString($"resource-manager:zone:{MonitoringSourceZoneIds.EtwSystemInterrupts}");

    private readonly object subscriptionGate = new();
    private readonly SemaphoreSlim demandSignal = new(0, 1);
    private readonly KernelModuleAddressMap modules = new();
    private readonly SystemInterruptWindowAggregator aggregator = new();
    private readonly NativeItemSamplingSubscriptionTracker<InterruptSampleRequest> subscriptions = new(
        subscriptionOwner,
        roleId: 5,
        static request => request.SourceKey,
        static _ => [InterruptItemId],
        static (itemIds, _) => itemIds.Contains(
            InterruptItemId,
            StringComparer.OrdinalIgnoreCase)
            ? new InterruptSampleRequest("provider")
            : null,
        coalesceItemsIntoLatestCapture: true);
    private IKernelEtwSubscription? kernelSubscription;
    private long sessionGeneration;
    private long nextLeaseId;
    private DateTimeOffset sessionStartedAt = DateTimeOffset.MinValue;
    private ResourceManagerComputeZoneMode currentComputeMode = ResourceManagerComputeZoneMode.Normal;
    private volatile string state = "Idle";
    private volatile string message = "ETW 系统中断监控按报告需求启动。";
    private SystemInterruptSnapshot latestSnapshot =
        SystemInterruptSnapshot.Unavailable(
            "Idle",
            "ETW 系统中断监控按报告需求启动。");

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
        subscriptions.TrackPersistent(
            new InterruptSampleRequest(sourceKey),
            DateTimeOffset.UtcNow,
            refreshInterval);
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
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var plan = subscriptions.CreatePlan(DateTimeOffset.UtcNow);
                if (!plan.IsActive)
                {
                    if (IsRunning)
                    {
                        StopSession("ETW 系统中断监控已因无订阅而停止。");
                    }
                    await demandSignal.WaitAsync(stoppingToken);
                    continue;
                }

                if (plan.Request is null)
                {
                    await WaitForDemandOrDelayAsync(plan.Delay, stoppingToken);
                    continue;
                }

                EnsureStarted();
                var sampled = TryPublishCurrentSnapshot(
                    DateTimeOffset.UtcNow,
                    plan.OwnerToken);
                var completion = !plan.OwnerToken.IsActive
                    ? subscriptions.MarkSkipped(plan.Request, DateTimeOffset.UtcNow)
                    : sampled
                        ? subscriptions.MarkSampled(plan.Request, DateTimeOffset.UtcNow)
                        : subscriptions.MarkFailed(plan.Request, DateTimeOffset.UtcNow);
                await WaitForDemandOrDelayAsync(completion.Delay, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }

        StopSession("ETW 系统中断监控正在随程序停止。");
    }

    public override void Dispose()
    {
        base.Dispose();
        demandSignal.Dispose();
    }

    private async Task WaitForDemandOrDelayAsync(
        TimeSpan delay,
        CancellationToken cancellationToken)
    {
        if (delay <= TimeSpan.Zero)
        {
            return;
        }

        using var delayCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        var demandTask = demandSignal.WaitAsync(delayCts.Token);
        var delayTask = Task.Delay(delay, delayCts.Token);
        await Task.WhenAny(demandTask, delayTask);
        await delayCts.CancelAsync();
        try
        {
            await Task.WhenAll(demandTask, delayTask);
        }
        catch (OperationCanceledException) when (delayCts.IsCancellationRequested)
        {
        }
    }

    private void ReleaseLease(string sourceKey)
    {
        subscriptions.Remove(sourceKey, DateTimeOffset.UtcNow);
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

    private bool IsRunning
    {
        get
        {
            lock (subscriptionGate)
            {
                if (kernelSubscription is null)
                {
                    return false;
                }
            }

            var snapshot = kernelEtwBroker.GetSnapshot();
            return snapshot.IsRunning
                && snapshot.Generation == Volatile.Read(ref sessionGeneration);
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
            if (kernelSubscription is not null)
            {
                RefreshBrokerState();
                return;
            }

            try
            {
                kernelSubscription = kernelEtwBroker.Subscribe(new KernelEtwSubscriptionRequest(
                    "system-interrupts",
                    KernelTraceEventParser.Keywords.Interrupt
                    | KernelTraceEventParser.Keywords.ImageLoad,
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
        KernelTraceEventParser kernel,
        KernelEtwSessionContext context)
    {
        aggregator.Reset();
        modules.Clear();
        sessionStartedAt = context.StartedAt;
        Volatile.Write(ref sessionGeneration, context.Generation);
        state = "Running";
        message = "ETW 系统中断监控正在采集 ISR/DPC，并解析驱动模块。";
        Volatile.Write(
            ref latestSnapshot,
            aggregator.CreateSnapshot(
                context.StartedAt,
                context.Generation,
                context.StartedAt,
                Environment.ProcessorCount,
                new SystemInterruptProviderState(
                    "etw-system-interrupts",
                    state,
                    message)));
        kernel.PerfInfoISR += data => ObserveInterrupt(
            SystemInterruptEventKinds.Isr,
            data.ElapsedTimeMSec,
            data.Routine);
        kernel.PerfInfoDPC += data => ObserveInterrupt(
            SystemInterruptEventKinds.Dpc,
            data.ElapsedTimeMSec,
            data.Routine);
        kernel.PerfInfoThreadedDPC += data => ObserveInterrupt(
            SystemInterruptEventKinds.ThreadedDpc,
            data.ElapsedTimeMSec,
            data.Routine);
        kernel.PerfInfoTimerDPC += data => ObserveInterrupt(
            SystemInterruptEventKinds.TimerDpc,
            data.ElapsedTimeMSec,
            data.Routine);
        kernel.ImageDCStart += ObserveImageLoad;
        kernel.ImageLoad += ObserveImageLoad;
        kernel.ImageUnload += data => modules.Remove(data.ImageBase);
    }

    private void RefreshBrokerState()
    {
        var snapshot = kernelEtwBroker.GetSnapshot();
        if (snapshot.IsRunning
            && snapshot.Generation == Volatile.Read(ref sessionGeneration))
        {
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
            var brokerSnapshot = kernelEtwBroker.GetSnapshot();
            if (kernelSubscription is null
                || generation <= 0
                || !brokerSnapshot.IsRunning
                || brokerSnapshot.Generation != generation)
            {
                _ = ownerToken.TryPublish(() => PublishUnavailable(state, message));
                return false;
            }

            return ownerToken.TryPublish(() => Volatile.Write(
                ref latestSnapshot,
                aggregator.CreateSnapshot(
                    now,
                    generation,
                    sessionStartedAt,
                    Environment.ProcessorCount,
                    new SystemInterruptProviderState(
                        "etw-system-interrupts",
                        state,
                        message,
                        brokerSnapshot.EventsLost))));
        }
    }

    private void PublishUnavailable(string snapshotState, string snapshotMessage)
        => Volatile.Write(
            ref latestSnapshot,
            SystemInterruptSnapshot.Unavailable(
                snapshotState,
                snapshotMessage));

    private void ObserveImageLoad(ImageLoadTraceData data)
    {
        modules.Upsert(data.ImageBase, data.ImageSize, data.FileName);
    }

    private void ObserveInterrupt(string kind, double durationMilliseconds, ulong routineAddress)
    {
        if (!double.IsFinite(durationMilliseconds) || durationMilliseconds <= 0)
        {
            return;
        }

        var module = modules.Resolve(routineAddress);
        aggregator.Observe(new SystemInterruptEventSample(
            DateTimeOffset.UtcNow,
            kind,
            durationMilliseconds,
            routineAddress,
            module.Name,
            module.Path));
    }

    private void StopSession(string idleMessage, string idleState = "Idle")
    {
        IKernelEtwSubscription? subscriptionToRelease;
        lock (subscriptionGate)
        {
            subscriptionToRelease = kernelSubscription;
            kernelSubscription = null;
            sessionStartedAt = DateTimeOffset.MinValue;
            Volatile.Write(ref sessionGeneration, 0);
        }

        subscriptionToRelease?.Dispose();
        aggregator.Reset();
        modules.Clear();
        state = idleState;
        message = idleMessage;
        PublishUnavailable(state, message);
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
