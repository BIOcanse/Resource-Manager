using System.Runtime.CompilerServices;
using ResourceManager.App.Domain.DeviceTopology;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;
using ResourceManager.ReadWrite.Windows.Devices.Snapshots;

namespace ResourceManager.App.Infrastructure.DeviceTopology.Snapshots;

public sealed partial class DeviceTopologySnapshotProvider
{
    internal IDisposable AcquireSoftwareInformationSubscription(string subscriptionId, TimeSpan interval,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subscriptionId);
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        _ = SoftwareInventory ?? throw new InvalidOperationException("No software inventory is bound to this provider.");
        var sourceKey = $"lease:{Interlocked.Increment(ref nextLeaseId)}:{subscriptionId.Trim()}";
        return TrackInformationDemand(new DeviceTopologySampleRequest(sourceKey,
            DeviceTopologySampleScope.Software, cancellationToken), interval);
    }

    internal async ValueTask<DeviceInformationSnapshot> CaptureSoftwareInformationAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        var inventory = SoftwareInventory ?? throw new InvalidOperationException("No software inventory is bound to this provider.");
        using var lease = samplingSubscriptionOwner.Acquire(2);
        return await inventory.CaptureInformationAsync(cancellationToken, lease.OwnerToken.TryPublish);
    }

    public async IAsyncEnumerable<DeviceInformationSnapshot> SubscribeSoftwareAsync(string subscriptionId, TimeSpan interval,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var lease = AcquireSoftwareInformationSubscription(subscriptionId, interval, cancellationToken);
        await foreach (var value in SoftwareInventory!.SubscribeInformationAsync(cancellationToken))
            yield return value;
    }

    private async Task<DeviceTopologySampleCompletion> TrySampleSoftwareAsync(SamplingOwnerToken ownerToken,
        CancellationToken cancellationToken,
        Action<DeviceTopologySampleScope, IReadOnlySet<string>>? collectionCommitted = null)
    {
        if (!ownerToken.IsActive || cancellationToken.IsCancellationRequested) return DeviceTopologySampleCompletion.Canceled;
        if (!HasCurrentDemand(DeviceTopologySampleScope.Software)) return DeviceTopologySampleCompletion.Sampled;
        var inventory = SoftwareInventory ?? throw new InvalidOperationException("No software inventory is bound to this provider.");
        try
        {
            var snapshot = await inventory.CaptureInformationAsync(cancellationToken,
                action => TryPublishHosted(ownerToken, action, () => collectionCommitted?.Invoke(DeviceTopologySampleScope.Software,
                    new HashSet<string>(StringComparer.Ordinal) { SoftwareItemId })));
            if (!ownerToken.IsActive) return DeviceTopologySampleCompletion.Canceled;
            return snapshot.ObservedAt is null ? DeviceTopologySampleCompletion.Failed : DeviceTopologySampleCompletion.Sampled;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return DeviceTopologySampleCompletion.Canceled;
        }
    }
}
