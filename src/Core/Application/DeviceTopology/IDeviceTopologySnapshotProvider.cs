using ResourceManager.App.Domain.DeviceTopology;

namespace ResourceManager.App.Application.DeviceTopology;

public interface IDeviceTopologySnapshotProvider
{
    DeviceTopologySnapshotState ReadState();

    IAsyncEnumerable<DeviceTopologySnapshotState> SubscribeAsync(
        string subscriptionId,
        TimeSpan interval,
        CancellationToken cancellationToken);

    // Structural state uses change notification; one-shot requests and information reads
    // continue to use sample completion even when the sampled value is identical.
    IAsyncEnumerable<DeviceTopologySnapshotState> SubscribeChangesAsync(
        string subscriptionId, TimeSpan interval, CancellationToken cancellationToken);

    // No unread/retained-state yield: demand publishes the owner's once-read fixed value,
    // or its typed failure. Its original observation time is not a freshness criterion.
    IAsyncEnumerable<DeviceInformationSnapshot> SubscribeAcpiAsync(
        string subscriptionId,
        TimeSpan interval,
        CancellationToken cancellationToken);

    // The same Sampled owner serves named network holders and one-shot reads.
    IAsyncEnumerable<DeviceInformationSnapshot> SubscribeNetworkEndpointsAsync(
        string subscriptionId, TimeSpan interval, CancellationToken cancellationToken, bool includeCurrent = true);
}
