using ResourceManager.App.Application.Monitoring;

namespace ResourceManager.App.Infrastructure.Monitoring;

public sealed class NvidiaNvapiMonitoringZone : MonitoringSourceZone
{
    private readonly NvidiaNvapiReader reader = new();

    public NvidiaNvapiMonitoringZone()
        : base(MonitoringSourceZoneIds.VendorNvidiaNvapi)
    {
    }

    internal Task<IReadOnlyList<NvidiaNvapiGpuSensor>> ReadAsync(
        NvidiaNvapiReadRequest request,
        WindowsGpuAdapterInventoryRead windowsInventory,
        CancellationToken cancellationToken)
    {
        return CanRead
            ? reader.ReadAsync(request, windowsInventory, cancellationToken)
            : Task.FromResult<IReadOnlyList<NvidiaNvapiGpuSensor>>([]);
    }
}
