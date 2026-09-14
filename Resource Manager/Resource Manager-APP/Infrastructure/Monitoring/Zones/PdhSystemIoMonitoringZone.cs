using ResourceManager.App.Application.Monitoring;

namespace ResourceManager.App.Infrastructure.Monitoring;

public sealed class PdhSystemIoMonitoringZone : MonitoringSourceZone
{
    private readonly PdhSystemIoReader reader;

    public PdhSystemIoMonitoringZone()
        : this(ResourceManager.App.Infrastructure.NativeCore.UnavailableNativePdhSnapshotSource.Instance)
    {
    }

    internal PdhSystemIoMonitoringZone(
        ResourceManager.App.Infrastructure.NativeCore.INativePdhSnapshotSource snapshotSource)
        : base(MonitoringSourceZoneIds.PdhSystemIo)
    {
        reader = new PdhSystemIoReader(snapshotSource);
    }

    internal PdhSystemIoMetrics Read(PdhSystemIoReadRequest request)
    {
        return CanRead
            ? reader.Read(request)
            : new PdhSystemIoMetrics(
                PdhDiskMetrics.NotRequested,
                PdhNetworkMetrics.NotRequested)
            {
                ProviderAvailability =
                    ResourceManager.App.Infrastructure.NativeCore
                        .NativePdhProviderAvailability.NotRequested,
                ObservedAt = null
            };
    }
}
