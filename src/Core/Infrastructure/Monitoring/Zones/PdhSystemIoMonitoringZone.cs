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
        return reader.Read(request);
    }
}
