using ResourceManager.App.Domain.CpuTopology;
using ResourceManager.Subscription;

namespace ResourceManager.App.Application.CpuTopology;

public static class CpuCoreResidencySingleFetch
{
    /// <summary>Owns named demand until a fresh residency result or explicit
    /// unavailable result arrives, then releases the subscription.</summary>
    public static async Task<CpuCoreResidencyObservation> FetchOnceAsync(this ICpuCoreResidencyReader source,
        string holder, CancellationToken cancellationToken)
    {
        var started = DateTimeOffset.UtcNow;
        return await SubscriptionSingleFetch.ReadAsync(source.SubscribeAsync(holder,
            SubscriptionSingleFetch.Interval, cancellationToken),
            value => value is not CpuCoreResidencySnapshot snapshot || snapshot.CapturedAt >= started, cancellationToken)
            ?? throw new InvalidOperationException("The CPU residency subscription ended before publishing an observation.");
    }
}
