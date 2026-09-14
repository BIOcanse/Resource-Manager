using ResourceManager.App.Domain.Metrics;

namespace ResourceManager.App.Application.Metrics;

public interface IMetricSampler
{
    Task<HardwareMetricSnapshot> GetSnapshotAsync(CancellationToken cancellationToken);

    Task<HardwareMetricSnapshot> GetSnapshotAsync(
        MetricSampleRequest request,
        CancellationToken cancellationToken);

    Task<HardwareMetricSnapshot> CaptureSnapshotAsync(
        MetricSampleRequest request,
        CancellationToken cancellationToken);
}
