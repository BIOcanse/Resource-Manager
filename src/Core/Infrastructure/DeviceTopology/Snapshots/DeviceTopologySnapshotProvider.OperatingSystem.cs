using System.Runtime.CompilerServices;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;
using ResourceManager.ReadWrite.Information;
using ResourceManager.ReadWrite.Windows.Devices.Snapshots;
using ResourceManager.ReadWrite.Windows.SystemInformation;

namespace ResourceManager.App.Infrastructure.DeviceTopology.Snapshots;

public sealed partial class DeviceTopologySnapshotProvider
{
    internal IDisposable AcquireOperatingSystemInformationSubscription(string subscriptionId, TimeSpan interval,
        CancellationToken cancellationToken = default,
        WindowsOperatingSystemInformationFields fields = WindowsOperatingSystemInformationFields.All)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subscriptionId);
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        var sourceKey = $"lease:{Interlocked.Increment(ref nextLeaseId)}:{subscriptionId.Trim()}";
        return TrackInformationDemand(new DeviceTopologySampleRequest(sourceKey,
            DeviceTopologySampleScope.OperatingSystem, cancellationToken, OperatingSystemFields: fields), interval);
    }
    internal async ValueTask<DeviceInformationSnapshot> CaptureOperatingSystemInformationAsync(CancellationToken cancellationToken,
        WindowsOperatingSystemInformationFields fields = WindowsOperatingSystemInformationFields.All)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        using var lease = samplingSubscriptionOwner.Acquire(2);
        return await OperatingSystemInventory.CaptureSelectedInformationAsync(fields, cancellationToken, lease.OwnerToken.TryPublish);
    }
    internal async IAsyncEnumerable<DeviceInformationSnapshot> SubscribeOperatingSystemAsync(string id, TimeSpan interval,
        [EnumeratorCancellation] CancellationToken cancellationToken,
        WindowsOperatingSystemInformationFields fields = WindowsOperatingSystemInformationFields.All)
    {
        using var publication = OperatingSystemInventory.Publications.Subscribe([WindowsOperatingSystemInformationOwner.CollectionItemId]);
        using var lease = AcquireOperatingSystemInformationSubscription(id, interval, cancellationToken, fields);
        yield return OperatingSystemInventory.ReadInformation();
        while (!cancellationToken.IsCancellationRequested)
        {
            await publication.WaitAsync(cancellationToken);
            yield return OperatingSystemInventory.ReadInformation();
        }
    }
    private async Task<DeviceTopologySampleCompletion> TrySampleOperatingSystemAsync(SamplingOwnerToken ownerToken,
        CancellationToken cancellationToken, WindowsOperatingSystemInformationFields selected,
        Action<DeviceTopologySampleScope, IReadOnlySet<string>>? collectionCommitted = null)
    {
        if (!ownerToken.IsActive || cancellationToken.IsCancellationRequested) return DeviceTopologySampleCompletion.Canceled;
        if (!HasCurrentDemand(DeviceTopologySampleScope.OperatingSystem)) return DeviceTopologySampleCompletion.Sampled;
        try
        {
            var queried = WindowsOperatingSystemInformationFields.None;
            var fields = await OperatingSystemInventory.CaptureSelectedInformationAsync(selected, cancellationToken,
                action => TryPublishHosted(ownerToken, action, () => collectionCommitted?.Invoke(DeviceTopologySampleScope.OperatingSystem,
                    OperatingSystemPublicationKeys(queried))), sourceQueriesCompleted: mask => queried = mask);
            if (!ownerToken.IsActive) return DeviceTopologySampleCompletion.Canceled;
            return fields.ObservedAt is null ? DeviceTopologySampleCompletion.Failed : DeviceTopologySampleCompletion.Sampled;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        { return DeviceTopologySampleCompletion.Canceled; }
    }
}


