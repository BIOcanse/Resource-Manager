using ResourceManager.App.Domain.CpuTopology;
using ResourceManager.Subscription;

namespace ResourceManager.App.Application.CpuTopology;

public static class CpuCoreResidencySingleFetch
{
    /// <summary>Owns named demand until a fresh residency result or explicit
    /// unavailable result arrives, then releases the subscription.</summary>
    public static Task<CpuCoreResidencySnapshot?> FetchOnceAsync(this ICpuCoreResidencyReader source,
        string holder, CancellationToken cancellationToken)
    {
        var started = DateTimeOffset.UtcNow;
        return SubscriptionSingleFetch.ReadAsync(source.SubscribeAsync(holder,
            SubscriptionSingleFetch.Interval, cancellationToken),
            snapshot => snapshot is null || snapshot.CapturedAt >= started, cancellationToken);
    }
}
