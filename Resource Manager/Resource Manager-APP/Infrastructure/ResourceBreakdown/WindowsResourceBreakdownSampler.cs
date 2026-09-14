using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Extensions.Hosting;
using ResourceManager.App.Application.Metrics;
using ResourceManager.App.Application.RuntimeSpecialization;
using ResourceManager.App.Application.ProcessAttribution;
using ResourceManager.App.Application.ResourceBreakdown;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.Monitoring;
using ResourceManager.App.Domain.ProcessAttribution;
using ResourceManager.App.Domain.ResourceBreakdown;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.App.Infrastructure.ProcessIdentity;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;
using System.Runtime.CompilerServices;
using ResourceManager.App.Infrastructure.Windows;
using ResourceManager.App.Infrastructure.Telemetry.Etw;
using ResourceManager.App.Infrastructure.NativeCore;

namespace ResourceManager.App.Infrastructure.ResourceBreakdown;

internal enum ResourceBreakdownSampleCompletion
{
    Sampled,
    Failed,
    SkippedBusy,
    Canceled
}

internal sealed record ResourceBreakdownSampleAttempt(
    ResourceBreakdownSampleCompletion Completion,
    DateTimeOffset AttemptedAt,
    ResourceBreakdownSnapshot? Snapshot,
    SchedulingProcessFactSnapshot? SchedulingSnapshot,
    Exception? Error);

internal sealed record ResourceBreakdownCapture(
    ResourceBreakdownSnapshot ResourceSnapshot,
    SchedulingProcessFactSnapshot? SchedulingSnapshot);

public sealed partial class WindowsResourceBreakdownSampler(
    IMetricSampler metricSampler,
    IRuntimeProcessAttributionCatalogProvider processAttributionCatalogProvider,
    IResourceResidualBreakdownProvider residualBreakdownProvider,
    IPhysicalDiskIoAttributionReader physicalDiskIoAttributionReader,
    INetworkAttributionReader networkAttributionReader,
    IRuntimePlanProvider runtimePlanProvider,
    HostManagerSamplingSubscriptionOwner samplingSubscriptionOwner,
    PdhProcessGpuReader processGpuReader,
    ILogger<WindowsResourceBreakdownSampler>? logger = null,
    GpuAllocationCollector? gpuAllocations = null) :
    IResourceBreakdownSampler,
    IResourceBreakdownObservationSource,
    IResourceBreakdownPushSource,
    ISchedulingProcessFactSource,
    ISchedulingProcessFactObservationSource,
    IHostedService,
    IDisposable
{
    private const int SamplingRoleId = 3;
    private readonly object lifecycleGate = new();
    private readonly SemaphoreSlim sampleGate = new(1, 1);
    private readonly SemaphoreSlim demandSignal = new(0, 1);
    private readonly IWindowsProcessInventoryReader processInventoryReader =
        WindowsProcessInventoryReader.Instance;
    private readonly HostedResourcePublicationState hostedPublicationState = new();
    private readonly ResourceBreakdownSnapshotState directSnapshotState = new();
    private ResourceBreakdownSnapshotState snapshotState =>
        hostedPublicationState.Resource;
    private SchedulingProcessFactSnapshotState schedulingSnapshotState =>
        hostedPublicationState.Scheduling;
    private readonly NativeItemSamplingSubscriptionTracker<ResourceBreakdownSampleRequest> subscriptions = new(
        samplingSubscriptionOwner,
        SamplingRoleId,
        static request => $"resource-breakdown:{request.CacheKey}",
        GetSubscriptionDatasetIds,
        CreateResourceBreakdownSampleRequest,
        coalesceItemsIntoLatestCapture: false);
    private readonly WindowsGpuAdapterOrderReader gpuAdapterReader = new();
    private readonly WindowsShellProcessIdentityReader shellIdentityReader = new();
    private readonly ConcurrentDictionary<string, ProcessFileMetadata> fileMetadataCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ProcessInstanceCache<CachedProcessIdentity> processIdentityCache = new();
    private readonly ProcessInstanceCache<CachedProcessAttribution> processAttributionCache = new();
    private readonly ProcessCpuDeltaTracker hostedProcessCpuDeltaTracker = new();
    private readonly ProcessCpuDeltaTracker directProcessCpuDeltaTracker = new();
    private CancellationTokenSource? samplingCts;
    private Task? samplingTask;
    private Task? samplingDrainTask;
    private HostedResourcePublicationOwnerToken? samplingOwnerToken;
    private bool samplingStopInProgress;
    private long schedulingProcessGeneration;
    private long nextObservationLeaseId;
    private int disposed;
    private int ownedResourcesDisposed;
    private readonly SamplingPublicationSignal publicationSignal = new();

    internal WindowsResourceBreakdownSampler(
        IMetricSampler metricSampler,
        IRuntimeProcessAttributionCatalogProvider processAttributionCatalogProvider,
        IResourceResidualBreakdownProvider residualBreakdownProvider,
        IPhysicalDiskIoAttributionReader physicalDiskIoAttributionReader,
        INetworkAttributionReader networkAttributionReader,
        IRuntimePlanProvider runtimePlanProvider,
        HostManagerSamplingSubscriptionOwner samplingSubscriptionOwner,
        PdhProcessGpuReader processGpuReader,
        IWindowsProcessInventoryReader processInventoryReader)
        : this(
            metricSampler,
            processAttributionCatalogProvider,
            residualBreakdownProvider,
            physicalDiskIoAttributionReader,
            networkAttributionReader,
            runtimePlanProvider,
            samplingSubscriptionOwner,
            processGpuReader)
    {
        this.processInventoryReader = processInventoryReader
            ?? throw new ArgumentNullException(nameof(processInventoryReader));
    }

    public async Task<ResourceBreakdownSnapshot> GetSnapshotAsync(
        IReadOnlyList<string> metricIds,
        IReadOnlyDictionary<string, string> scaleModes,
        CancellationToken cancellationToken)
    {
        return await GetSnapshotAsync(
            ResourceBreakdownSampleRequest.ForResourceTable(metricIds, scaleModes),
            cancellationToken);
    }

    public Task<ResourceBreakdownSnapshot> GetSnapshotAsync(
        ResourceBreakdownSampleRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var subscriptionRequest = SelectSubscribable(request);
        return Task.FromResult(snapshotState.ReadOrCreate(
            DateTimeOffset.UtcNow,
            subscriptionRequest));
    }

    public async Task<ResourceBreakdownSnapshot> CaptureSnapshotAsync(
        ResourceBreakdownSampleRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return await CaptureTrackedSnapshotAsync(request, cancellationToken);
    }

    public IDisposable AcquireSubscription(
        string subscriptionId,
        ResourceBreakdownSampleRequest request,
        TimeSpan refreshInterval)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subscriptionId);
        ArgumentNullException.ThrowIfNull(request);
        var subscriptionRequest = SelectSubscribable(request);
        if (!GetSubscriptionDatasetIds(subscriptionRequest).Any())
        {
            return EmptyObservationSubscription.Instance;
        }
        var leaseId = Interlocked.Increment(ref nextObservationLeaseId);
        var sourceKey = $"lease:{leaseId}:{subscriptionId.Trim()}";
        IDisposable? hardwareLease = null;
        IDisposable? allocationLease = null;
        try
        {
            var hardwareRequest = CreateHardwareRequest(subscriptionRequest);
            if (!hardwareRequest.IsEmpty)
            {
                var observationSource = metricSampler
                    as IMetricSnapshotObservationSource
                    ?? throw new InvalidOperationException(
                        "Hosted resource subscriptions require a hosted hardware observation source.");
                hardwareLease = observationSource.AcquireSubscription(
                    $"resource-breakdown:{sourceKey}",
                    hardwareRequest,
                    refreshInterval);
            }
            if (NeedsGpuAllocations(subscriptionRequest))
                allocationLease = gpuAllocations?.AcquireSubscription();
            subscriptions.TrackPersistent(
                sourceKey,
                subscriptionRequest,
                DateTimeOffset.UtcNow,
                refreshInterval);
            SignalDemand();
            return new ObservationSubscriptionLease(
                this,
                sourceKey,
                hardwareLease,
                allocationLease);
        }
        catch
        {
            hardwareLease?.Dispose();
            allocationLease?.Dispose();
            throw;
        }
    }

    public ResourceBreakdownSnapshot ReadLatest(
        ResourceBreakdownSampleRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return snapshotState.ReadOrCreate(DateTimeOffset.UtcNow, request);
    }

    public async IAsyncEnumerable<ResourceBreakdownSnapshot> SubscribeAsync(
        string subscriptionId,
        ResourceBreakdownSampleRequest request,
        TimeSpan interval,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subscriptionId);
        ArgumentNullException.ThrowIfNull(request);
        var subscriptionRequest = SelectSubscribable(request);
        var datasetIds = GetPublicationDatasetIds(subscriptionRequest).ToArray();
        if (datasetIds.Length == 0)
        {
            yield break;
        }

        using var publication = publicationSignal.Subscribe(datasetIds);
        using var lease = AcquireSubscription(
            subscriptionId,
            subscriptionRequest,
            interval);

        while (!cancellationToken.IsCancellationRequested)
        {
            await publication.WaitAsync(cancellationToken);
            yield return ReadLatest(subscriptionRequest);
        }
    }

    public IDisposable AcquireSubscription(
        string subscriptionId,
        SchedulingProcessMetricMask metricMask,
        TimeSpan refreshInterval)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subscriptionId);
        if (metricMask == SchedulingProcessMetricMask.None
            || (metricMask & ~SupportedSchedulingMetricMask) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(metricMask));
        }
        return AcquireSubscription(
            subscriptionId,
            new ResourceBreakdownSampleRequest(
                [],
                new Dictionary<string, string>(),
                ProcessSampleDetailLevel.SmartSchedulingLite,
                metricMask),
            refreshInterval);
    }

    public SchedulingProcessFactSnapshot? ReadLatest(
        SchedulingProcessFactRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return schedulingSnapshotState.ReadLatest(
            request,
            DateTimeOffset.UtcNow);
    }

    private async Task<ResourceBreakdownSnapshot> CaptureTrackedSnapshotAsync(
        ResourceBreakdownSampleRequest request,
        CancellationToken cancellationToken)
    {
        await sampleGate.WaitAsync(cancellationToken);
        var attemptedAt = DateTimeOffset.UtcNow;
        directSnapshotState.MarkAttempt(request, attemptedAt);
        try
        {
            var timing = CurrentDirectCaptureTiming();
            var capture = await ReadSnapshotCoreAsync(
                request,
                directProcessCpuDeltaTracker,
                cancellationToken,
                requireHostedHardwareObservation: false);
            directSnapshotState.ApplyDirect(
                request,
                capture.ResourceSnapshot,
                attemptedAt,
                timing.DefaultInterval,
                timing.FreshnessGrace);
            return directSnapshotState.ReadOrCreate(DateTimeOffset.UtcNow, request);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            var transition = directSnapshotState.MarkFailed(request, attemptedAt, error);
            if (transition != ResourceBreakdownFailureTransition.Repeated)
            {
                logger?.LogError(error, "Resource breakdown sampling failed.");
            }
            throw;
        }
        finally
        {
            sampleGate.Release();
        }
    }

    private void ReleaseObservationLease(string sourceKey)
    {
        subscriptions.Remove(sourceKey, DateTimeOffset.UtcNow);
        SignalDemand();
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
                    "The resource breakdown worker is still draining after stop.");
            }
            if (samplingTask is { IsCompleted: false })
            {
                return Task.CompletedTask;
            }

            if (samplingDrainTask is { IsCompleted: true })
            {
                samplingDrainTask = null;
            }

            if (samplingOwnerToken is { } abandonedOwner)
            {
                _ = hostedPublicationState.TryCloseOwner(
                    abandonedOwner,
                    DateTimeOffset.UtcNow,
                    new InvalidOperationException(
                        "The previous resource breakdown worker ended without closing its publication owner."));
            }

            samplingCts?.Dispose();
            var cts = new CancellationTokenSource();
            var ownerToken = hostedPublicationState.OpenOwner();
            samplingCts = cts;
            samplingOwnerToken = ownerToken;
            samplingTask = Task.Run(
                () => RunSamplingLoopAsync(ownerToken, cts.Token),
                CancellationToken.None);
        }
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        Task? drain;
        lock (lifecycleGate)
        {
            if (samplingOwnerToken is { } activeOwner)
            {
                _ = hostedPublicationState.TryCloseOwner(
                    activeOwner,
                    DateTimeOffset.UtcNow,
                    new OperationCanceledException(
                        "Resource breakdown owner stopped."));
            }
            if (samplingTask is null)
            {
                samplingOwnerToken = null;
                return;
            }

            samplingStopInProgress = true;
            samplingDrainTask ??= DrainSamplingWorkerAsync(
                samplingTask,
                samplingCts,
                samplingOwnerToken);
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

        Task? drain = null;
        lock (lifecycleGate)
        {
            if (samplingOwnerToken is { } activeOwner)
            {
                _ = hostedPublicationState.TryCloseOwner(
                    activeOwner,
                    DateTimeOffset.UtcNow,
                    new ObjectDisposedException(
                        nameof(WindowsResourceBreakdownSampler)));
            }
            if (samplingTask is not null)
            {
                samplingStopInProgress = true;
                samplingDrainTask ??= DrainSamplingWorkerAsync(
                    samplingTask,
                    samplingCts,
                    samplingOwnerToken);
                drain = samplingDrainTask;
                samplingCts?.Cancel();
            }
        }
        try
        {
            drain?.GetAwaiter().GetResult();
        }
        catch (Exception error)
        {
            logger?.LogError(
                error,
                "Resource breakdown worker failed during disposal.");
        }
        DisposeOwnedResources();
    }

    private async Task DrainSamplingWorkerAsync(
        Task task,
        CancellationTokenSource? cts,
        HostedResourcePublicationOwnerToken? ownerToken)
    {
        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception error)
        {
            logger?.LogError(
                error,
                "Resource breakdown worker failed while draining after stop.");
        }
        finally
        {
            lock (lifecycleGate)
            {
                if (ReferenceEquals(samplingTask, task))
                {
                    samplingTask = null;
                    samplingCts = null;
                    if (samplingOwnerToken == ownerToken)
                    {
                        samplingOwnerToken = null;
                    }
                }
                samplingStopInProgress = false;
                samplingDrainTask = null;
            }
            cts?.Dispose();
        }
    }

    private async Task RunSamplingLoopAsync(
        HostedResourcePublicationOwnerToken ownerToken,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await RunSamplingIterationAsync(ownerToken, cancellationToken);
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception error)
            {
                var failedAt = DateTimeOffset.UtcNow;
                if (!hostedPublicationState.TryMarkOwnerFault(
                        ownerToken,
                        failedAt,
                        error))
                {
                    return;
                }
                logger?.LogError(
                    error,
                    "Resource breakdown background sampling iteration failed; the owner will retry.");
                await DelayAfterWorkerFailureAsync(cancellationToken);
            }
        }
    }

    private async Task RunSamplingIterationAsync(
        HostedResourcePublicationOwnerToken ownerToken,
        CancellationToken cancellationToken)
    {
        var plan = subscriptions.CreatePlan(DateTimeOffset.UtcNow);
        if (!plan.IsActive)
        {
            if (!hostedPublicationState.TryAcceptSchedule(
                    ownerToken,
                    plan.OwnerToken,
                    plan.Schedule))
            {
                return;
            }
            await demandSignal.WaitAsync(cancellationToken);
            return;
        }
        if (plan.Request is null)
        {
            if (!hostedPublicationState.TryAcceptSchedule(
                    ownerToken,
                    plan.OwnerToken,
                    plan.Schedule))
            {
                return;
            }
            await WaitForDemandOrDelayAsync(plan.Delay, cancellationToken);
            return;
        }

        var planSettled = false;
        try
        {
            var attempt = await TryRefreshCacheAsync(plan.Request, cancellationToken);
            var completedAt = DateTimeOffset.UtcNow;
            NativeItemSamplingSubscriptionCompletion completion;
            if (attempt.Completion == ResourceBreakdownSampleCompletion.Sampled)
            {
                if (attempt.Snapshot is null)
                {
                    throw new InvalidOperationException(
                        "Resource breakdown capture completed without a resource snapshot.");
                }

                if (!hostedPublicationState.TryPrepareSample(
                        ownerToken,
                        plan.OwnerToken,
                        plan.Request,
                        attempt.Snapshot,
                        attempt.SchedulingSnapshot,
                        attempt.AttemptedAt,
                        plan.Schedule,
                        out var preparation)
                    || preparation is null
                    || !hostedPublicationState.TryCommit(preparation))
                {
                    completion = subscriptions.MarkSkipped(
                        plan.Request,
                        completedAt);
                    planSettled = true;
                    await WaitForDemandOrDelayAsync(
                        completion.Delay,
                        cancellationToken);
                    return;
                }
                if (preparation.Failure is { } publicationError)
                {
                    logger?.LogError(
                        publicationError,
                        "Resource breakdown capture was published as empty because validation failed.");
                }
                publicationSignal.Publish(plan.DueItemIds);
                completion = subscriptions.MarkSampled(
                    plan.Request,
                    completedAt);
                planSettled = true;
            }
            else if (attempt.Completion == ResourceBreakdownSampleCompletion.Failed)
            {
                var error = attempt.Error
                    ?? new InvalidOperationException(
                        "Background resource breakdown capture failed without an error.");
                if (!hostedPublicationState.TryPrepareFailure(
                        ownerToken,
                        plan.OwnerToken,
                        plan.Request,
                        attempt.AttemptedAt,
                        plan.Schedule,
                        error,
                        out var preparation)
                    || preparation is null
                    || !hostedPublicationState.TryCommit(preparation))
                {
                    completion = subscriptions.MarkSkipped(
                        plan.Request,
                        completedAt);
                    planSettled = true;
                    await WaitForDemandOrDelayAsync(
                        completion.Delay,
                        cancellationToken);
                    return;
                }
                publicationSignal.Publish(plan.DueItemIds);
                completion = subscriptions.MarkSampled(plan.Request, completedAt);
                planSettled = true;
                logger?.LogError(error, "Background resource breakdown sampling failed.");
            }
            else if (attempt.Completion is
                     ResourceBreakdownSampleCompletion.SkippedBusy or
                     ResourceBreakdownSampleCompletion.Canceled)
            {
                completion = subscriptions.MarkSkipped(plan.Request, completedAt);
                planSettled = true;
            }
            else
            {
                throw new InvalidOperationException(
                    "Resource breakdown sampling returned an unknown completion.");
            }
            await WaitForDemandOrDelayAsync(completion.Delay, cancellationToken);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            if (!planSettled)
            {
                subscriptions.MarkSkipped(
                    plan.Request,
                    DateTimeOffset.UtcNow);
            }
            throw;
        }
        catch (Exception error)
        {
            if (!planSettled)
            {
                try
                {
                    subscriptions.MarkSkipped(
                        plan.Request,
                        DateTimeOffset.UtcNow);
                }
                catch (Exception settlementError)
                {
                    throw new AggregateException(
                        "Resource breakdown sampling faulted and its native plan could not be released.",
                        error,
                        settlementError);
                }
            }
            throw;
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
        TimeSpan interval,
        CancellationToken cancellationToken)
    {
        if (interval <= TimeSpan.Zero)
        {
            return;
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var demand = demandSignal.WaitAsync(linked.Token);
        var delay = Task.Delay(interval, linked.Token);
        await Task.WhenAny(demand, delay);
        await linked.CancelAsync();
        try
        {
            await Task.WhenAll(demand, delay);
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
        }
    }

    private void SignalDemand()
    {
        ReleaseDemandSignal(demandSignal);
    }

    internal static void ReleaseDemandSignal(SemaphoreSlim signal)
    {
        ArgumentNullException.ThrowIfNull(signal);
        try
        {
            signal.Release();
        }
        catch (SemaphoreFullException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private async Task<ResourceBreakdownSampleAttempt> TryRefreshCacheAsync(
        ResourceBreakdownSampleRequest request,
        CancellationToken cancellationToken)
    {
        bool entered;
        try
        {
            entered = await sampleGate.WaitAsync(0, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new ResourceBreakdownSampleAttempt(
                    ResourceBreakdownSampleCompletion.Canceled,
                    DateTimeOffset.UtcNow,
                    null,
                    null,
                    null);
        }

        if (!entered)
        {
            return new ResourceBreakdownSampleAttempt(
                ResourceBreakdownSampleCompletion.SkippedBusy,
                DateTimeOffset.UtcNow,
                null,
                null,
                null);
        }

        var attemptedAt = DateTimeOffset.UtcNow;
        try
        {
            var capture = await ReadSnapshotCoreAsync(
                request,
                hostedProcessCpuDeltaTracker,
                cancellationToken,
                requireHostedHardwareObservation: true);
            return new ResourceBreakdownSampleAttempt(
                ResourceBreakdownSampleCompletion.Sampled,
                attemptedAt,
                capture.ResourceSnapshot,
                capture.SchedulingSnapshot,
                null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new ResourceBreakdownSampleAttempt(
                ResourceBreakdownSampleCompletion.Canceled,
                attemptedAt,
                null,
                null,
                null);
        }
        catch (Exception error)
        {
            return new ResourceBreakdownSampleAttempt(
                ResourceBreakdownSampleCompletion.Failed,
                attemptedAt,
                null,
                null,
                error);
        }
        finally
        {
            sampleGate.Release();
        }
    }

    private (TimeSpan DefaultInterval, TimeSpan FreshnessGrace) CurrentDirectCaptureTiming()
    {
        var role = runtimePlanProvider.Current.HostManager
            .RequirePublished()
            .SamplingSubscription
            .HotPublish
            .Roles
            .Single(static role => role.RoleId == SamplingRoleId);
        return (
            TimeSpan.FromMilliseconds(role.DefaultIntervalMilliseconds),
            TimeSpan.FromMilliseconds(role.FreshnessGraceMilliseconds));
    }

    private void DisposeOwnedResources()
    {
        if (Interlocked.Exchange(ref ownedResourcesDisposed, 1) != 0)
        {
            return;
        }

        CancellationTokenSource? cts;
        lock (lifecycleGate)
        {
            cts = samplingCts;
            samplingCts = null;
        }
        cts?.Dispose();
        demandSignal.Dispose();
        sampleGate.Dispose();
    }

    private async Task<ResourceBreakdownCapture> ReadSnapshotCoreAsync(
        ResourceBreakdownSampleRequest request,
        ProcessCpuDeltaTracker cpuDeltaTracker,
        CancellationToken cancellationToken,
        bool requireHostedHardwareObservation)
    {
        var cleanMetricIds = request.MetricIds
            .Where(IsSupportedMetric)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var publicationDatasetIds = request.PublicationDatasetIds.ToHashSet(
            StringComparer.OrdinalIgnoreCase);
        var capturesGpuDataset = publicationDatasetIds.Contains(
                SamplingDatasetIds.ProcessGpuUsage)
            || publicationDatasetIds.Contains(
                SamplingDatasetIds.ProcessGpuVram)
            || cleanMetricIds.Any(IsGpuMetric);
        IReadOnlyList<WindowsGpuAdapter> adapters = capturesGpuDataset
            ? gpuAdapterReader.ReadInventory().Adapters
                .Where(static adapter => !adapter.IsSoftware)
                .ToArray()
            : [];
        if (publicationDatasetIds.Contains(SamplingDatasetIds.ProcessGpuUsage)
            || publicationDatasetIds.Contains(SamplingDatasetIds.ProcessGpuVram))
        {
            var expandedGpuMetrics = cleanMetricIds.ToHashSet(
                StringComparer.OrdinalIgnoreCase);
            foreach (var adapter in adapters)
            {
                if (publicationDatasetIds.Contains(SamplingDatasetIds.ProcessGpuUsage))
                {
                    expandedGpuMetrics.Add($"gpu.{adapter.Index}.usage");
                }
                if (publicationDatasetIds.Contains(SamplingDatasetIds.ProcessGpuVram))
                {
                    expandedGpuMetrics.Add($"gpu.{adapter.Index}.vram");
                }
            }
            cleanMetricIds = expandedGpuMetrics
                .Order(MonitoringMetricIdPriorityComparer.Instance)
                .ToArray();
        }
        var publishesProcessInventory = publicationDatasetIds.Contains(
            SamplingDatasetIds.ProcessInventory);
        var publishesProcessAttribution = publicationDatasetIds.Contains(
            SamplingDatasetIds.ProcessAttribution);
        if (cleanMetricIds.Length == 0
            && request.SchedulingMetricMask == SchedulingProcessMetricMask.None
            && !publishesProcessInventory
            && !publishesProcessAttribution)
        {
            return new ResourceBreakdownCapture(
                new ResourceBreakdownSnapshot(DateTimeOffset.Now, []),
                null);
        }

        var needsCpu = cleanMetricIds.Contains(
                "cpu.usage",
                StringComparer.OrdinalIgnoreCase)
            || request.SchedulingMetricMask.HasFlag(
                SchedulingProcessMetricMask.CpuUsage);
        var needsDisk = cleanMetricIds.Any(IsDiskMetric);
        var needsNetwork = cleanMetricIds.Any(IsNetworkMetric);
        var processBatch = CaptureProcessSamples(
            needsCpu,
            request.ProcessDetailLevel,
            cpuDeltaTracker);
        if (processBatch.Status != SamplingObservationStatus.Current)
        {
            throw new InvalidOperationException(
                "The process inventory did not produce a complete current observation.");
        }
        var processSamples = processBatch.Samples;
        ProcessAttributionSnapshot? processAttribution = null;
        if (cleanMetricIds.Length > 0 || RequiresProcessAttribution(request))
        {
            var attributionCatalog =
                await processAttributionCatalogProvider.GetCatalogAsync(
                    cancellationToken);
            processAttribution = CreateProcessAttributionSnapshot(
                processSamples,
                attributionCatalog);
        }
        var hardwareRequest = CreateHardwareRequest(
            cleanMetricIds,
            request.SchedulingMetricMask);
        HardwareMetricSnapshot? hardwareSnapshot = null;
        if (!hardwareRequest.IsEmpty)
        {
            hardwareSnapshot = await ReadHardwareSnapshotAsync(
                hardwareRequest,
                cancellationToken,
                requireHostedHardwareObservation);
        }
        var gpuAttribution = adapters.Count > 0
            ? processGpuReader.ReadBreakdownSnapshot(adapters)
            : ProcessGpuBreakdownRead.NotRequested;
        var networkAttribution = needsNetwork
            ? networkAttributionReader.Read(
                CreateNetworkAttributionRequest(
                    hardwareSnapshot!,
                    processSamples))
            : NetworkAttributionSnapshot.NotRequested;
        var diskAttribution = needsDisk
            ? physicalDiskIoAttributionReader.Read()
            : PhysicalDiskIoAttributionSnapshot.NotRequested;

        var bars = new List<ResourceBreakdownBar>();
        var allocationReading = NeedsGpuAllocations(request)
            ? ReadGpuAllocations(processSamples)
            : null;
        var baseScorePlan = runtimePlanProvider.Current.BaseScore;
        foreach (var metricId in cleanMetricIds)
        {
            if (processAttribution is null || hardwareSnapshot is null)
            {
                throw new InvalidOperationException(
                    "A resource-breakdown metric capture omitted its attribution or hardware dependency.");
            }
            var scaleMode = ResourceBreakdownScaleModes.Normalize(metricId, request.ScaleModes.GetValueOrDefault(metricId));
            var bar = CreateBar(
                metricId,
                scaleMode,
                processAttribution,
                baseScorePlan,
                hardwareSnapshot,
                gpuAttribution,
                diskAttribution,
                networkAttribution);
            if (bar is not null)
            {
                if (TryParseGpuMetric(metricId, out var adapterIndex, out var kind) && kind == "vram")
                {
                    var adapter = adapters.FirstOrDefault(a => a.Index == adapterIndex);
                    var amounts = adapter is null ? null : allocationReading?.Adapters.GetValueOrDefault(NativePdhAdapterIdentity.Pack(adapter.Luid));
                    bar = CreateGpuAllocationBar(bar, amounts, processAttribution, baseScorePlan);
                }
                bars.Add(bar);
            }
        }

        var snapshot = new ResourceBreakdownSnapshot(DateTimeOffset.Now, bars);
        SchedulingProcessFactSnapshot? schedulingSnapshot = null;
        if (request.SchedulingMetricMask != SchedulingProcessMetricMask.None
            || publishesProcessInventory
            || publishesProcessAttribution)
        {
            var generation = checked((ulong)Interlocked.Increment(
                ref schedulingProcessGeneration));
            var memoryDependency = hardwareSnapshot is not null
                && SystemMemoryUsageDependency.TryCreate(
                    hardwareSnapshot,
                    DateTimeOffset.UtcNow,
                    out var currentMemoryDependency)
                ? currentMemoryDependency
                : null;
            var gpuInventory = hardwareSnapshot?.GpuInventory
                ?? new SchedulingGpuInventorySnapshot(
                    SamplingObservationStatus.NotRequested,
                    0,
                    0,
                    0,
                    0,
                    0,
                    0,
                    []);
            schedulingSnapshot = CreateSchedulingSnapshot(
                new SchedulingProcessFactRequest(
                    request.SchedulingMetricMask,
                    memoryDependency,
                    gpuInventory),
                processBatch,
                processAttribution,
                generation,
                snapshot.CapturedAt.UtcTicks,
                preserveUnattributedMetricRows: true,
                allocationReading: allocationReading);
        }
        return new ResourceBreakdownCapture(snapshot, schedulingSnapshot);
    }

    private async Task<HardwareMetricSnapshot> ReadHardwareSnapshotAsync(
        MetricSampleRequest request,
        CancellationToken cancellationToken,
        bool requireHostedObservation)
    {
        if (!requireHostedObservation)
        {
            return await metricSampler.CaptureSnapshotAsync(
                request,
                cancellationToken);
        }

        var observationSource = metricSampler
            as IMetricSnapshotObservationSource
            ?? throw new InvalidOperationException(
                "Hosted resource sampling requires a hosted hardware observation source.");
        return observationSource.ReadLatest(request)
            ?? throw new InvalidOperationException(
                "The hosted hardware dataset has not published a snapshot yet.");
    }

    private sealed class ObservationSubscriptionLease(
        WindowsResourceBreakdownSampler owner,
        string sourceKey,
        IDisposable? hardwareLease,
        IDisposable? allocationLease) : IDisposable
    {
        private int state;

        public void Dispose()
        {
            if (Interlocked.CompareExchange(ref state, 1, 0) != 0)
            {
                return;
            }

            Exception? resourceReleaseError = null;
            Exception? hardwareReleaseError = null;
            Exception? allocationReleaseError = null;
            try
            {
                owner.ReleaseObservationLease(sourceKey);
            }
            catch (Exception error)
            {
                resourceReleaseError = error;
            }

            try
            {
                hardwareLease?.Dispose();
            }
            catch (Exception error)
            {
                hardwareReleaseError = error;
            }

            try { allocationLease?.Dispose(); }
            catch (Exception error) { allocationReleaseError = error; }
            Volatile.Write(ref state, 2);
            if (allocationReleaseError is not null)
                throw new AggregateException(new[] { resourceReleaseError, hardwareReleaseError, allocationReleaseError }.OfType<Exception>());
            if (resourceReleaseError is not null
                && hardwareReleaseError is not null)
            {
                throw new AggregateException(
                    "Resource and hardware observation leases both failed to release.",
                    resourceReleaseError,
                    hardwareReleaseError);
            }
            if (resourceReleaseError is not null)
            {
                throw resourceReleaseError;
            }
            if (hardwareReleaseError is not null)
            {
                throw hardwareReleaseError;
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
}
