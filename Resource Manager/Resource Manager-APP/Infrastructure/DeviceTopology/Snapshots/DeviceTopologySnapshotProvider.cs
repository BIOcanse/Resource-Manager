using System.Runtime.CompilerServices;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ResourceManager.App.Application.DeviceTopology;
using ResourceManager.App.Domain.DeviceTopology;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;

namespace ResourceManager.App.Infrastructure.DeviceTopology.Snapshots;

public sealed class DeviceTopologySnapshotProvider(
    IDeviceTopologyReader reader,
    IDeviceTopologySnapshotStore store,
    DeviceTopologySemanticComparer semanticComparer,
    HostManagerSamplingSubscriptionOwner samplingSubscriptionOwner,
    ILogger<DeviceTopologySnapshotProvider> logger)
    : IDeviceTopologySnapshotProvider, IHostedService, IDisposable
{
    internal const string SnapshotItemId = "deviceTopology.snapshot";
    private readonly SemaphoreSlim demandSignal = new(0, 1);
    private readonly SemaphoreSlim sampleGate = new(1, 1);
    private readonly CurrentValuePublicationSignal publicationSignal = new();
    private readonly NativeItemSamplingSubscriptionTracker<DeviceTopologySampleRequest> subscriptions = new(
        samplingSubscriptionOwner,
        2,
        static request => request.SourceKey,
        static _ => [SnapshotItemId],
        static (itemIds, _) => itemIds.Contains(SnapshotItemId, StringComparer.OrdinalIgnoreCase)
            ? new DeviceTopologySampleRequest("provider")
            : null,
        coalesceItemsIntoLatestCapture: true);
    private DeviceTopologySnapshotState state = DeviceTopologySnapshotState.Warming;
    private string? semanticHash;
    private CancellationTokenSource? samplingCts;
    private Task? samplingTask;
    private long contentGeneration;
    private long stateRevision;
    private long nextLeaseId;
    private string? failureFingerprint;
    private int disposed;

    public async IAsyncEnumerable<DeviceTopologySnapshotState> SubscribeAsync(
        string subscriptionId,
        TimeSpan interval,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subscriptionId);
        cancellationToken.ThrowIfCancellationRequested();
        using var publication = publicationSignal.Subscribe();
        var sourceKey = $"lease:{Interlocked.Increment(ref nextLeaseId)}:{subscriptionId.Trim()}";
        subscriptions.TrackPersistent(
            new DeviceTopologySampleRequest(sourceKey),
            DateTimeOffset.UtcNow,
            interval);
        SignalDemand();
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await publication.WaitAsync(cancellationToken);
                yield return ReadState();
            }
        }
        finally
        {
            subscriptions.Remove(sourceKey, DateTimeOffset.UtcNow);
            SignalDemand();
        }
    }

    public DeviceTopologySnapshotState ReadState()
    {
        return Volatile.Read(ref state);
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var persisted = await store.LoadAsync(cancellationToken);
        if (persisted is not null)
        {
            semanticHash = persisted.SemanticHash;
            contentGeneration = Math.Max(1, persisted.ContentGeneration);
            stateRevision = 1;
            Publish(new DeviceTopologySnapshotState(
                DeviceTopologySnapshotState.CurrentSchemaVersion,
                DeviceTopologySnapshotStatus.Ready,
                persisted.Snapshot,
                contentGeneration,
                stateRevision,
                DeviceTopologySnapshotSource.Persisted,
                persisted.Snapshot.CapturedAt,
                null,
                null,
                []));
        }

        samplingCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        samplingTask = Task.Run(() => RunSamplingLoopAsync(samplingCts.Token), CancellationToken.None);
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
        catch (OperationCanceledException) when (task.IsCompleted)
        {
        }

        subscriptions.Clear(DateTimeOffset.UtcNow);
        _ = Interlocked.CompareExchange(ref samplingTask, null, task);
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

        var task = Interlocked.Exchange(ref samplingTask, null);
        if (task is not null)
        {
            subscriptions.Clear(DateTimeOffset.UtcNow);
        }
        demandSignal.Dispose();
        sampleGate.Dispose();
    }

    internal Task<DeviceTopologySampleCompletion> SampleNowAsync(
        CancellationToken cancellationToken)
    {
        return TrySampleAsync(null, cancellationToken);
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

            var completion = await TrySampleAsync(
                plan.OwnerToken,
                cancellationToken);
            var completedAt = DateTimeOffset.UtcNow;
            var subscriptionCompletion = completion switch
            {
                DeviceTopologySampleCompletion.Sampled =>
                    subscriptions.MarkSampled(plan.Request, completedAt),
                DeviceTopologySampleCompletion.Failed =>
                    subscriptions.MarkFailed(plan.Request, completedAt),
                DeviceTopologySampleCompletion.SkippedBusy or
                DeviceTopologySampleCompletion.Canceled =>
                    subscriptions.MarkSkipped(plan.Request, completedAt),
                _ => throw new InvalidOperationException(
                    "Device topology sampling returned an unknown completion.")
            };
            await WaitForDemandOrDelayAsync(
                subscriptionCompletion.Delay,
                cancellationToken);
        }
    }

    private async Task<DeviceTopologySampleCompletion> TrySampleAsync(
        SamplingOwnerToken? ownerToken,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!await sampleGate.WaitAsync(0, cancellationToken))
            {
                return DeviceTopologySampleCompletion.SkippedBusy;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return DeviceTopologySampleCompletion.Canceled;
        }

        var attemptedAt = DateTimeOffset.UtcNow;
        if (ownerToken is { IsActive: false })
        {
            sampleGate.Release();
            return DeviceTopologySampleCompletion.Canceled;
        }

        try
        {
            var snapshot = reader.ReadSnapshot();
            var nextHash = semanticComparer.ComputeHash(snapshot);
            var changed = false;
            DeviceTopologySnapshotState? nextState = null;
            if (!TryPublish(ownerToken, () =>
            {
                changed = !string.Equals(semanticHash, nextHash, StringComparison.Ordinal);
                if (changed)
                {
                    semanticHash = nextHash;
                    contentGeneration = Interlocked.Increment(ref contentGeneration);
                }

                var current = ReadState();
                var publishedSnapshot = changed || current.Snapshot is null
                    ? snapshot
                    : current.Snapshot;
                nextState = current with
                {
                    State = DeviceTopologySnapshotStatus.Ready,
                    Snapshot = publishedSnapshot,
                    ContentGeneration = contentGeneration,
                    StateRevision = Interlocked.Increment(ref stateRevision),
                    Source = DeviceTopologySnapshotSource.Live,
                    LastSuccessAt = snapshot.CapturedAt,
                    LastAttemptAt = attemptedAt,
                    FailureCode = null,
                    AttemptDiagnostics = []
                };
                Publish(nextState);
                failureFingerprint = null;
                publicationSignal.Publish();
            }))
            {
                return DeviceTopologySampleCompletion.Canceled;
            }

            if (changed)
            {
                try
                {
                    await store.SaveAsync(new DeviceTopologyPersistedSnapshot(
                        DeviceTopologySnapshotState.CurrentSchemaVersion,
                        string.Empty,
                        contentGeneration,
                        nextHash,
                        nextState!.Snapshot!), cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Unable to persist the device topology snapshot.");
                }
            }

            return DeviceTopologySampleCompletion.Sampled;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return DeviceTopologySampleCompletion.Canceled;
        }
        catch (DeviceTopologyIncompleteSourcesException ex)
        {
            return PublishFailure(
                ownerToken,
                ex,
                attemptedAt,
                ex.Diagnostics[0].Code,
                ex.Diagnostics)
                ? DeviceTopologySampleCompletion.Failed
                : DeviceTopologySampleCompletion.Canceled;
        }
        catch (Exception ex)
        {
            return PublishFailure(
                ownerToken,
                ex,
                attemptedAt,
                "device-topology-read-failed",
                [])
                ? DeviceTopologySampleCompletion.Failed
                : DeviceTopologySampleCompletion.Canceled;
        }
        finally
        {
            sampleGate.Release();
        }
    }

    private bool PublishFailure(
        SamplingOwnerToken? ownerToken,
        Exception exception,
        DateTimeOffset attemptedAt,
        string failureCode,
        IReadOnlyList<DeviceTopologySourceDiagnostic> diagnostics)
    {
        return TryPublish(ownerToken, () =>
        {
            var fingerprint = $"{exception.GetType().FullName}:{exception.HResult}:{failureCode}";
            if (!string.Equals(failureFingerprint, fingerprint, StringComparison.Ordinal))
            {
                logger.LogWarning(
                    exception,
                    "Device topology background sample failed with {FailureCode}; sources: {SourceIds}.",
                    failureCode,
                    string.Join(",", diagnostics.Select(static diagnostic => diagnostic.SourceId)));
            }
            else
            {
                logger.LogDebug(
                    "Device topology source failure remains unchanged: {FailureCode}.",
                    failureCode);
            }

            failureFingerprint = fingerprint;
            var current = ReadState();
            Publish(current with
            {
                State = DeviceTopologySnapshotStatus.Failed,
                StateRevision = Interlocked.Increment(ref stateRevision),
                LastAttemptAt = attemptedAt,
                FailureCode = failureCode,
                AttemptDiagnostics = diagnostics.ToArray()
            });
            publicationSignal.Publish();
        });
    }

    private static bool TryPublish(SamplingOwnerToken? ownerToken, Action publish)
    {
        if (ownerToken is null)
        {
            publish();
            return true;
        }

        return ownerToken.TryPublish(publish);
    }

    private void Publish(DeviceTopologySnapshotState next)
    {
        Volatile.Write(ref state, next);
    }

    private void SignalDemand()
    {
        if (demandSignal.CurrentCount == 0)
        {
            try
            {
                demandSignal.Release();
            }
            catch (SemaphoreFullException)
            {
            }
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

    private async Task WaitForDemandOrDelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        if (delay < TimeSpan.Zero)
        {
            throw new InvalidOperationException(
                "Native device-topology sampling returned a past next-wake time.");
        }

        try
        {
            await demandSignal.WaitAsync(delay, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private sealed record DeviceTopologySampleRequest(string SourceKey);
}

internal enum DeviceTopologySampleCompletion : byte
{
    Sampled = 1,
    Failed = 2,
    SkippedBusy = 3,
    Canceled = 4
}
