using ResourceManager.App.Application.Monitoring;

namespace ResourceManager.App.Infrastructure.Monitoring;

public sealed class PdhSystemIoMonitoringZone : MonitoringSourceZone
{
    private readonly PdhSystemIoReader reader;

    public PdhSystemIoMonitoringZone()
        : this(UnavailableNativePdhSnapshotSource.Instance)
    {
    }

    internal PdhSystemIoMonitoringZone(
        ResourceManager.ReadWrite.Native.Pdh.INativePdhSnapshotSource snapshotSource)
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
                    NativePdhProviderAvailability.NotRequested,
                ObservedAt = null
            };
    }
}
