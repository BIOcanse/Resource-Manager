using ResourceManager.App.Domain.CpuTopology;
using ResourceManager.App.Domain.DeviceTopology;
using ResourceManager.Subscription;
using ResourceManager.ReadWrite.Information;

namespace ResourceManager.App.Application.CpuTopology;

public static class CpuTopologySingleFetch
{
    public static Task<CpuTopologySnapshot?> FetchOnceAsync(this ICpuTopologyReader source,
        string holder, CancellationToken cancellationToken)
    {
        var started = DateTimeOffset.UtcNow;
        return SubscriptionSingleFetch.ReadAsync(source.SubscribeAsync(holder,
            SubscriptionSingleFetch.Interval, cancellationToken),
            snapshot => snapshot is null || snapshot.CapturedAt >= started, cancellationToken);
    }

    public static Task<DeviceInformationSnapshot?> FetchInformationOnceAsync(this ICpuTopologyReader source,
        string holder, CancellationToken cancellationToken)
    {
        var started = DateTimeOffset.UtcNow;
        // This stream publishes after attempts; a null observation time is its
        // explicit failed result with cleared values, and must also be delivered.
        return SubscriptionSingleFetch.ReadAsync(source.SubscribeInformationAsync(holder,
            SubscriptionSingleFetch.Interval, cancellationToken),
            snapshot => snapshot.ObservedAt is null || snapshot.ObservedAt >= started, cancellationToken);
    }
}
