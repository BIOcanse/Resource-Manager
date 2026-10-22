using ResourceManager.ReadWrite.Windows.Metrics.Capture;
using ResourceManager.ReadWrite.Windows.Metrics.Writes;
using ResourceManager.App.Infrastructure.Control.Writers;
using ResourceManager.ReadWrite.Windows.Metrics.Resources;
using ResourceManager.ReadWrite.Native;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ResourceManager.App.Application.Metrics;
using ResourceManager.App.Application.Monitoring;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.Monitoring;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.ReadWrite.Vendors.Amd.Smu;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;
using System.Runtime.CompilerServices;
using System.Collections.Immutable;
using ResourceManager.ReadWrite;
using ResourceManager.Subscription;
using ResourceManager.App.Infrastructure.Adaptation;

namespace ResourceManager.App.Infrastructure.Monitoring;

public sealed partial class WindowsHardwareMetricSampler :
    IMetricSampler,
    IMetricSnapshotObservationSource,
    IMetricSnapshotPushSource,
    IHostedService,
    IDisposable
{
    private readonly WindowsCpuMonitoringZone cpuZone;
    private readonly WindowsMemoryMonitoringZone memoryZone;
    private readonly WindowsVirtualMemoryMonitoringZone virtualMemoryZone;
    private readonly WindowsGpuAdapterOrderMonitoringZone gpuAdapterOrderZone;
    private readonly PdhCpuFrequencyMonitoringZone cpuFrequencyZone;
    private readonly PdhGpuEngineMonitoringZone gpuEngineZone;
    private readonly PdhSystemIoMonitoringZone systemIoZone;
    private readonly NvidiaNvmlMonitoringZone nvidiaZone;
    private readonly NvidiaNvapiMonitoringZone nvidiaNvapiZone;
    private readonly AmdAdlxMonitoringZone amdAdlxZone;
    private readonly AmdSmuMonitoringZone amdSmuZone;
    private readonly IntelMsrMonitoringZone intelMsrZone;
    private readonly WindowsNpuMemoryMonitoringZone npuMemoryZone;
    private readonly WindowsNpuEngineMonitoringZone npuEngineZone;
    private readonly IntelGclMonitoringZone intelGclZone;
    private readonly IpHelperNetworkMonitoringZone networkZone;
    // 哪一块卡是核显 —— 启动时认一次，之后不变。
    private readonly AmdIntegratedGpuSensorRoute integratedGpuSensorRoute;
    private readonly WindowsPlatformSensorReader platformSensorReader;
    private readonly WindowsBatteryMonitoringZone batteryZone;
    private readonly HostManagerMetricSnapshotOwner metricSnapshotOwner;
    private readonly DashboardMonitoringCatalogState dashboardMonitoringCatalog;
    private readonly WindowsHardwareMetricCapture capture;
    private readonly ILogger<WindowsHardwareMetricSampler> logger;
    private readonly RuntimePlanProvider runtimePlanProvider;
    private readonly object lifecycleGate = new();
    private readonly SemaphoreSlim demandSignal = new(0, 1);
    private readonly SemaphoreSlim sampleGate;
    private readonly SamplingPublicationSignal publicationSignal = new();
    private readonly LastSuccessfulHardwareMetricSnapshot publishedSnapshot = new("Host Manager metric snapshot");
    // 当前处于“被拒绝”状态的 dataset；只在持有 sampleGate 的发布路径上读写，用于只在状态变化时记日志。
    private readonly HashSet<string> rejectedDatasetIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly NativeItemSamplingSubscriptionTracker<MetricSampleRequest>
        subscriptions;
    private readonly NativeSamplingSubscriptionWorker<MetricSampleRequest, SamplingOwnerToken> samplingWorker;
    private CancellationTokenSource? samplingCts;
    private Task? samplingTask;
    private Task? samplingDrainTask;
    private ulong? samplingRunId;
    private bool samplingStopInProgress;
    private long nextObservationLeaseId;
    private int disposed;
    private int ownedResourcesDisposed;

    internal WindowsHardwareMetricSampler(
        WindowsCpuMonitoringZone cpuZone,
        WindowsMemoryMonitoringZone memoryZone,
        WindowsVirtualMemoryMonitoringZone virtualMemoryZone,
        WindowsGpuAdapterOrderMonitoringZone gpuAdapterOrderZone,
        PdhCpuFrequencyMonitoringZone cpuFrequencyZone,
        PdhGpuEngineMonitoringZone gpuEngineZone,
        PdhSystemIoMonitoringZone systemIoZone,
        NvidiaNvmlMonitoringZone nvidiaZone,
        NvidiaNvapiMonitoringZone nvidiaNvapiZone,
        AmdAdlxMonitoringZone amdAdlxZone,
        AmdSmuMonitoringZone amdSmuZone,
        AmdIntegratedGpuSensorRoute integratedGpuSensorRoute,
        WindowsPlatformSensorReader platformSensorReader,
        HostManagerMetricSnapshotOwner metricSnapshotOwner,
        DashboardMonitoringCatalogState dashboardMonitoringCatalog,
        IMonitoringSourceZoneRegistry monitoringSourceZoneRegistry,
        HostManagerSamplingSubscriptionOwner samplingSubscriptionOwner,
        RuntimePlanProvider runtimePlanProvider,
        ILogger<WindowsHardwareMetricSampler> logger,
        WindowsBatteryMonitoringZone? batteryZone = null,
        IntelMsrMonitoringZone? intelMsrZone = null,
        IpHelperNetworkMonitoringZone? networkZone = null,
        IntelGclMonitoringZone? intelGclZone = null,
        WindowsMetricControlResources? borrowedControls = null,
        WindowsNpuMemoryMonitoringZone? npuMemoryZone = null, WindowsNpuEngineMonitoringZone? npuEngineZone = null)
        : this(cpuZone, memoryZone, virtualMemoryZone, gpuAdapterOrderZone, cpuFrequencyZone,
            gpuEngineZone, systemIoZone, nvidiaZone, nvidiaNvapiZone, amdAdlxZone, amdSmuZone,
            integratedGpuSensorRoute, platformSensorReader, metricSnapshotOwner, dashboardMonitoringCatalog,
            monitoringSourceZoneRegistry, samplingSubscriptionOwner, runtimePlanProvider, logger, batteryZone,
            intelMsrZone, networkZone, intelGclZone, borrowedControls, npuMemoryZone, npuEngineZone, null)
    {
    }

    private WindowsHardwareMetricSampler(
        WindowsCpuMonitoringZone cpuZone,
        WindowsMemoryMonitoringZone memoryZone,
        WindowsVirtualMemoryMonitoringZone virtualMemoryZone,
        WindowsGpuAdapterOrderMonitoringZone gpuAdapterOrderZone,
        PdhCpuFrequencyMonitoringZone cpuFrequencyZone,
        PdhGpuEngineMonitoringZone gpuEngineZone,
        PdhSystemIoMonitoringZone systemIoZone,
        NvidiaNvmlMonitoringZone nvidiaZone,
        NvidiaNvapiMonitoringZone nvidiaNvapiZone,
        AmdAdlxMonitoringZone amdAdlxZone,
        AmdSmuMonitoringZone amdSmuZone,
        AmdIntegratedGpuSensorRoute integratedGpuSensorRoute,
        WindowsPlatformSensorReader platformSensorReader,
        HostManagerMetricSnapshotOwner metricSnapshotOwner,
        DashboardMonitoringCatalogState dashboardMonitoringCatalog,
        IMonitoringSourceZoneRegistry monitoringSourceZoneRegistry,
        HostManagerSamplingSubscriptionOwner samplingSubscriptionOwner,
        RuntimePlanProvider runtimePlanProvider,
        ILogger<WindowsHardwareMetricSampler> logger,
        WindowsBatteryMonitoringZone? batteryZone,
        IntelMsrMonitoringZone? intelMsrZone,
        IpHelperNetworkMonitoringZone? networkZone,
        IntelGclMonitoringZone? intelGclZone,
        WindowsMetricControlResources? borrowedControls,
        WindowsNpuMemoryMonitoringZone? npuMemoryZone,
        WindowsNpuEngineMonitoringZone? npuEngineZone,
        Func<ulong, uint, INativeSamplingOutputScratch>? createOutputScratch)
    {
        this.cpuZone = cpuZone;
        this.memoryZone = memoryZone;
        this.virtualMemoryZone = virtualMemoryZone;
        this.gpuAdapterOrderZone = gpuAdapterOrderZone;
        this.cpuFrequencyZone = cpuFrequencyZone;
        this.gpuEngineZone = gpuEngineZone;
        this.systemIoZone = systemIoZone;
        this.nvidiaZone = nvidiaZone;
        this.nvidiaNvapiZone = nvidiaNvapiZone;
        this.amdAdlxZone = amdAdlxZone;
        this.amdSmuZone = amdSmuZone;
        this.npuMemoryZone = npuMemoryZone ?? new WindowsNpuMemoryMonitoringZone();
        this.npuEngineZone = npuEngineZone ?? new WindowsNpuEngineMonitoringZone();
        this.intelMsrZone = intelMsrZone ?? new IntelMsrMonitoringZone(AppContext.BaseDirectory);
        this.integratedGpuSensorRoute = integratedGpuSensorRoute;
        this.platformSensorReader = platformSensorReader;
        this.networkZone = networkZone ?? new IpHelperNetworkMonitoringZone();
        this.intelGclZone = intelGclZone ?? new IntelGclMonitoringZone();
        this.batteryZone = batteryZone ?? new WindowsBatteryMonitoringZone();
        this.metricSnapshotOwner = metricSnapshotOwner;
        this.dashboardMonitoringCatalog = dashboardMonitoringCatalog;
        var resources = CreateBorrowedResources(borrowedControls);
        controlFields = new WindowsMetricControlFields(resources.Controls!, resources.ReadGpuInventory, sourceId =>
            monitoringSourceZoneRegistry.GetZone(NativeMetricSnapshotSourceCatalog.ResolveZoneId(sourceId switch
            {
                "control.intel.msr" => NativeMetricSnapshotSourceCatalog.IntelMsr,
                "control.amd.hardware-bridge" or "control.amd.igpu.hardware-bridge" => NativeMetricSnapshotSourceCatalog.AmdSmu,
                "control.amd.adlx" => NativeMetricSnapshotSourceCatalog.AmdAdlx,
                "control.intel.igcl" => NativeMetricSnapshotSourceCatalog.IntelGcl,
                _ => throw new ArgumentException("Unknown control source.", nameof(sourceId))
            })).CurrentMode == ResourceManager.App.Domain.Adaptation.ResourceManagerComputeZoneMode.Freeze);
        capture = new(resources,
            sourceId => monitoringSourceZoneRegistry.GetMode(NativeMetricSnapshotSourceCatalog.ResolveZoneId(sourceId)) switch
            {
                ResourceManager.App.Domain.Adaptation.ResourceManagerComputeZoneMode.Normal => NativeMetricSnapshotZoneMode.Normal,
                ResourceManager.App.Domain.Adaptation.ResourceManagerComputeZoneMode.LowPower => NativeMetricSnapshotZoneMode.LowPower,
                ResourceManager.App.Domain.Adaptation.ResourceManagerComputeZoneMode.Freeze => NativeMetricSnapshotZoneMode.Freeze,
                _ => throw new InvalidOperationException("The monitoring source zone mode is invalid.")
            }, TimeSpan.FromSeconds(5), integratedGpuSensorRoute, () => metricSnapshotOwner.RequestTopologyRefresh(),
            (error, sourceId) => logger.LogWarning(error, "Metric source {SourceId} failed.", sourceId));
        sampleGate = capture.SampleGate;
        subscriptions = CreateSubscriptions(samplingSubscriptionOwner, createOutputScratch);
        this.logger = logger;
        samplingWorker = new(subscriptions, demandSignal, SynchronizeCounterDemand,
            ReportSamplingWorkerFailure, TimeSpan.FromSeconds(1));
        this.runtimePlanProvider = runtimePlanProvider;
        runtimePlanProvider.Published += OnRuntimePlanPublished;
        OnRuntimePlanPublished(runtimePlanProvider.Current);
    }

    // Production DI selects this internal route explicitly; the ordinary
    // constructor continues to use the tracker’s managed output buffers.
    internal WindowsHardwareMetricSampler(
        WindowsCpuMonitoringZone cpuZone,
        WindowsMemoryMonitoringZone memoryZone,
        WindowsVirtualMemoryMonitoringZone virtualMemoryZone,
        WindowsGpuAdapterOrderMonitoringZone gpuAdapterOrderZone,
        PdhCpuFrequencyMonitoringZone cpuFrequencyZone,
        PdhGpuEngineMonitoringZone gpuEngineZone,
        PdhSystemIoMonitoringZone systemIoZone,
        NvidiaNvmlMonitoringZone nvidiaZone,
        NvidiaNvapiMonitoringZone nvidiaNvapiZone,
        AmdAdlxMonitoringZone amdAdlxZone,
        AmdSmuMonitoringZone amdSmuZone,
        AmdIntegratedGpuSensorRoute integratedGpuSensorRoute,
        WindowsPlatformSensorReader platformSensorReader,
        HostManagerMetricSnapshotOwner metricSnapshotOwner,
        DashboardMonitoringCatalogState dashboardMonitoringCatalog,
        IMonitoringSourceZoneRegistry monitoringSourceZoneRegistry,
        HostManagerSamplingSubscriptionOwner samplingSubscriptionOwner,
        RuntimePlanProvider runtimePlanProvider,
        ILogger<WindowsHardwareMetricSampler> logger,
        ResourceManagerLedgerOwner ledgerOwner,
        WindowsBatteryMonitoringZone? batteryZone = null,
        IntelMsrMonitoringZone? intelMsrZone = null,
        IpHelperNetworkMonitoringZone? networkZone = null,
        IntelGclMonitoringZone? intelGclZone = null,
        WindowsMetricControlResources? borrowedControls = null,
        WindowsNpuMemoryMonitoringZone? npuMemoryZone = null,
        WindowsNpuEngineMonitoringZone? npuEngineZone = null)
        : this(cpuZone, memoryZone, virtualMemoryZone, gpuAdapterOrderZone,
            cpuFrequencyZone, gpuEngineZone, systemIoZone, nvidiaZone, nvidiaNvapiZone,
            amdAdlxZone, amdSmuZone, integratedGpuSensorRoute, platformSensorReader,
            metricSnapshotOwner, dashboardMonitoringCatalog, monitoringSourceZoneRegistry,
            samplingSubscriptionOwner, runtimePlanProvider, logger, batteryZone, intelMsrZone,
            networkZone, intelGclZone, borrowedControls, npuMemoryZone, npuEngineZone,
            CreateLedgerOutputFactory(ledgerOwner))
    {
    }

    private static Func<ulong, uint, INativeSamplingOutputScratch> CreateLedgerOutputFactory(
        ResourceManagerLedgerOwner ledgerOwner)
    {
        ArgumentNullException.ThrowIfNull(ledgerOwner);
        return (length, alignment) =>
            SamplingOutputScratch.Create(ledgerOwner.CreateScratch, length, alignment);
    }

    private NativeItemSamplingSubscriptionTracker<MetricSampleRequest> CreateSubscriptions(
        HostManagerSamplingSubscriptionOwner owner,
        Func<ulong, uint, INativeSamplingOutputScratch>? createOutputScratch)
        => new(owner, 1, static request => request.CacheKey, GetSubscriptionItemIds,
            CreateMetricSampleRequest, coalesceItemsIntoLatestCapture: false, createOutputScratch);

    private void OnRuntimePlanPublished(CompiledRuntimePlan plan)
    {
        _ = plan;
        using var publication = runtimePlanProvider.AcquirePublicationLease();
        publishedSnapshot.ConfigureHistory(publication.Plan.HostManager.DataHistory);
    }

    public Task<HardwareMetricSnapshot> GetSnapshotAsync(
        CancellationToken cancellationToken)
        => GetSnapshotAsync(MetricSampleRequest.All, cancellationToken);

    public Task<HardwareMetricSnapshot> GetSnapshotAsync(
        MetricSampleRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = ReadCachedSnapshot();
        return Task.FromResult(snapshot is null
            ? CreateWarmingSnapshot(request)
            : ProjectSnapshot(snapshot, request));
    }

    public async Task<HardwareMetricSnapshot> CaptureSnapshotAsync(
        MetricSampleRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        await sampleGate.WaitAsync(cancellationToken);
        try
        {
            var snapshot = await ReadSnapshotCoreAsync(
                request,
                cancellationToken,
                completed =>
                {
                    _ = publishedSnapshot.Publish(completed, request);
                });
            if (request.IncludesCatalog) dashboardMonitoringCatalog.PublishCatalogProbe(publishedSnapshot.Read()!);
            PublishCompletedDatasets(GetPublicationItemIds(request));
            return ProjectSnapshot(snapshot, request);
        }
        finally
        {
            sampleGate.Release();
        }
    }

    public IDisposable AcquireSubscription(
        string subscriptionId,
        MetricSampleRequest request,
        TimeSpan refreshInterval)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(subscriptionId);
        ArgumentNullException.ThrowIfNull(request);
        var subscriptionRequest =
            dashboardMonitoringCatalog.SelectSubscribable(request);
        if (subscriptionRequest.IsEmpty)
        {
            return EmptyObservationSubscription.Instance;
        }
        var leaseId = Interlocked.Increment(ref nextObservationLeaseId);
        var sourceKey = $"lease:{leaseId}:{subscriptionId.Trim()}";
        lock (lifecycleGate)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
            subscriptions.TrackPersistent(
                sourceKey,
                subscriptionRequest,
                DateTimeOffset.UtcNow,
                refreshInterval);
        }
        SignalDemand(demandSignal);
        return new ObservationSubscriptionLease(this, sourceKey);
    }

    public HardwareMetricSnapshot? ReadLatest(MetricSampleRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var snapshot = ReadCachedSnapshot();
        return snapshot is null
            ? null
            : ProjectSnapshot(snapshot, request);
    }

    public async IAsyncEnumerable<HardwareMetricSnapshot> SubscribeAsync(
        string subscriptionId,
        MetricSampleRequest request,
        TimeSpan interval,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subscriptionId);
        ArgumentNullException.ThrowIfNull(request);
        var subscriptionRequest = dashboardMonitoringCatalog.SelectSubscribable(request);
        var datasetIds = GetSubscriptionItemIds(subscriptionRequest).ToArray();
        var publicationDatasetIds = GetPublicationItemIds(subscriptionRequest)
            .ToArray();
        if (subscriptionRequest.IsEmpty
            || datasetIds.Length == 0
            || publicationDatasetIds.Length == 0)
        {
            yield break;
        }

        using var publication = publicationSignal.Subscribe(publicationDatasetIds);
        using var lease = AcquireSubscription(
            subscriptionId,
            subscriptionRequest,
            interval);

        while (!cancellationToken.IsCancellationRequested)
        {
            await publication.WaitAsync(cancellationToken);
            yield return ReadLatest(subscriptionRequest)
                ?? CreateWarmingSnapshot(subscriptionRequest);
        }
    }

    internal Task? CaptureSamplingWorkerTask()
    {
        lock (lifecycleGate)
        {
            return samplingTask;
        }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref disposed) != 0,
            this);
        lock (lifecycleGate)
        {
            ObjectDisposedException.ThrowIf(
                Volatile.Read(ref disposed) != 0,
                this);
            if (samplingStopInProgress)
            {
                throw new InvalidOperationException(
                    "The hardware sampling worker is still draining after stop.");
            }
            if (samplingTask is { IsCompleted: false })
            {
                return Task.CompletedTask;
            }

            if (samplingDrainTask is { IsCompleted: true })
            {
                samplingDrainTask = null;
            }

            if (samplingRunId is { } abandonedRun)
            {
                _ = publishedSnapshot.CloseHostedRun(
                    abandonedRun,
                    DateTimeOffset.UtcNow,
                    "hardware-owner-abandoned");
            }

            samplingCts?.Dispose();
            var cts = new CancellationTokenSource();
            var runId = publishedSnapshot.OpenHostedRun();
            FieldCollectionResult? completedFieldResult = null;
            samplingCts = cts;
            samplingRunId = runId;
            samplingTask = Task.Run(
                () => samplingWorker.RunAsync((plan, token) => TryRefreshCacheAsync(runId, plan.OwnerToken,
                    plan.Schedule, plan.Request!, plan.DueItemIds, plan.Triggers,
                    result => completedFieldResult = result, token), cts.Token,
                    planCreated: plan =>
                    {
                        completedFieldResult = null;
                        if (plan.Request is null)
                            fieldSubscriptionDelivery.Deliver(plan.Triggers, null, freshOnly: false);
                    },
                    collectionCompleted: (plan, _) =>
                    {
                        if (plan.Request is null)
                        {
                            fieldSubscriptionDelivery.Deliver(plan.Triggers, null, freshOnly: true);
                            return;
                        }
                        var result = completedFieldResult;
                        completedFieldResult = null;
                        fieldSubscriptionDelivery.Deliver(plan.Triggers, result);
                    }),
                CancellationToken.None);
        }
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        Task? drain;
        lock (lifecycleGate)
        {
            fieldSubscriptionDelivery.RetireAll();
            if (samplingRunId is { } activeRun)
            {
                _ = publishedSnapshot.CloseHostedRun(
                    activeRun,
                    DateTimeOffset.UtcNow,
                    "hardware-owner-stopped");
            }
            if (samplingTask is null)
            {
                samplingRunId = null;
                return;
            }

            samplingStopInProgress = true;
            samplingDrainTask ??= DrainSamplingWorkerAsync(
                samplingTask,
                samplingCts,
                samplingRunId);
            drain = samplingDrainTask;
            samplingCts?.Cancel();
        }

        await drain.WaitAsync(cancellationToken);
    }

    public void Dispose()
    {
        var firstDisposal = Interlocked.Exchange(ref disposed, 1) == 0;
        if (firstDisposal) runtimePlanProvider.Published -= OnRuntimePlanPublished;

        Task? drain = null;
        lock (lifecycleGate)
        {
            if (firstDisposal)
            {
                fieldSubscriptionDelivery.RetireAll();
                if (samplingRunId is { } activeRun)
                {
                    _ = publishedSnapshot.CloseHostedRun(
                        activeRun,
                        DateTimeOffset.UtcNow,
                        "hardware-owner-disposed");
                }
            }
            if (samplingTask is not null)
            {
                samplingStopInProgress = true;
                samplingDrainTask ??= DrainSamplingWorkerAsync(
                    samplingTask,
                    samplingCts,
                    samplingRunId);
                drain = samplingDrainTask;
                samplingCts?.Cancel();
            }
        }
        try
        {
            drain?.GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Hardware sampling worker failed during disposal.");
        }
        lock (lifecycleGate)
        {
            if (Volatile.Read(ref ownedResourcesDisposed) != 0) return;
            subscriptions.CloseOutputScratch();
            var cts = samplingCts;
            samplingCts = null;
            cts?.Dispose();
            demandSignal.Dispose();
            sampleGate.Dispose();
            Volatile.Write(ref ownedResourcesDisposed, 1);
        }
    }

    private async Task DrainSamplingWorkerAsync(
        Task task,
        CancellationTokenSource? cts,
        ulong? runId)
    {
        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Hardware sampling worker failed while draining after stop.");
        }
        finally
        {
            lock (lifecycleGate)
            {
                if (ReferenceEquals(samplingTask, task))
                {
                    samplingTask = null;
                    samplingCts = null;
                    if (samplingRunId == runId)
                    {
                        samplingRunId = null;
                    }
                }
                samplingStopInProgress = false;
                samplingDrainTask = null;
            }
            cts?.Dispose();
        }
    }

    private void SynchronizeCounterDemand(IReadOnlyList<string> activeItemIds)
    {
        // The worker also calls this with empty demand when its lifetime ends.
        if (dashboardMonitoringCatalog.Current is null || !metricSnapshotOwner.TryAcquire(out var lease))
        {
            capture.ReleasePlatformSensorDemand();
            return;
        }
        using (lease)
        {
            var workspace = lease!.Workspace;
            var catalog = workspace.ReadCatalog();
            var handles = NativeMetricSnapshotRequestProjection.CreateMetricHandles(MetricSampleRequest.ForIds(activeItemIds), catalog);
            SynchronizePlatformSensorDemand(handles, catalog);
            if (!catalog.RuleBindings.Values.Any(static rule => rule.MetricKind is
                (uint)NativeMetricSnapshotMetricKind.CounterRatio or (uint)NativeMetricSnapshotMetricKind.TimedCounterRate
                or (uint)NativeMetricSnapshotMetricKind.FloatingCounterRatio or (uint)NativeMetricSnapshotMetricKind.FloatingCounterPercentage
                or (uint)NativeMetricSnapshotMetricKind.CounterFrequency)) return;
            workspace.SynchronizeCounterDemand(handles, DateTimeOffset.UtcNow);
        }
    }

    internal void SynchronizePlatformSensorDemand(IReadOnlyCollection<ulong> activeHandles, NativeMetricSnapshotCatalogProjection catalog)
        => capture.SynchronizePlatformSensorDemand(activeHandles, catalog);
    private async Task<SubscriptionSampleCompletion> TryRefreshCacheAsync(
        ulong runId,
        SamplingOwnerToken workspaceOwnerToken,
        NativeItemSamplingSubscriptionScheduleReceipt schedule,
        MetricSampleRequest request,
        IReadOnlyList<string> dueDatasetIds,
        ImmutableArray<NativeItemSamplingSubscriptionTrigger<MetricSampleRequest>> triggers,
        Action<FieldCollectionResult> completeFieldResult,
        CancellationToken cancellationToken)
    {
        if (!await sampleGate.WaitAsync(0, cancellationToken))
        {
            return SubscriptionSampleCompletion.Skipped;
        }

        HardwareMetricHostedPublicationTicket? ticket = null;
        var queriedAddresses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var publicationDatasetIds = request.IncludesCatalog
            ? dueDatasetIds.Concat([SamplingDatasetIds.StorageInventory, SamplingDatasetIds.FanInventory, SamplingDatasetIds.BatteryInventory]).ToArray()
            : dueDatasetIds;
        try
        {
            if (!publishedSnapshot.TryCaptureHostedTicket(
                    runId,
                    workspaceOwnerToken,
                    schedule,
                    out var capturedTicket))
            {
                return SubscriptionSampleCompletion.Skipped;
            }
            ticket = capturedTicket;
            var snapshot = await ReadSnapshotCoreAsync(
                request,
                cancellationToken,
                sourceQueriesCompleted: queriedAddresses.UnionWith);
            if (!publishedSnapshot.TryPublishHosted(
                    capturedTicket,
                    snapshot,
                    request,
                    publicationDatasetIds,
                    readyUntil: null,
                    out var publication))
            {
                completeFieldResult(fieldSubscriptionDelivery.CaptureCompleted(
                    triggers, queriedAddresses, committed: false));
                return SubscriptionSampleCompletion.Skipped;
            }
            completeFieldResult(fieldSubscriptionDelivery.CaptureCompleted(
                triggers, queriedAddresses, publication.IsSuccessful));
            var (newlyRejected, recovered) = TrackRejectedDatasets(rejectedDatasetIds, publication);
            if (newlyRejected.Count != 0)
            {
                logger.LogWarning(
                    "Hardware sampling started rejecting {RejectedCount} datasets: {RejectedDatasets}. " +
                    "Repeated rejections are not logged again until the dataset publishes successfully.",
                    newlyRejected.Count,
                    string.Join(", ", newlyRejected));
            }
            if (recovered.Count != 0)
            {
                logger.LogInformation(
                    "Hardware sampling published previously rejected datasets again: {RecoveredDatasets}.",
                    string.Join(", ", recovered));
            }
            if (request.IncludesCatalog)
            {
                dashboardMonitoringCatalog.PublishCatalogProbe(publishedSnapshot.Read()!);
            }
            PublishCompletedDatasets(dueDatasetIds);
            return SubscriptionSampleCompletion.Sampled;
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            return SubscriptionSampleCompletion.Canceled;
        }
        catch (MetricSnapshotWorkspaceRetiredException)
        {
            return SubscriptionSampleCompletion.Skipped;
        }
        catch (Exception ex)
        {
            if (ticket is not { } failureTicket
                || !publishedSnapshot.TryRecordHostedFailure(
                    failureTicket,
                    request,
                    publicationDatasetIds,
                    DateTimeOffset.UtcNow,
                    $"hardware-sample-{ex.GetType().Name}"))
            {
                return SubscriptionSampleCompletion.Skipped;
            }
            completeFieldResult(fieldSubscriptionDelivery.CaptureCompleted(
                triggers, queriedAddresses, committed: false));
            logger.LogWarning(
                ex,
                "Native hardware metric background sample failed.");
            if (request.IncludesCatalog) dashboardMonitoringCatalog.PublishCatalogProbe(publishedSnapshot.Read()!);
            PublishCompletedDatasets(dueDatasetIds);
            return SubscriptionSampleCompletion.Sampled;
        }
        finally
        {
            sampleGate.Release();
        }
    }

    // 同一 dataset 持续被拒绝只在进入拒绝状态时报告一次；重新发布成功时报告恢复，之后再被拒绝会再次报告。
    // 本次发布没有涉及的 dataset 保持原状态。
    internal static (IReadOnlyList<string> NewlyRejected, IReadOnlyList<string> Recovered) TrackRejectedDatasets(
        ISet<string> rejectedDatasetIds,
        HardwareMetricPublicationResult publication)
    {
        var newlyRejected = new List<string>();
        foreach (var datasetId in publication.RejectedDatasetIds)
        {
            if (rejectedDatasetIds.Add(datasetId)) newlyRejected.Add(datasetId);
        }
        var recovered = new List<string>();
        foreach (var datasetId in publication.CommittedDatasetIds)
        {
            if (rejectedDatasetIds.Remove(datasetId)) recovered.Add(datasetId);
        }
        return (newlyRejected, recovered);
    }

    private void ReportSamplingWorkerFailure(SubscriptionWorkerFailure failure)
    {
        switch (failure.Stage)
        {
            case SubscriptionWorkerFailureStage.Planning:
                logger.LogError(failure.Error, "Hardware sampling could not create a subscription plan; the owner will retry.");
                break;
            case SubscriptionWorkerFailureStage.Demand:
                logger.LogError(failure.Error, "Hardware counter demand synchronization failed.");
                break;
            case SubscriptionWorkerFailureStage.Collection:
                logger.LogError(failure.Error, "Hardware sampling plan failed; the owner remains active and will retry.");
                break;
            case SubscriptionWorkerFailureStage.InterruptedSettlement:
                logger.LogError(failure.Error, "Hardware sampling cancellation could not settle the pending plan.");
                break;
            case SubscriptionWorkerFailureStage.FaultedSettlement:
                logger.LogError(failure.Error, "Hardware sampling fault could not release the pending plan after {ErrorType}.",
                    failure.OriginalError?.GetType().Name);
                break;
            case SubscriptionWorkerFailureStage.Cleanup:
                logger.LogError(failure.Error, "Hardware counter baseline cleanup failed after sampling stopped.");
                break;
        }
    }

    internal static void SignalDemand(SemaphoreSlim signal)
        => NativeSamplingSubscriptionWorker<MetricSampleRequest, SamplingOwnerToken>.SignalDemand(signal);

    private void ReleaseObservationLease(string sourceKey)
    {
        lock (lifecycleGate)
        {
            if (Volatile.Read(ref disposed) != 0) return;
            subscriptions.Remove(sourceKey, DateTimeOffset.UtcNow);
        }
        SignalDemand(demandSignal);
    }

    private IEnumerable<string> GetSubscriptionItemIds(
        MetricSampleRequest request)
    {
        if (request.IsEmpty)
        {
            return [];
        }

        if (request.IsAll)
        {
            return SamplingDatasetIds.ResolveAllSystemDatasets(
                dashboardMonitoringCatalog.Current?.Items.Keys ?? [])
                .Append(SamplingDatasetIds.SystemMetricCatalog);
        }

        var result = request.Ids
            .Select(SamplingDatasetIds.ForSystemMetric)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (request.IncludesCatalog) result.Add(SamplingDatasetIds.SystemMetricCatalog);
        if (request.IncludesAllGpuCoreMetrics)
        {
            result.UnionWith(
                (dashboardMonitoringCatalog.Current?.Items.Keys ?? [])
                .Where(IsGpuCoreMetric));
        }
        if (result.Any(itemId => IsGpuMetric(itemId) || IsManagedGpuMetric(itemId)))
        {
            result.Add(SamplingDatasetIds.SystemGpuInventory);
        }
        return result;
    }

    private bool IsManagedGpuMetric(string itemId)
    {
        var snapshot = dashboardMonitoringCatalog.Current;
        return snapshot is not null && snapshot.FieldBindings.TryGetValue(itemId, out var binding)
            && binding.Field.Resource is MetricResource.Gpu or MetricResource.Fan
            && WindowsMetricControlBindings.TryFind(binding)?.Family is WindowsMetricControlFamily.AmdDesktopGpu
                or WindowsMetricControlFamily.IntelIntegratedGpu or WindowsMetricControlFamily.AmdIntegratedGpu;
    }
    internal IEnumerable<string> GetPublicationItemIds(
        MetricSampleRequest request)
        => request.IsAll
            ? GetSubscriptionItemIds(request)
            : GetSubscriptionItemIds(request).Where(static itemId =>
                !itemId.Equals(
                    SamplingDatasetIds.SystemGpuInventory,
                    StringComparison.OrdinalIgnoreCase));

    internal void PublishCompletedDatasets(IEnumerable<string> datasetIds)
    {
        var snapshot = ReadCachedSnapshot();
        var metadataChanged = snapshot is not null && dashboardMonitoringCatalog.PublishReadingMetadata(snapshot);
        publicationSignal.Publish(MetricDatasetIds.ExpandBoundSystemDatasets(
            metadataChanged ? datasetIds.Append(MetricDatasetIds.SystemMetricCatalog) : datasetIds,
            snapshot?.FieldBindings.Values ?? []));
    }

    private MetricSampleRequest? CreateMetricSampleRequest(
        IReadOnlyList<string> itemIds,
        IReadOnlyList<
            NativeItemSamplingSubscriptionSourceView<MetricSampleRequest>> sources)
    {
        var dueItems = itemIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var relevantSources = sources
            .Where(source => source.ItemIds.Any(dueItems.Contains))
            .ToArray();
        var metricIds = dueItems
            .Where(static itemId => !itemId.Equals(SamplingDatasetIds.SystemMetricCatalog, StringComparison.OrdinalIgnoreCase)
                && !itemId.Equals(
                SamplingDatasetIds.SystemGpuInventory,
                StringComparison.OrdinalIgnoreCase))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var dueGpuInventory = dueItems.Contains(
            SamplingDatasetIds.SystemGpuInventory);
        if (dueGpuInventory)
        {
            metricIds.UnionWith(relevantSources
                .SelectMany(static source => source.Request.Ids)
                .Where(itemId => IsGpuMetric(itemId) || IsManagedGpuMetric(itemId)));
            if (!metricIds.Any(IsGpuMetric))
            {
                var inventoryProbeMetric =
                    (dashboardMonitoringCatalog.Current?.Items.Keys ?? [])
                    .FirstOrDefault(IsGpuMetric);
                if (inventoryProbeMetric is not null)
                {
                    metricIds.Add(inventoryProbeMetric);
                }
            }
        }
        var dueCatalog = dueItems.Contains(SamplingDatasetIds.SystemMetricCatalog);
        if (metricIds.Count == 0 && !dueCatalog)
        {
            return null;
        }
        var request = MetricSampleRequest.ForIds(metricIds);
        return dueCatalog ? request.WithCatalog() : request;
    }

    private HardwareMetricSnapshot? ReadCachedSnapshot()
        => publishedSnapshot.Read();

    private HardwareMetricSnapshot CreateWarmingSnapshot(
        MetricSampleRequest request)
    {
        var datasetIds = request.IsAll
            ? SamplingDatasetIds.ResolveAllSystemDatasets(
                dashboardMonitoringCatalog.Current?.Items.Keys ?? [])
                .ToHashSet(StringComparer.OrdinalIgnoreCase)
            : request.Ids
                .Select(SamplingDatasetIds.ForSystemMetric)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (request.IncludesCatalog) datasetIds.Add(SamplingDatasetIds.SystemMetricCatalog);
        if (request.IncludesAllGpuCoreMetrics
            || RequestIncludesResource(request, MetricResource.Gpu))
        {
            datasetIds.Add(SamplingDatasetIds.SystemGpuInventory);
        }

        var datasets = datasetIds.ToDictionary(
            static datasetId => datasetId,
            static datasetId => new HardwareMetricDatasetObservation(
                datasetId,
                SamplingObservationStatus.Warming,
                0,
                0,
                0,
                0,
                0,
                0),
            StringComparer.OrdinalIgnoreCase);
        var cpuRequested = RequestIncludesResource(request, MetricResource.Cpu);
        var memoryRequested = RequestIncludesResource(request, MetricResource.Memory);
        var pageFileRequested = request.IsAll
            || request.IncludesPrefix("virtualMemory.");
        var gpuRequested = request.IsAll
            || request.IncludesAllGpuCoreMetrics
            || RequestIncludesResource(request, MetricResource.Gpu);
        return new HardwareMetricSnapshot(
            DateTimeOffset.UnixEpoch,
            new CpuMetrics(
                string.Empty,
                0,
                false,
                cpuRequested
                    ? CpuMetricObservationStatus.Warming
                    : CpuMetricObservationStatus.NotRequested,
                0,
                0,
                0,
                0,
                0,
                string.Empty,
                new CpuSensorMetrics(
                    new HardwareSensorProviderState(
                        "Host Manager metric snapshot",
                        "Warming",
                        null),
                    null,
                    null,
                    null,
                    null)),
            new MemoryMetrics(0, 0, 0, false, string.Empty)
            {
                ObservationStatus = memoryRequested
                    ? SamplingObservationStatus.Warming
                    : SamplingObservationStatus.NotRequested
            },
            new VirtualMemoryMetrics(0, 0, 0, string.Empty, false)
            {
                ObservationStatus = pageFileRequested
                    ? SamplingObservationStatus.Warming
                    : SamplingObservationStatus.NotRequested
            },
            [],
            new SchedulingGpuInventorySnapshot(
                gpuRequested
                    ? SamplingObservationStatus.Warming
                    : SamplingObservationStatus.NotRequested,
                0,
                0,
                0,
                0,
                0,
                0,
                []),
            new Dictionary<string, MetricValue>(
                StringComparer.OrdinalIgnoreCase))
        {
            Datasets = datasets,
            FieldBindings = dashboardMonitoringCatalog.Current?.FieldBindings
                ?? new Dictionary<string, MetricDeviceFieldBinding>()
        };
    }

    private static HardwareMetricSnapshot ProjectSnapshot(
        HardwareMetricSnapshot snapshot,
        MetricSampleRequest request)
    {
        if (request.IsAll || request.IsCatalogProbe)
        {
            return snapshot;
        }

        var items = snapshot.Items
            .Where(pair => request.Includes(pair.Key))
            .ToDictionary(
                static pair => pair.Key,
                static pair => pair.Value,
                StringComparer.OrdinalIgnoreCase);
        var requestedDatasets = SamplingDatasetIds.ResolveSystemDatasets(
            request,
            snapshot.Datasets.Keys);
        var datasets = requestedDatasets.ToDictionary(
            static datasetId => datasetId,
            datasetId => snapshot.Datasets.TryGetValue(
                    datasetId,
                    out var observation)
                ? observation
                : new HardwareMetricDatasetObservation(
                    datasetId,
                    SamplingObservationStatus.Warming,
                    0,
                    0,
                    0,
                    0,
                    0,
                    0),
            StringComparer.OrdinalIgnoreCase);
        return snapshot with
        {
            Items = items,
            Datasets = datasets
        };
    }

    private bool IsGpuMetric(string metricId)
        => dashboardMonitoringCatalog.Current?.FieldBindings.TryGetValue(metricId, out var binding) == true
            ? binding.Field.Resource == MetricResource.Gpu
            : metricId.StartsWith("gpu.", StringComparison.OrdinalIgnoreCase);

    private bool RequestIncludesResource(MetricSampleRequest request, MetricResource resource)
        => request.IsAll || request.Ids.Any(id =>
            dashboardMonitoringCatalog.Current?.FieldBindings.TryGetValue(id, out var binding) == true
                ? binding.Field.Resource == resource
                : resource switch
                {
                    MetricResource.Gpu => IsGpuMetric(id),
                    MetricResource.Cpu => id.StartsWith("cpu.", StringComparison.OrdinalIgnoreCase),
                    MetricResource.Memory => id.StartsWith("memory.", StringComparison.OrdinalIgnoreCase)
                        || id.StartsWith("virtualMemory.", StringComparison.OrdinalIgnoreCase),
                    _ => false
                });

    private bool IsGpuCoreMetric(string metricId)
    {
        if (dashboardMonitoringCatalog.Current?.FieldBindings.TryGetValue(metricId, out var binding) == true)
            return binding.Field.Code is MetricFieldCode.GpuUsage or MetricFieldCode.GpuMemoryUsed
                or MetricFieldCode.GpuMemoryPercent or MetricFieldCode.GpuEngineBusy;
        if (!IsGpuMetric(metricId))
        {
            return false;
        }
        var separator = metricId.IndexOf('.', "gpu.".Length);
        if (separator <= "gpu.".Length)
        {
            return false;
        }
        var name = metricId[(separator + 1)..];
        return name.Equals("usage", StringComparison.OrdinalIgnoreCase)
            || name.Equals("vram", StringComparison.OrdinalIgnoreCase)
            || name.Equals("vramPercent", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<HardwareMetricSnapshot> ReadSnapshotCoreAsync(MetricSampleRequest request,
        CancellationToken cancellationToken, Action<HardwareMetricSnapshot>? onCommitted = null,
        Action<ImmutableHashSet<string>>? sourceQueriesCompleted = null)
    {
        if (request.IncludesCatalog) metricSnapshotOwner.RequestTopologyRefresh();
        _ = metricSnapshotOwner.RefreshTopologyIfRequested();
        using var lease = await metricSnapshotOwner.AcquireAsync(cancellationToken);
        var result = await capture.CaptureUnderGateAsync(request, lease.Workspace, lease.WorkspaceIdentity, lease.Topology,
            commit => lease.TryPublish(_ => { commit(); return true; }, out _),
            new(HostMetricSnapshotCopy.VirtualMemoryDetail, HostMetricSnapshotCopy.LegacyLabel), cancellationToken,
            workspace => { _ = workspace.PersistRecoveryCheckpointIfAdvanced(DateTimeOffset.UtcNow); }, onCommitted,
            enrichBeforeCommit: async (snapshot, selection, token, sourceQueryStarted) =>
            {
                if (selection.IsCatalogProbe) snapshot = controlFields.Discover(snapshot);
                return selection.IsCatalogProbe ? snapshot
                    : await controlFields.ReadSelectedAsync(snapshot, selection, token, sourceQueryStarted);
            },
            sourceQueriesCompleted: sourceQueriesCompleted,
            fieldCatalog: dashboardMonitoringCatalog.Current);
        return result ?? throw new MetricSnapshotWorkspaceRetiredException();
    }
    internal static HardwareMetricSnapshot AddCatalogObservations(HardwareMetricSnapshot snapshot, NativeMetricSnapshotCatalogTopologySnapshot? topology, DateTimeOffset observedAt)
        => WindowsHardwareMetricCapture.AddCatalogObservations(snapshot, topology, observedAt);

    private sealed class ObservationSubscriptionLease(
        WindowsHardwareMetricSampler owner,
        string sourceKey) : IDisposable
    {
        private int state;

        public void Dispose()
        {
            if (Interlocked.CompareExchange(ref state, 1, 0) != 0)
            {
                return;
            }

            try
            {
                owner.ReleaseObservationLease(sourceKey);
                Volatile.Write(ref state, 2);
            }
            catch
            {
                Volatile.Write(ref state, 0);
                throw;
            }
        }
    }

    private sealed class EmptyObservationSubscription : IDisposable
    {
        internal static EmptyObservationSubscription Instance { get; } = new();

        public void Dispose()
        {
        }
    }

    private sealed class MetricSnapshotWorkspaceRetiredException()
        : Exception("The metric-snapshot workspace retired before publication.");
}







