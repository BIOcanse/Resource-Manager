using ResourceManager.App.Domain.Metrics;

namespace ResourceManager.App.Application.Metrics;

public interface IMetricSnapshotPushSource
{
    IAsyncEnumerable<HardwareMetricSnapshot> SubscribeAsync(
        string subscriptionId,
        MetricSampleRequest request,
        TimeSpan interval,
        CancellationToken cancellationToken);
}
