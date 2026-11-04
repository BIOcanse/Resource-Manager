using ResourceManager.App.Application.Monitoring;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.DeviceTopology;

namespace ResourceManager.App.Infrastructure.Monitoring;

public sealed class AmdAdlxMonitoringZone : MonitoringSourceZone
{
    private readonly AmdAdlxBridgeReader reader;

    public AmdAdlxMonitoringZone()
        : this(new AmdAdlxBridgeReader(GpuProviderMessages.Describe))
    {
    }

    internal AmdAdlxMonitoringZone(AmdAdlxBridgeReader reader)
        : base(MonitoringSourceZoneIds.VendorAmdAdlx)
    {
        this.reader = reader;
    }

    internal Task<IReadOnlyList<GpuMetrics>> ReadAsync(
        AmdAdlxReadRequest request,
        WindowsGpuAdapterInventoryRead windowsInventory,
        CancellationToken cancellationToken)
    {
        return reader.ReadAsync(request, windowsInventory, cancellationToken);
    }

    internal IReadOnlyList<DeviceTopologyAmdGpuInformation>? ReadDeclarations(WindowsGpuAdapterInventoryRead inventory)
        => reader.ReadDeclarations(inventory, true);
}
