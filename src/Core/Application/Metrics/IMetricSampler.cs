using ResourceManager.App.Domain.Metrics;

namespace ResourceManager.App.Application.Metrics;

public interface IMetricSampler
{
    /// <summary>Single subscription fetch: holds named demand until new data is
    /// delivered. Cancellation releases demand; this does not read retained current.</summary>
    Task<HardwareMetricSnapshot> GetSnapshotAsync(CancellationToken cancellationToken);

    /// <summary>Single subscription fetch for requested measurements. An empty or
    /// unsupported selection returns an explicitly empty/warming projection.
    /// CatalogProbe reads committed declarations only and never samples numbers.</summary>
    Task<HardwareMetricSnapshot> GetSnapshotAsync(
        MetricSampleRequest request,
        CancellationToken cancellationToken);

    Task<HardwareMetricSnapshot> CaptureSnapshotAsync(
        MetricSampleRequest request,
        CancellationToken cancellationToken);
}
