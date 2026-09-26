using ResourceManager.App.Domain.DeviceTopology;

namespace ResourceManager.App.Application.DeviceTopology;

public interface IDeviceTopologySnapshotProvider
{
    DeviceTopologySnapshotState ReadState();

    IAsyncEnumerable<DeviceTopologySnapshotState> SubscribeAsync(
        string subscriptionId,
        TimeSpan interval,
        CancellationToken cancellationToken);
}
