using ResourceManager.App.Application.Monitoring;
using ResourceManager.App.Domain.Metrics;

namespace ResourceManager.App.Infrastructure.Monitoring;

public sealed class NvidiaNvmlMonitoringZone : MonitoringSourceZone
{
    private readonly NvidiaNvmlReader reader = new();

    public NvidiaNvmlMonitoringZone()
        : base(MonitoringSourceZoneIds.VendorNvidiaNvml)
    {
    }

    internal Task<IReadOnlyList<GpuMetrics>> ReadAsync(
        NvidiaNvmlReadRequest request,
        WindowsGpuAdapterInventoryRead windowsInventory,
        CancellationToken cancellationToken)
    {
        return CanRead
            ? reader.ReadAsync(request, windowsInventory, cancellationToken)
            : Task.FromResult<IReadOnlyList<GpuMetrics>>([]);
    }
}
