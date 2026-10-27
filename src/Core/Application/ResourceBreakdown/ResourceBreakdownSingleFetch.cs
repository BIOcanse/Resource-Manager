using ResourceManager.App.Domain.ResourceBreakdown;
using ResourceManager.Subscription;

namespace ResourceManager.App.Application.ResourceBreakdown;

public static class ResourceBreakdownSingleFetch
{
    /// <summary>Registers named demand, waits for a new attempt for every
    /// delivered dataset, then releases; never returns a retained old sample.</summary>
    public static Task<ResourceBreakdownSnapshot?> FetchOnceAsync(this IResourceBreakdownPushSource source,
        string holder, ResourceBreakdownSampleRequest request, CancellationToken cancellationToken)
    {
        var started = DateTimeOffset.UtcNow;
        return SubscriptionSingleFetch.ReadAsync(source.SubscribeAsync(holder, request,
            SubscriptionSingleFetch.Interval, cancellationToken), snapshot => snapshot.Datasets.Count > 0
                ? snapshot.Datasets.All(item => item.LastAttemptAt >= started)
                : snapshot.Sampling.LastAttemptAt >= started, cancellationToken);
    }
}
