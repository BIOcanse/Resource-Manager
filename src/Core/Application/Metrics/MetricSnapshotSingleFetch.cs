using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.Monitoring;
using ResourceManager.Subscription;

namespace ResourceManager.App.Application.Metrics;

public static class MetricSnapshotSingleFetch
{
    /// <summary>Holds a named demand until every delivered measurement has been
    /// attempted after this call. Failed or unavailable new samples remain explicit
    /// empty values, including a counter attempt that only establishes its baseline.
    /// Freshness does not require a numeric result or a second interval capture.
    /// Catalog declarations are topology, not sampled measurements.</summary>
    public static Task<HardwareMetricSnapshot?> FetchOnceAsync(this IMetricSnapshotPushSource source,
        string holder, MetricSampleRequest request, CancellationToken cancellationToken)
    {
        var started = DateTimeOffset.UtcNow.UtcTicks;
        return SubscriptionSingleFetch.ReadAsync(source.SubscribeAsync(holder, request,
            SubscriptionSingleFetch.Interval, cancellationToken), snapshot =>
            {
                var observations = snapshot.Datasets.Values.Where(item =>
                    item.DatasetId != SamplingDatasetIds.SystemMetricCatalog).ToArray();
                return (observations.Length > 0 || request.IsCatalogProbe)
                    && observations.All(item => Math.Max(item.LastAttemptAtUtcTicks, item.ObservedAtUtcTicks) >= started);
            }, cancellationToken);
    }
}
