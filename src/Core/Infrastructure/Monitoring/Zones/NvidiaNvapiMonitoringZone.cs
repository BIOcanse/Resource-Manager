using ResourceManager.ReadWrite.Vendors.Nvidia;
using ResourceManager.App.Application.Monitoring;

namespace ResourceManager.App.Infrastructure.Monitoring;

public sealed class NvidiaNvapiMonitoringZone : MonitoringSourceZone, IDisposable
{
    private readonly NvidiaNvapiReader reader;

    public NvidiaNvapiMonitoringZone() : this(new NvidiaNvapiReader(GpuProviderMessages.Describe)) { }
    internal NvidiaNvapiMonitoringZone(NvidiaNvapiReader reader)
        : base(MonitoringSourceZoneIds.VendorNvidiaNvapi)
        => this.reader = reader ?? throw new ArgumentNullException(nameof(reader));

    internal NvidiaDrsSnapshot? ReadDrs(IReadOnlyList<NvidiaDrsProfileRequest> profiles, NvidiaDrsReadLimits limits, CancellationToken token)
        => CanRead ? reader.ReadDrs(profiles, limits, token) : null;
    internal NvidiaDrsSnapshot? DiscoverDrs(NvidiaDrsReadLimits limits, CancellationToken token)
        => CanRead ? reader.DiscoverDrs(limits, token) : null;
    internal bool TryPublishDrs(Action publish) => TryPublishWhileReadable(publish);
    // DI owns the original zone; a failed close retains this same reader for explicit retry.
    public void Dispose() => reader.Dispose();

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
