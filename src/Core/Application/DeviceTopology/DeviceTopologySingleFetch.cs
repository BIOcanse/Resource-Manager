using ResourceManager.App.Domain.DeviceTopology;
using ResourceManager.Subscription;
using ResourceManager.ReadWrite.Information;

namespace ResourceManager.App.Application.DeviceTopology;

public static class DeviceTopologySingleFetch
{
    public static Task<DeviceTopologySnapshotState?> FetchOnceAsync(this IDeviceTopologySnapshotProvider source,
        string holder, CancellationToken cancellationToken)
    {
        var started = DateTimeOffset.UtcNow;
        // The provider first yields retained state. Wait for a new attempt,
        // including a failed attempt whose current measured values were cleared.
        return SubscriptionSingleFetch.ReadAsync(source.SubscribeAsync(holder,
            SubscriptionSingleFetch.Interval, cancellationToken),
            state => state.LastAttemptAt >= started, cancellationToken);
    }

    public static Task<DeviceInformationSnapshot?> FetchAcpiOnceAsync(this IDeviceTopologySnapshotProvider source,
        string holder, CancellationToken cancellationToken)
    {
        // ACPI publishes its fixed current value with the original observation time.
        // This stream starts with the requested publication, without a retained-state yield.
        return SubscriptionSingleFetch.ReadAsync(source.SubscribeAcpiAsync(holder,
            SubscriptionSingleFetch.Interval, cancellationToken),
            static _ => true, cancellationToken);
    }
}
