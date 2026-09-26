using ResourceManager.App.Domain.Metrics;

namespace ResourceManager.App.Application.Metrics;

public interface IMetricSnapshotObservationSource
{
    IDisposable AcquireSubscription(
        string subscriptionId,
        MetricSampleRequest request,
        TimeSpan refreshInterval);

    HardwareMetricSnapshot? ReadLatest(MetricSampleRequest request);
}
