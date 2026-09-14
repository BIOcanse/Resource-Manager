using ResourceManager.App.Application.Monitoring;
using ResourceManager.App.Domain.Metrics;

namespace ResourceManager.App.Infrastructure.Monitoring;

public sealed class AmdAdlxMonitoringZone : MonitoringSourceZone
{
    private readonly AmdAdlxBridgeReader reader = new();

    public AmdAdlxMonitoringZone()
        : base(MonitoringSourceZoneIds.VendorAmdAdlx)
    {
    }

    internal Task<IReadOnlyList<GpuMetrics>> ReadAsync(
        AmdAdlxReadRequest request,
        WindowsGpuAdapterInventoryRead windowsInventory,
        CancellationToken cancellationToken)
    {
        return CanRead
            ? reader.ReadAsync(request, windowsInventory, cancellationToken)
            : Task.FromResult<IReadOnlyList<GpuMetrics>>([]);
    }
}
