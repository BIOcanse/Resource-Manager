using ResourceManager.ReadWrite.Vendors.Nvidia;
using ResourceManager.App.Application.Monitoring;

namespace ResourceManager.App.Infrastructure.Monitoring;

public sealed class NvidiaNvapiMonitoringZone : MonitoringSourceZone, IDisposable
{
    private readonly object gate = new();
    private readonly NvidiaNvapiReader reader;

    public NvidiaNvapiMonitoringZone() : this(new NvidiaNvapiReader(GpuProviderMessages.Describe)) { }
    internal NvidiaNvapiMonitoringZone(NvidiaNvapiReader reader)
        : base(MonitoringSourceZoneIds.VendorNvidiaNvapi)
        => this.reader = reader ?? throw new ArgumentNullException(nameof(reader));

    internal NvidiaDrsSnapshot? ReadDrs(IReadOnlyList<NvidiaDrsProfileRequest> profiles, NvidiaDrsReadLimits limits, CancellationToken token)
    {
        lock (gate) return reader.ReadDrs(profiles, limits, token);
    }
    internal NvidiaDrsSnapshot? DiscoverDrs(NvidiaDrsReadLimits limits, CancellationToken token)
    {
        lock (gate) return reader.DiscoverDrs(limits, token);
    }
    // DI owns the original zone; a failed close retains this same reader for explicit retry.
    public void Dispose()
    {
        lock (gate) reader.Dispose();
    }

    internal Task<IReadOnlyList<NvidiaNvapiGpuSensor>> ReadAsync(
        NvidiaNvapiReadRequest request,
        WindowsGpuAdapterInventoryRead windowsInventory,
        CancellationToken cancellationToken)
    {
        lock (gate)
            return reader.ReadAsync(request, windowsInventory, cancellationToken);
    }
}
