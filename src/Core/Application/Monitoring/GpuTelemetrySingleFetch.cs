using ResourceManager.Subscription;

namespace ResourceManager.App.Application.Monitoring;

public static class GpuTelemetrySingleFetch
{
    /// <summary>Owns demand for selected counters until a fresh result arrives.
    /// An empty counter request ends without returning retained adapters.</summary>
    public static Task<GpuTelemetryWorkerSnapshot?> FetchOnceAsync(this IGpuTelemetryPushSource source,
        string holder, GpuTelemetryWorkerRequest request, CancellationToken cancellationToken)
    {
        var started = DateTimeOffset.UtcNow;
        return SubscriptionSingleFetch.ReadAsync(source.SubscribeAsync(holder, request,
            SubscriptionSingleFetch.Interval, cancellationToken),
            snapshot => snapshot.CapturedAt >= started, cancellationToken);
    }
}
