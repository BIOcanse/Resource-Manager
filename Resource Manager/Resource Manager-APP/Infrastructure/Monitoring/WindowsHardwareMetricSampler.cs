using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ResourceManager.App.Application.Metrics;
using ResourceManager.App.Application.Monitoring;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.Monitoring;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.Monitoring.AmdSmu;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;
using System.Runtime.CompilerServices;

namespace ResourceManager.App.Infrastructure.Monitoring;

public sealed partial class WindowsHardwareMetricSampler :
    IMetricSampler,
    IMetricSnapshotObservationSource,
    IMetricSnapshotPushSource,
    IHostedService,
    IDisposable
{
    private static readonly TimeSpan CatalogProbeCacheDuration =
        TimeSpan.FromSeconds(2);

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
    private readonly WindowsPlatformSensorReader platformSensorReader;
    private readonly HostManagerMetricSnapshotOwner metricSnapshotOwner;
    private readonly DashboardMonitoringCatalogState dashboardMonitoringCatalog;
    private readonly NativeMetricSnapshotSourceRuntimeFacts sourceRuntimeFacts;
    private readonly ILogger<WindowsHardwareMetricSampler> logger;
    private readonly RuntimePlanProvider runtimePlanProvider;
    private readonly object lifecycleGate = new();
    private readonly SemaphoreSlim demandSignal = new(0, 1);
    private readonly SemaphoreSlim sampleGate = new(1, 1);
    private readonly SamplingPublicationSignal publicationSignal = new();
    private readonly LastSuccessfulHardwareMetricSnapshot publishedSnapshot =
        new();
    private readonly LastSuccessfulHardwareMetricSnapshot catalogProbeSnapshot =
        new();
    private readonly NativeItemSamplingSubscriptionTracker<MetricSampleRequest>
        subscriptions;
    private CancellationTokenSource? samplingCts;
    private Task? samplingTask;
    private Task? samplingDrainTask;
    private ulong? samplingRunId;
    private bool samplingStopInProgress;
    private long catalogProbeCompletedUtcTicks;
    private long nextObservationLeaseId;
    private int disposed;

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
        WindowsPlatformSensorReader platformSensorReader,
        HostManagerMetricSnapshotOwner metricSnapshotOwner,
        DashboardMonitoringCatalogState dashboardMonitoringCatalog,
        IMonitoringSourceZoneRegistry monitoringSourceZoneRegistry,
        HostManagerSamplingSubscriptionOwner samplingSubscriptionOwner,
        RuntimePlanProvider runtimePlanProvider,
        ILogger<WindowsHardwareMetricSampler> logger)
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
        this.platformSensorReader = platformSensorReader;
        this.metricSnapshotOwner = metricSnapshotOwner;
        this.dashboardMonitoringCatalog = dashboardMonitoringCatalog;
        sourceRuntimeFacts = new NativeMetricSnapshotSourceRuntimeFacts(
            monitoringSourceZoneRegistry);
        subscriptions =
            new NativeItemSamplingSubscriptionTracker<MetricSampleRequest>(
                 samplingSubscriptionOwner,
                 1,
                 static request => request.CacheKey,
                 GetSubscriptionItemIds,
                 CreateMetricSampleRequest,
                 coalesceItemsIntoLatestCapture: false);
        this.logger = logger;
        this.runtimePlanProvider = runtimePlanProvider;
        runtimePlanProvider.Published += OnRuntimePlanPublished;
        OnRuntimePlanPublished(runtimePlanProvider.Current);
    }

    private void OnRuntimePlanPublished(CompiledRuntimePlan plan)
    {
        _ = plan;
        using var publication = runtimePlanProvider.AcquirePublicationLease();
        publishedSnapshot.ConfigureHistory(publication.Plan.HostManager.DataHistory);
    }

    public Task<HardwareMetricSnapshot> GetSnapshotAsync(
        CancellationToken cancellationToken)
        => GetSnapshotAsync(MetricSampleRequest.All, cancellationToken);

    public async Task<HardwareMetricSnapshot> GetSnapshotAsync(
        MetricSampleRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.IsCatalogProbe)
        {
            return await RefreshCatalogCacheIfStaleAsync(cancellationToken);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = ReadCachedSnapshot();
        return snapshot is null
            ? CreateWarmingSnapshot(request)
            : ProjectSnapshot(snapshot, request);
    }

    public async Task<HardwareMetricSnapshot> CaptureSnapshotAsync(
        MetricSampleRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await sampleGate.WaitAsync(cancellationToken);
        try
        {
            var snapshot = await ReadSnapshotCoreAsync(
                request,
                cancellationToken);
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
        subscriptions.TrackPersistent(
            sourceKey,
            subscriptionRequest,
            DateTimeOffset.UtcNow,
            refreshInterval);
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
            samplingCts = cts;
            samplingRunId = runId;
            samplingTask = Task.Run(
                () => RunSamplingLoopAsync(runId, cts.Token),
                CancellationToken.None);
        }
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        Task? drain;
        lock (lifecycleGate)
        {
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
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        runtimePlanProvider.Published -= OnRuntimePlanPublished;

        Task? drain = null;
        lock (lifecycleGate)
        {
            if (samplingRunId is { } activeRun)
            {
                _ = publishedSnapshot.CloseHostedRun(
                    activeRun,
                    DateTimeOffset.UtcNow,
                    "hardware-owner-disposed");
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
        demandSignal.Dispose();
        sampleGate.Dispose();
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

    private async Task<HardwareMetricSnapshot>
        RefreshCatalogCacheIfStaleAsync(
            CancellationToken cancellationToken)
    {
        var nowTicks = DateTimeOffset.UtcNow.UtcTicks;
        var cached = catalogProbeSnapshot.Read();
        var completedTicks =
            Volatile.Read(ref catalogProbeCompletedUtcTicks);
        if (cached is not null
            && nowTicks - completedTicks
                < CatalogProbeCacheDuration.Ticks)
        {
            return cached;
        }

        await sampleGate.WaitAsync(cancellationToken);
        try
        {
            nowTicks = DateTimeOffset.UtcNow.UtcTicks;
            cached = catalogProbeSnapshot.Read();
            completedTicks =
                Volatile.Read(ref catalogProbeCompletedUtcTicks);
            if (cached is not null
                && nowTicks - completedTicks
                    < CatalogProbeCacheDuration.Ticks)
            {
                return cached;
            }

            var snapshot = await ReadSnapshotCoreAsync(
                MetricSampleRequest.CatalogProbe,
                cancellationToken);
            catalogProbeSnapshot.Publish(snapshot);
            dashboardMonitoringCatalog.PublishCatalogProbe(snapshot);
            Volatile.Write(
                ref catalogProbeCompletedUtcTicks,
                DateTimeOffset.UtcNow.UtcTicks);
            return snapshot;
        }
        finally
        {
            sampleGate.Release();
        }
    }

    private async Task RunSamplingLoopAsync(
        ulong runId,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            NativeItemSamplingSubscriptionPlan<MetricSampleRequest> plan;
            try
            {
                plan = CreateSamplingPlan(DateTimeOffset.UtcNow);
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(
                    ex,
                    "Hardware sampling could not create a subscription plan; the owner will retry.");
                await DelayAfterWorkerFailureAsync(cancellationToken);
                continue;
            }

            if (!plan.IsActive)
            {
                try
                {
                    await demandSignal.WaitAsync(cancellationToken);
                }
                catch (OperationCanceledException)
                    when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                continue;
            }
            if (plan.Request is null)
            {
                try
                {
                    await WaitForDemandOrDelayAsync(
                        plan.Delay,
                        cancellationToken);
                }
                catch (OperationCanceledException)
                    when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                continue;
            }

            var settlementAttempted = false;
            try
            {
                var sampled = await TryRefreshCacheAsync(
                    runId,
                    plan.OwnerToken,
                    plan.Schedule,
                    plan.Request,
                    plan.DueItemIds,
                    cancellationToken);
                settlementAttempted = true;
                var completion = sampled switch
                {
                    HardwareSampleCompletion.Sampled => subscriptions.MarkSampled(
                        plan.Request,
                        DateTimeOffset.UtcNow),
                    HardwareSampleCompletion.SkippedBusy or
                    HardwareSampleCompletion.Canceled => subscriptions.MarkSkipped(
                        plan.Request,
                        DateTimeOffset.UtcNow),
                    _ => throw new InvalidOperationException(
                        "Hardware sampling returned an unknown completion.")
                };
                await WaitForDemandOrDelayAsync(
                    completion.Delay,
                    cancellationToken);
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
                if (!settlementAttempted)
                {
                    settlementAttempted = true;
                    TrySettleInterruptedPlan(plan.Request);
                }
                return;
            }
            catch (Exception ex)
            {
                if (!settlementAttempted)
                {
                    settlementAttempted = true;
                    TrySettleFaultedPlan(plan.Request, ex);
                }
                logger.LogError(
                    ex,
                    "Hardware sampling plan failed; the owner remains active and will retry.");
                await DelayAfterWorkerFailureAsync(cancellationToken);
            }
        }
    }

    private void TrySettleInterruptedPlan(MetricSampleRequest request)
    {
        try
        {
            _ = subscriptions.MarkSkipped(request, DateTimeOffset.UtcNow);
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Hardware sampling cancellation could not settle the pending plan.");
        }
    }

    private void TrySettleFaultedPlan(
        MetricSampleRequest request,
        Exception originalError)
    {
        try
        {
            _ = subscriptions.MarkSkipped(request, DateTimeOffset.UtcNow);
        }
        catch (Exception settlementError)
        {
            logger.LogError(
                settlementError,
                "Hardware sampling fault could not release the pending plan after {ErrorType}.",
                originalError.GetType().Name);
        }
    }

    private static async Task DelayAfterWorkerFailureAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task WaitForDemandOrDelayAsync(
        TimeSpan delay,
        CancellationToken cancellationToken)
    {
        if (delay <= TimeSpan.Zero)
        {
            return;
        }

        using var delayCts =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
        var demandTask = demandSignal.WaitAsync(delayCts.Token);
        var delayTask = Task.Delay(delay, delayCts.Token);
        await Task.WhenAny(demandTask, delayTask);
        await delayCts.CancelAsync();
        try
        {
            await Task.WhenAll(demandTask, delayTask);
        }
        catch (OperationCanceledException)
            when (delayCts.IsCancellationRequested)
        {
        }
    }

    private async Task<HardwareSampleCompletion> TryRefreshCacheAsync(
        ulong runId,
        SamplingOwnerToken workspaceOwnerToken,
        NativeItemSamplingSubscriptionScheduleReceipt schedule,
        MetricSampleRequest request,
        IReadOnlyList<string> dueDatasetIds,
        CancellationToken cancellationToken)
    {
        if (!await sampleGate.WaitAsync(0, cancellationToken))
        {
            return HardwareSampleCompletion.SkippedBusy;
        }

        HardwareMetricHostedPublicationTicket? ticket = null;
        try
        {
            if (!publishedSnapshot.TryCaptureHostedTicket(
                    runId,
                    workspaceOwnerToken,
                    schedule,
                    out var capturedTicket))
            {
                return HardwareSampleCompletion.SkippedBusy;
            }
            ticket = capturedTicket;
            var snapshot = await ReadSnapshotCoreAsync(
                request,
                cancellationToken);
            if (!publishedSnapshot.TryPublishHosted(
                    capturedTicket,
                    snapshot,
                    request,
                    dueDatasetIds,
                    readyUntil: null,
                    out var publication))
            {
                return HardwareSampleCompletion.SkippedBusy;
            }
            if (!publication.IsSuccessful)
            {
                logger.LogWarning(
                    "Hardware sampling committed {CommittedCount} datasets but rejected {RejectedCount}: {RejectedDatasets}.",
                    publication.CommittedDatasetCount,
                    publication.RejectedDatasetCount,
                    string.Join(", ", publication.RejectedDatasetIds));
            }
            publicationSignal.Publish(dueDatasetIds);
            return HardwareSampleCompletion.Sampled;
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            return HardwareSampleCompletion.Canceled;
        }
        catch (MetricSnapshotWorkspaceRetiredException)
        {
            return HardwareSampleCompletion.SkippedBusy;
        }
        catch (Exception ex)
        {
            if (ticket is not { } failureTicket
                || !publishedSnapshot.TryRecordHostedFailure(
                    failureTicket,
                    request,
                    dueDatasetIds,
                    DateTimeOffset.UtcNow,
                    $"hardware-sample-{ex.GetType().Name}"))
            {
                return HardwareSampleCompletion.SkippedBusy;
            }
            logger.LogWarning(
                ex,
                "Native hardware metric background sample failed.");
            publicationSignal.Publish(dueDatasetIds);
            return HardwareSampleCompletion.Sampled;
        }
        finally
        {
            sampleGate.Release();
        }
    }

    internal static void SignalDemand(SemaphoreSlim signal)
    {
        ArgumentNullException.ThrowIfNull(signal);
        try
        {
            signal.Release();
        }
        catch (SemaphoreFullException)
        {
            // One pending wake represents all demand observed before the next plan.
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private NativeItemSamplingSubscriptionPlan<MetricSampleRequest>
        CreateSamplingPlan(DateTimeOffset now)
        => subscriptions.CreatePlan(now);

    private void ReleaseObservationLease(string sourceKey)
    {
        subscriptions.Remove(sourceKey, DateTimeOffset.UtcNow);
        SignalDemand(demandSignal);
    }

    private IEnumerable<string> GetSubscriptionItemIds(
        MetricSampleRequest request)
    {
        if (request.IsCatalogProbe || request.IsEmpty)
        {
            return [];
        }

        if (request.IsAll)
        {
            return SamplingDatasetIds.ResolveAllSystemDatasets(
                dashboardMonitoringCatalog.Current?.Items.Keys ?? []);
        }

        var result = request.Ids
            .Select(SamplingDatasetIds.ForSystemMetric)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (request.IncludesAllGpuCoreMetrics)
        {
            result.UnionWith(
                (dashboardMonitoringCatalog.Current?.Items.Keys ?? [])
                .Where(IsGpuCoreMetric));
        }
        if (result.Any(IsGpuMetric))
        {
            result.Add(SamplingDatasetIds.SystemGpuInventory);
        }
        return result;
    }

    internal IEnumerable<string> GetPublicationItemIds(
        MetricSampleRequest request)
        => request.IsAll
            ? GetSubscriptionItemIds(request)
            : GetSubscriptionItemIds(request).Where(static itemId =>
                !itemId.Equals(
                    SamplingDatasetIds.SystemGpuInventory,
                    StringComparison.OrdinalIgnoreCase));

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
            .Where(static itemId => !itemId.Equals(
                SamplingDatasetIds.SystemGpuInventory,
                StringComparison.OrdinalIgnoreCase))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var dueGpuInventory = dueItems.Contains(
            SamplingDatasetIds.SystemGpuInventory);
        if (dueGpuInventory)
        {
            metricIds.UnionWith(relevantSources
                .SelectMany(static source => source.Request.Ids)
                .Where(IsGpuMetric));
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
        if (metricIds.Count == 0)
        {
            return null;
        }
        return MetricSampleRequest.ForIds(metricIds);
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
        if (request.IncludesAllGpuCoreMetrics
            || request.IncludesPrefix("gpu."))
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
        var cpuRequested = request.IsAll || request.IncludesPrefix("cpu.");
        var memoryRequested = request.IsAll || request.IncludesPrefix("memory.");
        var pageFileRequested = request.IsAll
            || request.IncludesPrefix("virtualMemory.");
        var gpuRequested = request.IsAll
            || request.IncludesAllGpuCoreMetrics
            || request.IncludesPrefix("gpu.");
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
            Datasets = datasets
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

    private static bool IsGpuMetric(string metricId)
        => metricId.StartsWith("gpu.", StringComparison.OrdinalIgnoreCase);

    private static bool IsGpuCoreMetric(string metricId)
    {
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

    private async Task<HardwareMetricSnapshot> ReadSnapshotCoreAsync(
        MetricSampleRequest request,
        CancellationToken cancellationToken)
    {
        metricSnapshotOwner.RefreshTopologyIfRequested();
        using var lease =
            await metricSnapshotOwner.AcquireAsync(cancellationToken);
        var workspace = lease.Workspace;
        var catalog = workspace.ReadCatalog();
        var commandAt = DateTimeOffset.UtcNow;
        var requestedMetricHandles =
            NativeMetricSnapshotRequestProjection.CreateMetricHandles(
                request,
                catalog);
        if (requestedMetricHandles.Length != 0)
        {
            var sourceModes =
                sourceRuntimeFacts.CaptureModes(catalog, commandAt);
            var includeGpuInventory = request.IsAll
                || request.IsCatalogProbe
                || request.IncludesPrefix("gpu.");
            var plan = workspace.PlanCollection(
                requestedMetricHandles,
                sourceModes,
                includeGpuInventory,
                commandAt);
            foreach (var sourcePlan in plan.Sources)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var completion = await CollectNativeSourceAsync(
                    plan,
                    sourcePlan,
                    cancellationToken);
                _ = workspace.CompleteSource(
                    plan,
                    completion,
                    DateTimeOffset.UtcNow);
            }
        }

        if (lease.TryPublish(
                currentWorkspace =>
                {
                    _ = currentWorkspace.PersistRecoveryCheckpointIfAdvanced(
                        DateTimeOffset.UtcNow);
                    var committed = currentWorkspace.ReadCommitted();
                    return NativeMetricSnapshotCommittedProjection.Create(
                        committed,
                        cpuZone.CpuName,
                        memoryZone.HardwareDescription,
                        lease.WorkspaceIdentity);
                },
                out var published))
        {
            return published;
        }

        throw new MetricSnapshotWorkspaceRetiredException();
    }

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

internal enum HardwareSampleCompletion
{
    Sampled,
    SkippedBusy,
    Canceled
}
