using System.Runtime.CompilerServices;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;
using ResourceManager.ReadWrite.Information;
using ResourceManager.ReadWrite.Windows.Devices.Snapshots;

namespace ResourceManager.App.Infrastructure.DeviceTopology.Snapshots;

public sealed partial class DeviceTopologySnapshotProvider
{
    internal IDisposable AcquireServiceInformationSubscription(string subscriptionId, TimeSpan interval,
        bool includeConfiguration, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subscriptionId);
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        var sourceKey = $"lease:{Interlocked.Increment(ref nextLeaseId)}:{subscriptionId.Trim()}";
        return TrackInformationDemand(new DeviceTopologySampleRequest(sourceKey,
            DeviceTopologySampleScope.Services, cancellationToken, includeConfiguration), interval);
    }
    internal async ValueTask<DeviceInformationSnapshot> CaptureServiceInformationAsync(CancellationToken cancellationToken,
        bool includeConfiguration = false)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        using var lease = samplingSubscriptionOwner.Acquire(2);
        return await ServicesInventory.CaptureInformationAsync(includeConfiguration, cancellationToken, lease.OwnerToken.TryPublish);
    }
    internal async IAsyncEnumerable<DeviceInformationSnapshot> SubscribeServicesAsync(string id, TimeSpan interval,
        bool includeConfiguration, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var publication = ServicesInventory.Publications.Subscribe(["information.services"]);
        using var lease = AcquireServiceInformationSubscription(id, interval, includeConfiguration, cancellationToken);
        yield return ServicesInventory.ReadInformation();
        while (!cancellationToken.IsCancellationRequested)
        {
            await publication.WaitAsync(cancellationToken);
            yield return ServicesInventory.ReadInformation();
        }
    }
    private async Task<DeviceTopologySampleCompletion> TrySampleServicesAsync(SamplingOwnerToken ownerToken,
        bool includeConfiguration, CancellationToken cancellationToken,
        Action<DeviceTopologySampleScope, IReadOnlySet<string>>? collectionCommitted = null)
    {
        if (!ownerToken.IsActive || cancellationToken.IsCancellationRequested) return DeviceTopologySampleCompletion.Canceled;
        if (!HasCurrentDemand(DeviceTopologySampleScope.Services)) return DeviceTopologySampleCompletion.Sampled;
        try
        {
            var fields = await ServicesInventory.CaptureInformationAsync(includeConfiguration, cancellationToken,
                action => TryPublishHosted(ownerToken, action, () => collectionCommitted?.Invoke(DeviceTopologySampleScope.Services,
                    new HashSet<string>(StringComparer.Ordinal) { ServicesItemId })));
            if (!ownerToken.IsActive) return DeviceTopologySampleCompletion.Canceled;
            return fields.ObservedAt is null ? DeviceTopologySampleCompletion.Failed : DeviceTopologySampleCompletion.Sampled;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        { return DeviceTopologySampleCompletion.Canceled; }
    }
}
