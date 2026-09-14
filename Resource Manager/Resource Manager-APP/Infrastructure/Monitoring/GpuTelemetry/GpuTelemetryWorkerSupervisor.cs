using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Hosting;
using ResourceManager.App.Application.Monitoring;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;

namespace ResourceManager.App.Infrastructure.Monitoring.GpuTelemetry;

public sealed class GpuTelemetryWorkerSupervisor(
    GpuTelemetryLocalProbe localProbe,
    HostManagerSamplingSubscriptionOwner subscriptionOwner,
    ILogger<GpuTelemetryWorkerSupervisor> logger) : IGpuTelemetryWorkerClient, IGpuTelemetryPushSource, IHostedService, IDisposable
{
    private readonly object gate = new();
    private readonly SemaphoreSlim demandSignal = new(0, 1);
    private readonly SemaphoreSlim refreshGate = new(1, 1);
    private readonly NativeItemSamplingSubscriptionTracker<GpuTelemetryWorkerRequest> subscriptions = new(
        subscriptionOwner,
        roleId: 4,
        CreateRequestKey,
        static request => request.CounterIds,
        CreateWorkerRequest,
        coalesceItemsIntoLatestCapture: false);
    private readonly SamplingPublicationSignal publicationSignal = new();
    private long sequence;
    private long nextSubscriptionLeaseId;
    private CancellationTokenSource? samplingCts;
    private Task? samplingTask;
    private GpuTelemetryWorkerSnapshot latest = GpuTelemetryWorkerSnapshot.Unavailable("GPU telemetry worker is not configured.");
    private int disposed;

    public GpuTelemetryWorkerSnapshot GetLatestSnapshot()
    {
        lock (gate)
        {
            return latest.WithStaleAge(DateTimeOffset.UtcNow);
        }
    }

    public async IAsyncEnumerable<GpuTelemetryWorkerSnapshot> SubscribeAsync(
        string subscriptionId,
        GpuTelemetryWorkerRequest request,
        TimeSpan interval,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subscriptionId);
        ArgumentNullException.ThrowIfNull(request);
        if (request.CounterIds.Length == 0)
        {
            yield break;
        }
        var sourceKey = $"push:{Interlocked.Increment(ref nextSubscriptionLeaseId)}:{subscriptionId.Trim()}";
        using var publication = publicationSignal.Subscribe(request.CounterIds);
        subscriptions.TrackPersistent(sourceKey, request, DateTimeOffset.UtcNow, interval);
        SignalDemand();
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await publication.WaitAsync(cancellationToken);
                yield return ProjectCurrentSnapshot(
                    GetLatestSnapshot(),
                    request.CounterIds);
            }
        }
        finally
        {
            subscriptions.Remove(sourceKey, DateTimeOffset.UtcNow);
            SignalDemand();
        }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        samplingCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        samplingTask = Task.Run(() => RunSamplingLoopAsync(samplingCts.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        var task = samplingTask;
        if (task is null)
        {
            return;
        }

        samplingCts?.Cancel();
        SignalDemand();
        try
        {
            await task.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task RunSamplingLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var plan = subscriptions.CreatePlan(DateTimeOffset.UtcNow);
            if (!plan.IsActive)
            {
                await WaitForDemandAsync(cancellationToken);
                continue;
            }

            if (plan.Request is null)
            {
                await WaitForDemandOrDelayAsync(plan.Delay, cancellationToken);
                continue;
            }

            var outcome = await RefreshCacheAsync(
                plan.Request,
                plan.OwnerToken,
                cancellationToken);

            NativeItemSamplingSubscriptionCompletion completion;
            if (outcome == RefreshOutcome.Published)
            {
                completion = subscriptions.MarkSampled(
                    plan.Request,
                    DateTimeOffset.UtcNow);
            }
            else if (outcome == RefreshOutcome.Failed)
            {
                completion = subscriptions.MarkFailed(
                    plan.Request,
                    DateTimeOffset.UtcNow);
            }
            else
            {
                completion = subscriptions.MarkSkipped(
                    plan.Request,
                    DateTimeOffset.UtcNow);
            }
            if (outcome == RefreshOutcome.Canceled)
            {
                return;
            }
            await WaitForDemandOrDelayAsync(completion.Delay, cancellationToken);
        }
    }

    private async Task WaitForDemandAsync(CancellationToken cancellationToken)
    {
        try
        {
            await demandSignal.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task WaitForDemandOrDelayAsync(
        TimeSpan delay,
        CancellationToken cancellationToken)
    {
        if (delay < TimeSpan.Zero)
        {
            throw new InvalidOperationException(
                "Native GPU telemetry sampling returned a past next-wake time.");
        }

        try
        {
            await demandSignal.WaitAsync(delay, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task<RefreshOutcome> RefreshCacheAsync(
        GpuTelemetryWorkerRequest request,
        SamplingOwnerToken ownerToken,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!await refreshGate.WaitAsync(0, cancellationToken))
            {
                return RefreshOutcome.SkippedBusy;
            }

            try
            {
                return await RefreshCacheCoreAsync(
                    request,
                    ownerToken,
                    cancellationToken);
            }
            finally
            {
                refreshGate.Release();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return RefreshOutcome.Canceled;
        }
    }

    private async Task<RefreshOutcome> RefreshCacheCoreAsync(
        GpuTelemetryWorkerRequest request,
        SamplingOwnerToken ownerToken,
        CancellationToken cancellationToken)
    {
        try
        {
            var snapshot = await localProbe.CaptureAsync(request, cancellationToken);
            var published = ownerToken.TryPublish(() =>
            {
                var committed = snapshot with
                {
                    Sequence = Interlocked.Increment(ref sequence)
                };
                lock (gate)
                {
                    latest = MergeCurrentSnapshot(
                        latest,
                        committed,
                        request.CounterIds,
                        topologyAuthoritative: true);
                }
                publicationSignal.Publish(request.CounterIds);
            });
            return published
                ? RefreshOutcome.Published
                : RefreshOutcome.OwnerRevoked;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "GPU telemetry local probe failed.");
            var published = ownerToken.TryPublish(() =>
            {
                var failed = new GpuTelemetryWorkerSnapshot(
                    GpuTelemetryWorkerSnapshot.CurrentProtocolVersion,
                    Interlocked.Increment(ref sequence),
                    DateTimeOffset.UtcNow,
                    0,
                    GpuTelemetryWorkerStatus.Failed,
                    [],
                    [
                        new GpuTelemetryProviderState(
                            "worker.supervisor",
                            GpuTelemetryWorkerStatus.Failed,
                            ex.Message,
                            true,
                            request.DetailLevel >= GpuTelemetryWorkerDetailLevel.Profiling,
                            0)
                    ],
                    "GPU telemetry refresh failed.");
                lock (gate)
                {
                    latest = MergeCurrentSnapshot(
                        latest,
                        failed,
                        request.CounterIds,
                        topologyAuthoritative: false);
                }
                publicationSignal.Publish(request.CounterIds);
            });
            return published
                ? RefreshOutcome.Failed
                : RefreshOutcome.OwnerRevoked;
        }
    }

    private enum RefreshOutcome
    {
        Published,
        Failed,
        SkippedBusy,
        Canceled,
        OwnerRevoked
    }

    internal static GpuTelemetryWorkerSnapshot MergeCurrentSnapshot(
        GpuTelemetryWorkerSnapshot current,
        GpuTelemetryWorkerSnapshot update,
        IReadOnlyCollection<string> updatedCounterIds,
        bool topologyAuthoritative = true)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(update);
        ArgumentNullException.ThrowIfNull(updatedCounterIds);
        var updatedIds = updatedCounterIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var replaceTopology = topologyAuthoritative
            && update.Status == GpuTelemetryWorkerStatus.Online;
        var currentAdapters = current.Adapters.ToDictionary(
            static adapter => adapter.AdapterIndex);
        var updateAdapters = update.Adapters.ToDictionary(
            static adapter => adapter.AdapterIndex);
        var adapterIndexes = (replaceTopology
                ? updateAdapters.Keys
                : currentAdapters.Keys.Concat(updateAdapters.Keys))
            .Distinct()
            .Order()
            .ToArray();
        var mergedAdapters = new List<GpuTelemetryAdapterSnapshot>(adapterIndexes.Length);
        foreach (var adapterIndex in adapterIndexes)
        {
            currentAdapters.TryGetValue(adapterIndex, out var currentAdapter);
            updateAdapters.TryGetValue(adapterIndex, out var updateAdapter);
            var sameAdapter = currentAdapter is not null
                && updateAdapter is not null
                && currentAdapter.AdapterIdentity == updateAdapter.AdapterIdentity;
            var counters = (sameAdapter || (!replaceTopology && updateAdapter is null)
                    ? currentAdapter?.Counters ?? []
                    : [])
                .ToDictionary(
                    static counter => counter.CounterId,
                    StringComparer.OrdinalIgnoreCase);
            foreach (var counterId in updatedIds)
            {
                if (counters.TryGetValue(counterId, out var prior))
                {
                    counters[counterId] = prior with { Value = null };
                }
            }
            foreach (var counter in updateAdapter?.Counters ?? [])
            {
                counters[counter.CounterId] = counter;
            }
            mergedAdapters.Add(new GpuTelemetryAdapterSnapshot(
                adapterIndex,
                updateAdapter?.AdapterIdentity ?? currentAdapter?.AdapterIdentity ?? 0,
                updateAdapter?.AdapterName ?? currentAdapter?.AdapterName,
                updateAdapter?.VendorId ?? currentAdapter?.VendorId,
                updateAdapter?.Architecture ?? currentAdapter?.Architecture,
                counters.Values
                    .OrderBy(static counter => counter.CounterId, StringComparer.OrdinalIgnoreCase)
                    .ToArray()));
        }
        return update with { Adapters = mergedAdapters.ToArray() };
    }

    internal static GpuTelemetryWorkerSnapshot ProjectCurrentSnapshot(
        GpuTelemetryWorkerSnapshot snapshot,
        IReadOnlyCollection<string> counterIds)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(counterIds);
        var requested = counterIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return snapshot with
        {
            Adapters = snapshot.Adapters
                .Select(adapter => adapter with
                {
                    Counters = adapter.Counters
                        .Where(counter => requested.Contains(counter.CounterId))
                        .ToArray()
                })
                .ToArray()
        };
    }

    private static string CreateRequestKey(GpuTelemetryWorkerRequest request)
    {
        return $"{(byte)request.DetailLevel}:{string.Join('\n', request.CounterIds)}";
    }

    private static GpuTelemetryWorkerRequest? CreateWorkerRequest(
        IReadOnlyList<string> counterIds,
        IReadOnlyList<NativeItemSamplingSubscriptionSourceView<GpuTelemetryWorkerRequest>> sources)
    {
        if (counterIds.Count == 0)
        {
            return null;
        }

        var dueCounters = counterIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var relevantSources = sources
            .Where(source => source.ItemIds.Any(dueCounters.Contains))
            .ToArray();
        return GpuTelemetryWorkerRequest.Create(
            counterIds,
            relevantSources.Length == 0
                ? GpuTelemetryWorkerDetailLevel.LowIntrusion
                : relevantSources.Max(static source => source.Request.DetailLevel),
            relevantSources.Length == 0
                ? 250
                : relevantSources.Max(static source => source.Request.TimeoutMilliseconds));
    }

    private void SignalDemand()
    {
        if (demandSignal.CurrentCount != 0)
        {
            return;
        }

        try
        {
            demandSignal.Release();
        }
        catch (SemaphoreFullException)
        {
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        var cts = Interlocked.Exchange(ref samplingCts, null);
        try
        {
            cts?.Cancel();
        }
        finally
        {
            cts?.Dispose();
        }

        demandSignal.Dispose();
        refreshGate.Dispose();
        lock (gate)
        {
            latest = GpuTelemetryWorkerSnapshot.Unavailable("GPU telemetry worker supervisor was disposed.");
        }
    }
}
