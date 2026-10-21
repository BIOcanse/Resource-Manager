using ResourceManager.ReadWrite.Information;

namespace ResourceManager.App.Infrastructure.DeviceTopology.Snapshots;

public sealed partial class DeviceTopologySnapshotProvider
{
    internal IDisposable AcquireInformationSubscription(string subscriptionId, TimeSpan interval,
        bool acpi = false, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subscriptionId);
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        var sourceKey = $"lease:{Interlocked.Increment(ref nextLeaseId)}:{subscriptionId.Trim()}";
        return TrackInformationDemand(new DeviceTopologySampleRequest(sourceKey,
            acpi ? DeviceTopologySampleScope.Acpi : DeviceTopologySampleScope.Snapshot, cancellationToken), interval);
    }

    internal async ValueTask<DeviceInformationSnapshot> CaptureInformationAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        using var lease = samplingSubscriptionOwner.Acquire(2);
        return await captureOwner.CaptureInformationAsync(cancellationToken, Permission(lease.OwnerToken, DeviceTopologySampleScope.Snapshot));
    }

    internal async ValueTask<DeviceInformationSnapshot> CaptureAcpiInformationAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        using var lease = samplingSubscriptionOwner.Acquire(2);
        return await captureOwner.CaptureAcpiInformationAsync(cancellationToken, Permission(lease.OwnerToken, DeviceTopologySampleScope.Acpi));
    }

    private sealed class InformationLease(DeviceTopologySnapshotProvider source, string key) : IDisposable
    {
        private DeviceTopologySnapshotProvider? owner = source;
        public void Dispose()
        {
            var current = Interlocked.Exchange(ref owner, null);
            if (current is null || Volatile.Read(ref current.disposed) != 0) return;
            current.subscriptions.Remove(key, DateTimeOffset.UtcNow);
            current.SignalDemand();
        }
    }
}
