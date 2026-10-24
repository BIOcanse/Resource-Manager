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
    InteractiveDesktopObservationStore? interactiveDesktops = null) :
    IResourceBreakdownSampler,
    IResourceBreakdownObservationSource,
    IResourceBreakdownPushSource,
    ISchedulingProcessFactSource,
    ISchedulingProcessFactObservationSource,
    IRuntimeSoftwareObservationSource,
    IHostedService,
    IDisposable
{
    private const int SamplingRoleId = 3;
    private readonly object lifecycleGate = new();
    private WindowsProcessFieldOwner processFieldOwner => hostedPublicationState.ProcessOwner;
    private SemaphoreSlim sampleGate => processFieldOwner.CaptureGate;
    private readonly SemaphoreSlim demandSignal = new(0, 1);
    private IWindowsProcessInventoryReader processInventoryReader { get => processFieldOwner.Inventory; set => processFieldOwner.Inventory = value; }
    private WindowsProcessBasicMetricReader processBasicMetricReader { get => processFieldOwner.BasicReader; set => processFieldOwner.BasicReader = value; }
    private readonly HostedResourcePublicationState hostedPublicationState = new(processGpuReader?.SnapshotSource);
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
        => new(owner, SamplingRoleId,
            static request => $"resource-breakdown:{request.CacheKey}", GetSubscriptionDatasetIds,
            CreateResourceBreakdownSampleRequest, coalesceItemsIntoLatestCapture: true, createOutputScratch);
    private readonly WindowsGpuAdapterOrderReader gpuAdapterReader = new();
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
        InteractiveDesktopObservationStore? interactiveDesktops = null)
        : this(metricSampler, processAttributionCatalogProvider, residualBreakdownProvider,
            physicalDiskIoAttributionReader, networkAttributionReader, runtimePlanProvider,
            samplingSubscriptionOwner, processGpuReader, logger, gpuAllocations, interactiveDesktops)
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
        Func<ulong, uint, INativeSamplingOutputScratch>? createOutputScratch = null)
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
        if (createOutputScratch is not null)
            subscriptions = CreateSubscriptions(samplingSubscriptionOwner, createOutputScratch);
        this.processInventoryReader = processInventoryReader
            ?? throw new ArgumentNullException(nameof(processInventoryReader));
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
        return AcquireHostedSubscription(sourceKey, subscriptionRequest, refreshInterval,
            NativeSamplingSubscriptionSourceFlags.None, null);
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
            fieldSubscriptionDelivery.RetireAll();
            pendingFieldCollectionResult = null;
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
        var ownerRevoked = false;
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
                    "Resource breakdown background sampling iteration failed; the owner will retry.");
            }, TimeSpan.FromSeconds(1));
        try
        {
            await worker.RunAsync((plan, token) => CollectAndPublishAsync(ownerToken, plan, token),
                cancellationToken, planCreated: plan =>
                {
                    pendingFieldCollectionResult = null;
                    if (plan.DueItemIds.Count == 0 && !hostedPublicationState.TryAcceptSchedule(
                            ownerToken, plan.OwnerToken, plan.Schedule))
                    {
                        ownerRevoked = true;
                        throw new OperationCanceledException(cancellationToken);
                    }
                    if (plan.Request is null)
                        fieldSubscriptionDelivery.Deliver(plan.Triggers, null, freshOnly: false);
                }, collectionCompleted: (plan, _) =>
                {
                    var result = pendingFieldCollectionResult;
                    pendingFieldCollectionResult = null;
                    result ??= fieldSubscriptionDelivery.CaptureCompleted(plan.Triggers,
                        ImmutableHashSet<string>.Empty.WithComparer(StringComparer.OrdinalIgnoreCase), committed: false);
                    fieldSubscriptionDelivery.Deliver(plan.Triggers, result,
                        freshOnly: plan.Request is null ? true : null);
                });
        }
        catch (Exception) when (ownerRevoked) { }
    }

    private async Task<SubscriptionSampleCompletion> CollectAndPublishAsync(
        HostedResourcePublicationOwnerToken ownerToken,
        NativeItemSamplingSubscriptionPlan<ResourceBreakdownSampleRequest, SamplingOwnerToken> plan,
        CancellationToken cancellationToken)
    {
        var request = plan.Request!;
        var attempt = await TryRefreshCacheAsync(request, cancellationToken);
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
            // The worker is drained before this point. The tracker gate excludes
            // synchronous Plan, including projection and the exact output borrow.
            // On failure, disposed remains set but a later Dispose retries here.
            subscriptions.CloseOutputScratch();
            if (Interlocked.Exchange(ref ownedResourcesDisposed, 1) != 0) return;
            var cts = samplingCts;
            samplingCts = null;
            cts?.Dispose();
            demandSignal.Dispose();
            processFieldOwner.Dispose();
            sampleGate.Dispose();
        }
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
        var queriedPublicationKeys = ImmutableHashSet.CreateBuilder<string>(StringComparer.OrdinalIgnoreCase);
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
                SchedulingProcessMetricMask.CpuUsage)
            || cleanMetricIds.Any(static id => ProcessMetricFieldRegistry.Find(id)?.Code == ProcessMetricFieldCode.CpuUsage);
        var needsDisk = cleanMetricIds.Any(IsDiskMetric);
        var needsNetwork = cleanMetricIds.Any(IsNetworkMetric);
        var definitions = cleanMetricIds.Select(ProcessMetricFieldRegistry.Find).OfType<ProcessMetricFieldDefinition>().ToArray();
        var needsObservedNetwork = definitions.Any(static field => field.DatasetId == SamplingDatasetIds.ProcessObservedNetworkTraffic);
        var needsObservedDisk = definitions.Any(static field => field.DatasetId == SamplingDatasetIds.ProcessObservedPhysicalDiskIo);
        var basicMask = definitions.Aggregate(ProcessMetricReadMask.None, static (mask, field) => mask | field.ReadMask);
        if (cleanMetricIds.Any(IsSupportedBarMetric) || request.SchedulingMetricMask != SchedulingProcessMetricMask.None)
            basicMask |= ProcessMetricReadMask.Memory | ProcessMetricReadMask.PrivateWorkingSet;
        var processBatch = CaptureProcessSamples(
            needsCpu,
            request.ProcessDetailLevel,
            cpuDeltaTracker,
            basicMask,
            preserveProductIdentity: definitions.Length == 0 || definitions.Length != cleanMetricIds.Length
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
            using var attributionCatalog =
                await processAttributionCatalogProvider.AcquireCatalogAsync(
                    cancellationToken);
            processAttribution = CreateProcessAttributionSnapshot(
                processSamples,
                attributionCatalog.Catalog);
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
            ? processGpuReader.ReadBreakdownSnapshot(adapters, processSamples.Where(static sample => sample.StartKey is > 0)
                .Select(static sample => new ProcessInstanceKey(sample.ProcessId, sample.StartKey!.Value)).ToArray())
            : ProcessGpuBreakdownRead.NotRequested;
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

        var bars = new List<ResourceBreakdownBar>();
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
                allocationReading);
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
        if (definitions.Any(static field => field.DatasetId == ProcessMetricDatasetIds.GpuResidency))
        {
            var gpuInventory = new ProcessMetricFieldSnapshot(processCapturedAt, null, [], processSamples.Select(sample =>
                new ProcessMetricFieldValues(sample.ProcessId, sample.StartKey?.ToString(System.Globalization.CultureInfo.InvariantCulture), [])).ToArray());
            processFieldParts.Add(processGpuReader.ReadResidencyFields(gpuInventory));
            queriedPublicationKeys.Add(ProcessMetricDatasetIds.GpuResidency);
        }
        if (needsObservedNetwork) processFieldParts.Add(CreateProcessNetworkFields(cleanMetricIds, processSamples, networkAttribution.ObservedTraffic));
        if (definitions.Any(static field => field.DatasetId == SamplingDatasetIds.ProcessBasicMetrics))
            processFieldParts.Add(CreateProcessBasicFields(definitions, processSamples, processCapturedAt, processBatch.CpuInterval, ioRates));
        var snapshot = new ResourceBreakdownSnapshot(DateTimeOffset.Now, bars)
        {
            ProcessFields = processFieldParts.Count > 0 ? ProcessMetricFieldSnapshot.Merge(processFieldParts) : null
        };
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
        IDisposable? allocationLease,
        IDisposable? fieldDeliveryLease = null) : IDisposable
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
            Exception? fieldDeliveryReleaseError = null;
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
            try { fieldDeliveryLease?.Dispose(); }
            catch (Exception error) { fieldDeliveryReleaseError = error; }
            Volatile.Write(ref state, 2);
            if (allocationReleaseError is not null || fieldDeliveryReleaseError is not null)
                throw new AggregateException(new[] { resourceReleaseError, hardwareReleaseError, allocationReleaseError,
                    fieldDeliveryReleaseError }.OfType<Exception>());
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


