using ResourceManager.App.Domain.DeviceTopology;

namespace ResourceManager.App.Application.DeviceTopology;

public interface IDeviceTopologySnapshotProvider
{
    DeviceTopologySnapshotState ReadState();

    IAsyncEnumerable<DeviceTopologySnapshotState> SubscribeAsync(
        string subscriptionId,
        TimeSpan interval,
        CancellationToken cancellationToken);

    // No unread/retained-state yield: demand publishes the owner's once-read fixed value,
    // or its typed failure. Its original observation time is not a freshness criterion.
    IAsyncEnumerable<DeviceInformationSnapshot> SubscribeAcpiAsync(
        string subscriptionId,
        TimeSpan interval,
        CancellationToken cancellationToken);
}
