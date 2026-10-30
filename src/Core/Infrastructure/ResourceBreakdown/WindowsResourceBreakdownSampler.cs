using ResourceManager.App.Infrastructure.Adaptation;
using ResourceManager.ReadWrite.Windows.Processes;
using ResourceManager.ReadWrite.ProcessFields;
using System.Collections.Concurrent;
using System.Collections.Immutable;
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
    Exception? Error)
{
    internal ProcessIoRatePreparation? IoRates { get; init; }
    internal ImmutableHashSet<string> QueriedPublicationKeys { get; init; }
        = ImmutableHashSet<string>.Empty.WithComparer(StringComparer.OrdinalIgnoreCase);
}

internal sealed record ResourceBreakdownCapture(
    ResourceBreakdownSnapshot ResourceSnapshot,
    SchedulingProcessFactSnapshot? SchedulingSnapshot)
{
    internal ProcessIoRatePreparation? IoRates { get; init; }
    internal ImmutableHashSet<string> QueriedPublicationKeys { get; init; }
        = ImmutableHashSet<string>.Empty.WithComparer(StringComparer.OrdinalIgnoreCase);
}

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
    GpuAllocationCollector? gpuAllocations = null,
    InteractiveDesktopObservationStore? interactiveDesktops = null,
    ResourceManager.Shared.BusinessBlocks.State.StateAuthority? stateAuthority = null) :
    IResourceBreakdownSampler,
    IResourceBreakdownObservationSource,
    IResourceBreakdownPushSource,
    ISchedulingProcessFactSource,
    ISchedulingProcessFactObservationSource,
    ISchedulingProcessFactPublicationSource,
    IRuntimeSoftwareObservationSource,
    IHostedService,
    IDisposable
{
    private const int SamplingRoleId = 3;
    internal Func<ulong, CancellationToken, PhysicalMemoryResidualFrame> PhysicalMemoryPfnCapture { get; set; }
        = new WindowsPhysicalMemoryPfnReader(state =>
        {
            if (state == "ready") logger?.LogInformation("Physical memory PFN state: {State}", state);
            else logger?.LogWarning("Physical memory PFN state: {State}", state);
        }).Capture;
    private readonly object lifecycleGate = new();
    private WindowsProcessFieldOwner processFieldOwner => hostedPublicationState.ProcessOwner;
    private SemaphoreSlim sampleGate => processFieldOwner.CaptureGate;
    private readonly SemaphoreSlim demandSignal = new(0, 1);
    private IWindowsProcessInventoryReader processInventoryReader { get => processFieldOwner.Inventory; set => processFieldOwner.Inventory = value; }
    private WindowsProcessBasicMetricReader processBasicMetricReader { get => processFieldOwner.BasicReader; set => processFieldOwner.BasicReader = value; }
    private readonly HostedResourcePublicationState hostedPublicationState = new(processGpuReader?.SnapshotSource, stateAuthority);
    private readonly ResourceBreakdownSnapshotState directSnapshotState = new();
    private ResourceBreakdownSnapshotState snapshotState =>
        hostedPublicationState.Resource;
    private SchedulingProcessFactSnapshotState schedulingSnapshotState =>
        hostedPublicationState.Scheduling;
    private readonly NativeItemSamplingSubscriptionTracker<ResourceBreakdownSampleRequest> subscriptions = CreateSubscriptions(
        samplingSubscriptionOwner, createOutputScratch: null);

    private static NativeItemSamplingSubscriptionTracker<ResourceBreakdownSampleRequest> CreateSubscriptions(
        HostManagerSamplingSubscriptionOwner owner,
        Func<ulong, uint, INativeSamplingOutputScratch>? createOutputScratch)
    {
        var tracker = new NativeItemSamplingSubscriptionTracker<ResourceBreakdownSampleRequest>(owner, SamplingRoleId,
            static request => $"resource-breakdown:{request.CacheKey}", GetSubscriptionDatasetIds,
            CreateResourceBreakdownSampleRequest, coalesceItemsIntoLatestCapture: false, createOutputScratch);
        void Bind(SubscriptionFieldGroup group, Func<ResourceBreakdownSampleRequest, bool> matches)
            => owner.Activity.BindField(group, tracker.CreateActivityRead(matches),
                handler => tracker.SubscriptionsChanged += handler);
        Bind(SubscriptionFieldGroup.ProcessDedicatedGpuMemory, NeedsGpuAllocations);
        Bind(SubscriptionFieldGroup.VramResidual, static request => request.MetricIds.Any(
            static id => TryParseGpuMetric(id, out _, out var kind) && kind == "vram"));
        Bind(SubscriptionFieldGroup.DiskAttribution, static request => request.MetricIds.Any(IsDiskMetric)
            || request.PublicationDatasetIds.Contains(SamplingDatasetIds.ProcessObservedPhysicalDiskIo)
            || request.MetricIds.Any(static id => ProcessMetricFieldRegistry.Find(id)?.DatasetId == SamplingDatasetIds.ProcessObservedPhysicalDiskIo));
        Bind(SubscriptionFieldGroup.NetworkAttribution, static request => request.MetricIds.Any(IsNetworkMetric)
            || request.PublicationDatasetIds.Contains(SamplingDatasetIds.ProcessObservedNetworkTraffic)
            || request.MetricIds.Any(static id => ProcessMetricFieldRegistry.Find(id)?.DatasetId == SamplingDatasetIds.ProcessObservedNetworkTraffic));
        return tracker;
    }
    private readonly Func<WindowsGpuAdapterInventoryRead> gpuAdapterInventory = new WindowsGpuAdapterOrderReader().ReadInventory;
    private readonly WindowsShellProcessIdentityReader shellIdentityReader = new();
    private readonly ConcurrentDictionary<string, ProcessFileMetadata> fileMetadataCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ProcessInstanceCache<CachedProcessIdentity> processIdentityCache = new();
    private readonly ProcessInstanceCache<CachedProcessAttribution> processAttributionCache = new();
    private ProcessCpuDeltaTracker hostedProcessCpuDeltaTracker => processFieldOwner.HostedCpu;
    private ProcessCpuDeltaTracker directProcessCpuDeltaTracker => processFieldOwner.DirectCpu;
    private CancellationTokenSource? samplingCts;
    private Task? samplingTask;
    private Task? samplingDrainTask;
    private HostedResourcePublicationOwnerToken? samplingOwnerToken;
    private bool samplingStopInProgress;
    private long schedulingProcessGeneration;
    private long nextObservationLeaseId;
    private int disposed;
    private int ownedResourcesDisposed;
    private SamplingPublicationSignal publicationSignal => processFieldOwner.Publications;
    SamplingPublicationSignal ISchedulingProcessFactPublicationSource.Publications => publicationSignal;

    // Production DI selects this internal route explicitly; the public 11-argument
    // CLR constructor remains the original ordinary-storage entry point.
    internal WindowsResourceBreakdownSampler(
        IMetricSampler metricSampler,
        IRuntimeProcessAttributionCatalogProvider processAttributionCatalogProvider,
        IResourceResidualBreakdownProvider residualBreakdownProvider,
        IPhysicalDiskIoAttributionReader physicalDiskIoAttributionReader,
        INetworkAttributionReader networkAttributionReader,
        IRuntimePlanProvider runtimePlanProvider,
        HostManagerSamplingSubscriptionOwner samplingSubscriptionOwner,
        PdhProcessGpuReader processGpuReader,
        ResourceManagerLedgerOwner ledgerOwner,
        ILogger<WindowsResourceBreakdownSampler>? logger = null,
        GpuAllocationCollector? gpuAllocations = null,
        InteractiveDesktopObservationStore? interactiveDesktops = null,
        ResourceManager.Shared.BusinessBlocks.State.StateAuthority? stateAuthority = null)
        : this(metricSampler, processAttributionCatalogProvider, residualBreakdownProvider,
            physicalDiskIoAttributionReader, networkAttributionReader, runtimePlanProvider,
            samplingSubscriptionOwner, processGpuReader, logger, gpuAllocations, interactiveDesktops, stateAuthority)
    {
        ArgumentNullException.ThrowIfNull(ledgerOwner);
        subscriptions = CreateSubscriptions(samplingSubscriptionOwner,
            (length, alignment) => SamplingOutputScratch.Create(ledgerOwner.CreateScratch, length, alignment));
    }

    internal WindowsResourceBreakdownSampler(
        IMetricSampler metricSampler,
        IRuntimeProcessAttributionCatalogProvider processAttributionCatalogProvider,
        IResourceResidualBreakdownProvider residualBreakdownProvider,
        IPhysicalDiskIoAttributionReader physicalDiskIoAttributionReader,
        INetworkAttributionReader networkAttributionReader,
        IRuntimePlanProvider runtimePlanProvider,
        HostManagerSamplingSubscriptionOwner samplingSubscriptionOwner,
        PdhProcessGpuReader processGpuReader,
        IWindowsProcessInventoryReader processInventoryReader,
        WindowsProcessBasicMetricReader? processBasicMetricReader = null,
        Func<ulong, uint, INativeSamplingOutputScratch>? createOutputScratch = null,
        GpuAllocationCollector? gpuAllocations = null,
        Func<WindowsGpuAdapterInventoryRead>? gpuAdapterInventory = null)
        : this(
            metricSampler,
            processAttributionCatalogProvider,
            residualBreakdownProvider,
            physicalDiskIoAttributionReader,
            networkAttributionReader,
            runtimePlanProvider,
            samplingSubscriptionOwner,
            processGpuReader,
            gpuAllocations: gpuAllocations)
    {
        if (createOutputScratch is not null)
            subscriptions = CreateSubscriptions(samplingSubscriptionOwner, createOutputScratch);
        this.processInventoryReader = processInventoryReader
            ?? throw new ArgumentNullException(nameof(processInventoryReader));
        if (gpuAdapterInventory is not null) this.gpuAdapterInventory = gpuAdapterInventory;
        if (processBasicMetricReader is not null) this.processBasicMetricReader = processBasicMetricReader;
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

    public async Task<ResourceBreakdownSnapshot> GetSnapshotAsync(
        ResourceBreakdownSampleRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var subscriptionRequest = SelectSubscribable(request);
        return await ((IResourceBreakdownPushSource)this).FetchOnceAsync(
            "single-fetch:resources", subscriptionRequest, cancellationToken).ConfigureAwait(false)
            ?? new ResourceBreakdownSnapshot(DateTimeOffset.UtcNow, [])
            { Sampling = new(ResourceBreakdownSamplingStatuses.Warming, 0, null, null, null, null) };
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
        lock (lifecycleGate)
        {
            ValidateFieldSubscriptionAdmission();
            return AcquireHostedSubscription(sourceKey, subscriptionRequest, refreshInterval,
                NativeSamplingSubscriptionSourceFlags.None, null);
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

        cancellationToken.ThrowIfCancellationRequested();
        subscriptions.ValidatePeriod(interval);
        ResourceManager.Subscription.NativeCurrentValueSubscriptionDelivery<ResourceBreakdownSnapshot>.Registration registration;
        IDisposable lease;
        lock (lifecycleGate)
        {
            ValidateFieldSubscriptionAdmission();
            var sourceKey = CreateFieldSubscriptionSourceKey(subscriptionId);
            registration = snapshotSubscriptionDelivery.Register(sourceKey,
                subscriptions.GetDeliveryItemIds(subscriptionRequest), () => ReadLatest(subscriptionRequest));
            try
            {
                lease = AcquireHostedSubscription(sourceKey, subscriptionRequest, interval,
                    NativeSamplingSubscriptionSourceFlags.PeriodicTriggers, null);
            }
            catch { registration.Dispose(); throw; }
        }
        using (lease)
        using (registration)
            await foreach (var snapshot in registration.ReadAllAsync(cancellationToken).ConfigureAwait(false))
                yield return snapshot;
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

    public IReadOnlyList<ResourceManager.App.Domain.ProcessAttribution.RuntimeSoftwareAttribution> ReadLatestSoftware()
        => schedulingSnapshotState.ReadLatestSoftware();

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
        if (request.MetricIds.Contains(PhysicalMemoryResidualFrame.MetricId))
            throw new InvalidOperationException("PFN details are captured only by the held subscription schedule.");
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
            cancellationToken.ThrowIfCancellationRequested();
            directSnapshotState.ApplyDirect(
                request,
                capture.ResourceSnapshot,
                attemptedAt,
                timing.DefaultInterval,
                timing.FreshnessGrace);
            if (capture.IoRates is not null) processFieldOwner.DirectIo.Commit(capture.IoRates);
            return directSnapshotState.ReadOrCreate(DateTimeOffset.UtcNow, request);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            var transition = directSnapshotState.MarkFailed(request, attemptedAt, error);
            ClearIoHistory(request, processFieldOwner.DirectIo);
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
        lock (lifecycleGate)
        {
            if (Volatile.Read(ref disposed) != 0) return;
            subscriptions.Remove(sourceKey, DateTimeOffset.UtcNow);
        }
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
            pendingFieldCollectionResult = null;
            if (samplingTask is null)
            {
                samplingOwnerToken = null;
                drain = null;
            }
            else
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

        if (drain is not null) await drain.WaitAsync(cancellationToken);
        else await processFieldOwner.ReleaseProcessInventoryBufferAsync(cancellationToken);
    }

    public void Dispose()
    {
        var firstDisposal = Interlocked.Exchange(ref disposed, 1) == 0;
        Task? drain = null;
        lock (lifecycleGate)
        {
            if (firstDisposal && samplingOwnerToken is { } activeOwner)
            {
                _ = hostedPublicationState.TryCloseOwner(
                    activeOwner,
                    DateTimeOffset.UtcNow,
                    new ObjectDisposedException(
                        nameof(WindowsResourceBreakdownSampler)));
            }
            if (firstDisposal)
            {
                fieldSubscriptionDelivery.RetireAll();
                snapshotSubscriptionDelivery.RetireAll();
                pendingFieldCollectionResult = null;
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
            if (subscriptions.HasOutstandingOutputResources) throw;
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
            if (subscriptions.HasOutstandingOutputResources) throw;
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
        var ownerRevoked = false;
        IDisposable? hardwareSubscription = null;
        string? hardwareSubscriptionKey = null;
        // These readers belong to this active sampling run, never to a field.
        Dictionary<string, RuntimeProcessAttributionCatalogLease>? attributionCatalogs = null;
        void ReleaseAttributionCatalogs()
        {
            if (attributionCatalogs is null) return;
            foreach (var reader in attributionCatalogs.Values) reader.Dispose();
            attributionCatalogs = null;
        }
        var worker = new NativeSamplingSubscriptionWorker<ResourceBreakdownSampleRequest, SamplingOwnerToken>(
            subscriptions, demandSignal, static _ => { }, failure =>
            {
                var error = failure.OriginalError is null ? failure.Error
                    : new AggregateException("Resource breakdown sampling faulted and its native plan could not be released.",
                        failure.OriginalError, failure.Error);
                if (!hostedPublicationState.TryMarkOwnerFault(ownerToken, DateTimeOffset.UtcNow, error))
                {
                    ownerRevoked = true;
                    System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
                }
                logger?.LogError(error,
                    "Resource breakdown background sampling failed and returned.");
            });
        try
        {
            await worker.RunAsync((plan, token) => CollectAndPublishAsync(ownerToken, plan, token, attributionCatalogs),
                cancellationToken, planCreated: plan =>
                {
                    if (plan.SourceViews.Any(static source => GetSubscriptionDatasetIds(source.Request)
                            .Contains(SamplingDatasetIds.ProcessAttribution)))
                        attributionCatalogs ??= new(StringComparer.Ordinal);
                    else
                        // The worker can wait for demand without returning from RunAsync.
                        ReleaseAttributionCatalogs();
                    gpuAllocations?.SynchronizeCaptureInterval(plan.SourceViews
                        .Where(static source => NeedsGpuAllocations(source.Request))
                        .Select(static source => (TimeSpan?)source.Interval).Min(), cancellationToken);
                    var hardwareRequest = MetricSampleRequest.Merge(plan.SourceViews.Select(static source => CreateHardwareRequest(source.Request)));
                    var hardwareInterval = plan.SourceViews.Count == 0 ? plan.Schedule.DefaultInterval
                        : plan.SourceViews.Min(static source => source.Interval);
                    var hardwareKey = hardwareRequest.CacheKey + hardwareInterval.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    if (hardwareKey != hardwareSubscriptionKey)
                    {
                        hardwareSubscription?.Dispose();
                        hardwareSubscription = null;
                        hardwareSubscriptionKey = null;
                        if (!hardwareRequest.IsEmpty)
                        {
                            var source = metricSampler as IMetricSnapshotObservationSource
                                ?? throw new InvalidOperationException("Hosted resource sampling requires a hardware observation source.");
                            hardwareSubscription = source.AcquireSubscription("resource-breakdown:running", hardwareRequest,
                                hardwareInterval);
                        }
                        hardwareSubscriptionKey = hardwareKey;
                    }
                    pendingFieldCollectionResult = null;
                    if (plan.DueItemIds.Count == 0 && !hostedPublicationState.TryAcceptSchedule(
                            ownerToken, plan.OwnerToken, plan.Schedule))
                    {
                        ownerRevoked = true;
                        throw new OperationCanceledException(cancellationToken);
                    }
                    if (plan.Request is null)
                    {
                        fieldSubscriptionDelivery.Deliver(plan.Triggers, null, freshOnly: false);
                        snapshotSubscriptionDelivery.Deliver(plan.Triggers);
                    }
                }, collectionCompleted: (plan, _) =>
                {
                    var result = pendingFieldCollectionResult;
                    pendingFieldCollectionResult = null;
                    result ??= fieldSubscriptionDelivery.CaptureCompleted(plan.Triggers,
                        ImmutableHashSet<string>.Empty.WithComparer(StringComparer.OrdinalIgnoreCase), committed: false);
                    fieldSubscriptionDelivery.Deliver(plan.Triggers, result,
                        freshOnly: plan.Request is null ? true : null);
                    if (plan.Request is not null)
                        snapshotSubscriptionDelivery.Deliver(plan.Triggers);
                });
        }
        catch (Exception) when (ownerRevoked && !subscriptions.HasOutstandingOutputResources) { }
        finally
        {
            try { ReleaseAttributionCatalogs(); }
            finally
            {
                pendingFieldCollectionResult = null;
                fieldSubscriptionDelivery.DiscardRunRounds();
                snapshotSubscriptionDelivery.DiscardRunRounds();
                try { gpuAllocations?.SynchronizeCaptureInterval(null); }
                finally
                {
                    try { hardwareSubscription?.Dispose(); }
                    finally { await processFieldOwner.ReleaseProcessInventoryBufferAsync(CancellationToken.None); }
                }
            }
        }
    }

    private async Task<SubscriptionSampleCompletion> CollectAndPublishAsync(
        HostedResourcePublicationOwnerToken ownerToken,
        NativeItemSamplingSubscriptionPlan<ResourceBreakdownSampleRequest, SamplingOwnerToken> plan,
        CancellationToken cancellationToken,
        IDictionary<string, RuntimeProcessAttributionCatalogLease>? retainedCatalogs)
    {
        var request = plan.Request!;
        var gpuInterval = plan.SourceViews.Where(static source => NeedsGpuAllocations(source.Request))
            .Select(static source => source.Interval).DefaultIfEmpty(plan.Schedule.DefaultInterval).Min();
        var attempt = await TryRefreshCacheAsync(request, cancellationToken, gpuInterval, retainedCatalogs);
        cancellationToken.ThrowIfCancellationRequested();
        if (attempt.Completion == ResourceBreakdownSampleCompletion.Sampled)
        {
            if (attempt.Snapshot is null)
                throw new InvalidOperationException("Resource breakdown capture completed without a resource snapshot.");
            if (!hostedPublicationState.TryPrepareSample(ownerToken, plan.OwnerToken, request,
                    attempt.Snapshot, attempt.SchedulingSnapshot, attempt.AttemptedAt, plan.Schedule,
                    out var preparation)
                || preparation is null || !hostedPublicationState.TryCommit(preparation, () =>
                {
                    if (preparation.Failure is null && attempt.IoRates is not null) processFieldOwner.HostedIo.Commit(attempt.IoRates);
                    else if (preparation.Failure is not null) ClearIoHistory(request, processFieldOwner.HostedIo);
                    try
                    {
                        pendingFieldCollectionResult = fieldSubscriptionDelivery.CaptureCompleted(
                            plan.Triggers, attempt.QueriedPublicationKeys, committed: true);
                    }
                    catch (Exception error)
                    {
                        pendingFieldCollectionResult = fieldSubscriptionDelivery.CaptureCompleted(
                            plan.Triggers,
                            ImmutableHashSet<string>.Empty.WithComparer(StringComparer.OrdinalIgnoreCase),
                            committed: false);
                        logger?.LogError(error,
                            "Resource breakdown committed, but its field delivery snapshot was unavailable.");
                    }
                }))
                return SubscriptionSampleCompletion.Skipped;
            if (preparation.Failure is { } publicationError)
                logger?.LogError(publicationError,
                    "Resource breakdown capture was published as empty because validation failed.");
            publicationSignal.Publish(GetPublicationDatasetIds(request));
            return SubscriptionSampleCompletion.Sampled;
        }
        if (attempt.Completion == ResourceBreakdownSampleCompletion.Failed)
        {
            var error = attempt.Error ?? new InvalidOperationException(
                "Background resource breakdown capture failed without an error.");
            if (!hostedPublicationState.TryPrepareFailure(ownerToken, plan.OwnerToken, request,
                    attempt.AttemptedAt, plan.Schedule, error, out var preparation)
                || preparation is null || !hostedPublicationState.TryCommit(preparation, () =>
                {
                    ClearIoHistory(request, processFieldOwner.HostedIo);
                    pendingFieldCollectionResult = fieldSubscriptionDelivery.CaptureCompleted(
                        plan.Triggers,
                        ImmutableHashSet<string>.Empty.WithComparer(StringComparer.OrdinalIgnoreCase),
                        committed: false);
                }))
                return SubscriptionSampleCompletion.Skipped;
            publicationSignal.Publish(GetPublicationDatasetIds(request));
            logger?.LogError(error, "Background resource breakdown sampling failed.");
            // Original native settlement treats a committed empty publication as a completed sample.
            return SubscriptionSampleCompletion.Sampled;
        }
        return attempt.Completion switch
        {
            ResourceBreakdownSampleCompletion.SkippedBusy => SubscriptionSampleCompletion.Skipped,
            ResourceBreakdownSampleCompletion.Canceled => SubscriptionSampleCompletion.Canceled,
            _ => throw new InvalidOperationException("Resource breakdown sampling returned an unknown completion.")
        };
    }

    private void SignalDemand()
    {
        ReleaseDemandSignal(demandSignal);
    }

    internal static void ReleaseDemandSignal(SemaphoreSlim signal)
    {
        ArgumentNullException.ThrowIfNull(signal);
        if (signal.CurrentCount != 0)
        {
            return;
        }

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
        CancellationToken cancellationToken, TimeSpan gpuAllocationInterval,
        IDictionary<string, RuntimeProcessAttributionCatalogLease>? retainedCatalogs)
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
                requireHostedHardwareObservation: true, gpuAllocationInterval, retainedCatalogs);
            cancellationToken.ThrowIfCancellationRequested();
            return new ResourceBreakdownSampleAttempt(
                ResourceBreakdownSampleCompletion.Sampled,
                attemptedAt,
                capture.ResourceSnapshot,
                capture.SchedulingSnapshot,
                null)
            {
                IoRates = capture.IoRates,
                QueriedPublicationKeys = capture.QueriedPublicationKeys
            };
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
        lock (lifecycleGate)
        {
            if (Volatile.Read(ref ownedResourcesDisposed) != 0) return;
            // The worker is drained before this point. The tracker gate excludes
            // synchronous Plan, including projection and the exact output borrow.
            // On failure, disposed remains set but a later Dispose retries here.
            subscriptions.Clear(DateTimeOffset.UtcNow);
            subscriptions.CloseOutputScratch();
            var cts = samplingCts;
            samplingCts = null;
            cts?.Dispose();
            demandSignal.Dispose();
            processFieldOwner.Dispose();
            sampleGate.Dispose();
            Volatile.Write(ref ownedResourcesDisposed, 1);
        }
    }

    private async Task<ResourceBreakdownCapture> ReadSnapshotCoreAsync(
        ResourceBreakdownSampleRequest request,
        ProcessCpuDeltaTracker cpuDeltaTracker,
        CancellationToken cancellationToken,
        bool requireHostedHardwareObservation, TimeSpan? gpuAllocationInterval = null,
        IDictionary<string, RuntimeProcessAttributionCatalogLease>? retainedCatalogs = null)
    {
        var cleanMetricIds = request.MetricIds
            .Where(IsSupportedMetric)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var queriedPublicationKeys = ImmutableHashSet.CreateBuilder<string>(StringComparer.OrdinalIgnoreCase);
        var publicationDatasetIds = request.PublicationDatasetIds.ToHashSet(
            StringComparer.OrdinalIgnoreCase);
        var capturesGpuDataset = publicationDatasetIds.Contains(
                SamplingDatasetIds.ProcessGpuUsage)
            || publicationDatasetIds.Contains(
                SamplingDatasetIds.ProcessGpuVram)
            || cleanMetricIds.Any(IsGpuMetric)
            || cleanMetricIds.Any(static id => ProcessMetricFieldRegistry.Find(id) is { } field
                && (field.GpuIndex is not null || field.DatasetId == ProcessMetricDatasetIds.GpuResidency));
        IReadOnlyList<WindowsGpuAdapter> adapters = capturesGpuDataset
            ? gpuAdapterInventory().Adapters
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
                SchedulingProcessMetricMask.CpuUsage)
            || cleanMetricIds.Any(static id => ProcessMetricFieldRegistry.Find(id)?.Code == ProcessMetricFieldCode.CpuUsage);
        var needsDisk = cleanMetricIds.Any(IsDiskMetric);
        var needsNetwork = cleanMetricIds.Any(IsNetworkMetric);
        var definitions = cleanMetricIds.Select(ProcessMetricFieldRegistry.Find).OfType<ProcessMetricFieldDefinition>().ToArray();
        var needsObservedNetwork = definitions.Any(static field => field.DatasetId == SamplingDatasetIds.ProcessObservedNetworkTraffic);
        var needsObservedDisk = definitions.Any(static field => field.DatasetId == SamplingDatasetIds.ProcessObservedPhysicalDiskIo);
        var needsPfnDetails = cleanMetricIds.Contains(PhysicalMemoryResidualFrame.MetricId);
        var basicMask = definitions.Aggregate(ProcessMetricReadMask.None, static (mask, field) => mask | field.ReadMask);
        if (needsPfnDetails || cleanMetricIds.Any(IsSupportedBarMetric) || request.SchedulingMetricMask != SchedulingProcessMetricMask.None)
            basicMask |= ProcessMetricReadMask.Memory | ProcessMetricReadMask.PrivateWorkingSet;
        var processBatch = CaptureProcessSamples(
            needsCpu,
            request.ProcessDetailLevel,
            cpuDeltaTracker,
            basicMask,
            preserveProductIdentity: request.IncludeProcessMemoryFacts || definitions.Length == 0 || definitions.Length != cleanMetricIds.Length
                || request.SchedulingMetricMask != SchedulingProcessMetricMask.None || publishesProcessAttribution);
        var processCapturedAt = processBatch.CapturedAt;
        if (processBatch.Status != SamplingObservationStatus.Current)
        {
            throw new InvalidOperationException(
                "The process inventory did not produce a complete current observation.");
        }
        if (definitions.Any(static field => field.DatasetId == ProcessMetricDatasetIds.BasicMetrics))
            queriedPublicationKeys.Add(ProcessMetricDatasetIds.BasicMetrics);
        var processSamples = processBatch.Samples;
        var ioRates = basicMask.HasFlag(ProcessMetricReadMask.Io)
            ? (ReferenceEquals(cpuDeltaTracker, hostedProcessCpuDeltaTracker) ? processFieldOwner.HostedIo : processFieldOwner.DirectIo)
                .Prepare(FieldProjectionSamples(processSamples)) : null;
        ProcessAttributionSnapshot? processAttribution = null;
        if (cleanMetricIds.Any(IsSupportedBarMetric) || RequiresProcessAttribution(request))
        {
            processAttribution = await CreateProcessAttributionSnapshotAsync(processSamples, cancellationToken, retainedCatalogs);
        }
        var hardwareRequest = CreateHardwareRequest(request with { MetricIds = cleanMetricIds });
        HardwareMetricSnapshot? hardwareSnapshot = null;
        if (!hardwareRequest.IsEmpty)
        {
            hardwareSnapshot = await ReadHardwareSnapshotAsync(
                hardwareRequest,
                cancellationToken,
                requireHostedHardwareObservation,
                allowMissingSnapshot: !cleanMetricIds.Any(IsSupportedBarMetric));
        }
        var needsGpuUsage = cleanMetricIds.Any(static id => TryParseGpuMetric(id, out _, out var kind) && kind == "usage");
        var gpuAttribution = adapters.Count > 0 && needsGpuUsage
            ? processGpuReader.ReadBreakdownSnapshot(adapters, processSamples.Where(static sample => sample.StartKey is > 0)
                .Select(static sample => new ProcessInstanceKey(sample.ProcessId, sample.StartKey!.Value)).ToArray())
            : ProcessGpuBreakdownRead.NotRequested;
        ResourceManager.ReadWrite.Native.Pdh.NativePdhReadResult? pdhRead = capturesGpuDataset
            && (needsGpuUsage || definitions.Any(static field => field.DatasetId == ProcessMetricDatasetIds.GpuResidency)) ? processGpuReader.SnapshotSource.Read(
            WindowsGpuAdapterIdentity.ComputeTopologyFingerprint(adapters)) : null;
        if (capturesGpuDataset)
        {
            var adapterKeys = adapters.Select(static adapter => (adapter.Index, Luid: WindowsGpuAdapterIdentity.Pack(adapter.Luid))).ToHashSet();
            var discovered = ProcessGpuResourceFieldRegistry.Describe(adapterKeys, pdhRead?.Snapshot);
            Volatile.Write(ref gpuResourceFieldCatalog, discovered.Concat(ResourceFactFields.Where(field =>
                field.GpuIndex is { } index && field.GpuAdapterLuid is { } luid && adapterKeys.Contains((index, luid))))
                .DistinctBy(static field => field.Name, StringComparer.OrdinalIgnoreCase).ToArray());
        }
        var networkAttribution = needsNetwork || needsObservedNetwork
            ? ReadNetworkAttribution(
                CreateNetworkAttributionRequest(
                    hardwareSnapshot,
                    processSamples,
                    needsObservedNetwork
                        ? needsNetwork ? NetworkAttributionReadMode.LegacyAndObservedTraffic : NetworkAttributionReadMode.ObservedTraffic
                        : NetworkAttributionReadMode.Legacy))
            : NetworkAttributionSnapshot.NotRequested;
        if (needsObservedNetwork)
            queriedPublicationKeys.Add(ProcessMetricDatasetIds.ObservedNetworkTraffic);
        var diskAttribution = needsDisk || needsObservedDisk
            ? physicalDiskIoAttributionReader.Read(needsObservedDisk)
            : PhysicalDiskIoAttributionSnapshot.NotRequested;
        if (needsObservedDisk)
            queriedPublicationKeys.Add(ProcessMetricDatasetIds.ObservedPhysicalDiskIo);

        // The existing subscription plan owns this work. Capture once and bind all physical-private
        // projections in this sample to the same immutable window; endpoint reads never enter here.
        var needsPhysicalResidual = cleanMetricIds.Contains("memory.usage", StringComparer.OrdinalIgnoreCase)
            || request.IncludeProcessMemoryFacts && request.ProcessMemoryFactIds.Contains(
                "process_all_private_working_set_actual", StringComparer.OrdinalIgnoreCase);
        var capturedResiduals = needsPhysicalResidual && residualBreakdownProvider is IResourceResidualCaptureSource source
            ? source.CaptureResiduals(cancellationToken) : residualBreakdownProvider;
        var bars = new List<ResourceBreakdownBar>();
        if (requireHostedHardwareObservation && NeedsGpuAllocations(request) && gpuAllocations is not null)
            await gpuAllocations.CaptureAsync(GpuAllocationProcesses(processSamples), gpuAllocationInterval ?? CurrentDirectCaptureTiming().DefaultInterval, cancellationToken);
        var allocationReading = NeedsGpuAllocations(request)
            ? ReadGpuAllocations(processSamples)
            : null;
        var baseScorePlan = processAttribution?.BaseScorePlan ?? runtimePlanProvider.Current.BaseScore;
        foreach (var metricId in cleanMetricIds.Where(IsSupportedBarMetric))
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
                networkAttribution,
                allocationReading,
                capturedResiduals);
            if (bar is not null)
            {
                bars.Add(bar);
            }
        }

        var processFieldParts = new List<ProcessMetricFieldSnapshot>();
        if (needsObservedDisk)
        {
            var diskInventory = new ProcessMetricFieldSnapshot(processCapturedAt, null, [], FieldProjectionSamples(processSamples)
                .Select(row => new ProcessMetricFieldValues(row.ProcessId, row.StartKey?.ToString(System.Globalization.CultureInfo.InvariantCulture), [])).ToArray());
            processFieldParts.Add(WindowsPhysicalDiskProcessFields.Project(cleanMetricIds, diskInventory, diskAttribution.ObservedTraffic));
        }
        ProcessMetricFieldSnapshot? engineFields = null;
        if (definitions.Any(static field => field.DatasetId == ProcessMetricDatasetIds.GpuResidency))
        {
            var gpuInventory = new ProcessMetricFieldSnapshot(processCapturedAt, null, [], processSamples.Select(sample =>
                new ProcessMetricFieldValues(sample.ProcessId, sample.StartKey?.ToString(System.Globalization.CultureInfo.InvariantCulture), [])).ToArray());
            engineFields = pdhRead is not null
                ? PdhProcessGpuReader.ProjectResidencyFields(pdhRead.Value, gpuInventory)
                : processGpuReader.ReadResidencyFields(gpuInventory);
            if (cleanMetricIds.Contains(WindowsPdhProcessGpuFields.FieldName, StringComparer.OrdinalIgnoreCase))
                processFieldParts.Add(engineFields);
            queriedPublicationKeys.Add(ProcessMetricDatasetIds.GpuResidency);
        }
        if (needsObservedNetwork) processFieldParts.Add(CreateProcessNetworkFields(cleanMetricIds, processSamples, networkAttribution.ObservedTraffic));
        if (definitions.Any(static field => field.DatasetId == SamplingDatasetIds.ProcessBasicMetrics))
            processFieldParts.Add(CreateProcessBasicFields(definitions, processSamples, processCapturedAt, processBatch.CpuInterval, ioRates));
        var gpuFacts = CreateGpuResourceFacts(definitions.Where(ProcessGpuResourceFieldRegistry.IsBound).ToArray(),
            processSamples, adapters, pdhRead, engineFields, allocationReading, hardwareSnapshot);
        if (gpuFacts.Fields.Fields.Count > 0)
        {
            processFieldParts.Add(gpuFacts.Fields);
            queriedPublicationKeys.UnionWith(gpuFacts.Fields.Fields.Select(static field => field.DatasetId));
        }
        var fieldSnapshot = processFieldParts.Count > 0 ? ProcessMetricFieldSnapshot.Merge(processFieldParts) : null;
        var pfnFrame = needsPfnDetails ? PhysicalMemoryPfnCapture(
            processSamples.Where(static process => process.HasPrivateWorkingSetBytes && process.StartKey is > 0)
                .Aggregate(0UL, static (sum, process) => checked(sum + (ulong)Math.Max(0, process.PrivateWorkingSetBytes))),
            cancellationToken) : null;
        var snapshot = new ResourceBreakdownSnapshot(DateTimeOffset.Now, bars)
        {
            PhysicalMemoryResidual = pfnFrame,
            ProcessFields = fieldSnapshot,
            ProcessMemoryFacts = request.IncludeProcessMemoryFacts && processAttribution is not null
                ? CreateProcessMeasureFacts(definitions.Where(field => request.ProcessMemoryFactIds.Contains(field.Name, StringComparer.OrdinalIgnoreCase)).ToArray(),
                    fieldSnapshot, hardwareSnapshot, networkAttribution.ObservedTraffic, diskAttribution.ObservedTraffic, capturedResiduals)
                    .Concat(gpuFacts.Facts.Where(fact => request.ProcessMemoryFactIds.Contains(fact.MetricId, StringComparer.OrdinalIgnoreCase))).ToArray() : []
        };
        SchedulingProcessFactSnapshot? schedulingSnapshot = null;
        if (request.SchedulingMetricMask != SchedulingProcessMetricMask.None
            || publishesProcessInventory
            || publishesProcessAttribution)
        {
            var generation = checked((ulong)Interlocked.Increment(
                ref schedulingProcessGeneration));
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
                    gpuInventory),
                processBatch,
                processAttribution,
                generation,
                snapshot.CapturedAt.UtcTicks,
                allocationReading: allocationReading);
        }
        return new ResourceBreakdownCapture(snapshot, schedulingSnapshot)
        {
            IoRates = ioRates,
            QueriedPublicationKeys = queriedPublicationKeys.ToImmutable()
        };
    }

    private static void ClearIoHistory(ResourceBreakdownSampleRequest request, ProcessIoRateTracker tracker)
    {
        if (request.MetricIds.Any(static name => ProcessMetricFieldRegistry.Find(name)?.ReadMask.HasFlag(ProcessMetricReadMask.Io) == true))
            tracker.Clear();
    }

    private async Task<HardwareMetricSnapshot?> ReadHardwareSnapshotAsync(
        MetricSampleRequest request,
        CancellationToken cancellationToken,
        bool requireHostedObservation,
        bool allowMissingSnapshot = false)
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
        var snapshot = observationSource.ReadLatest(request);
        if (snapshot is null && !allowMissingSnapshot)
            throw new InvalidOperationException("The hosted hardware dataset has not published a snapshot yet.");
        return snapshot;
    }

    private sealed class ObservationSubscriptionLease(
        WindowsResourceBreakdownSampler owner, string sourceKey, IDisposable? fieldDeliveryLease = null) : IDisposable
    {
        private int disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            try { owner.ReleaseObservationLease(sourceKey); }
            finally { fieldDeliveryLease?.Dispose(); }
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


