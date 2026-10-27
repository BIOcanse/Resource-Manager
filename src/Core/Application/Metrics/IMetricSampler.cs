using ResourceManager.App.Domain.Metrics;

namespace ResourceManager.App.Application.Metrics;

public interface IMetricSampler
{
    /// <summary>Single subscription fetch: holds named demand until new data is
    /// delivered. Cancellation releases demand; this does not read retained current.</summary>
    Task<HardwareMetricSnapshot> GetSnapshotAsync(CancellationToken cancellationToken);

    /// <summary>Single subscription fetch for the requested items. An empty or
    /// unsupported selection returns an explicitly empty/warming projection.</summary>
    Task<HardwareMetricSnapshot> GetSnapshotAsync(
        MetricSampleRequest request,
        CancellationToken cancellationToken);

    Task<HardwareMetricSnapshot> CaptureSnapshotAsync(
        MetricSampleRequest request,
        CancellationToken cancellationToken);
}
