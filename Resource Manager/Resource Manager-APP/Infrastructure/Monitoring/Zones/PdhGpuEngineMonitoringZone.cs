using ResourceManager.App.Application.Monitoring;

namespace ResourceManager.App.Infrastructure.Monitoring;

public sealed class PdhGpuEngineMonitoringZone : MonitoringSourceZone
{
    private readonly PdhGpuEngineUsageReader reader;

    public PdhGpuEngineMonitoringZone()
        : this(ResourceManager.App.Infrastructure.NativeCore.UnavailableNativePdhSnapshotSource.Instance)
    {
    }

    internal PdhGpuEngineMonitoringZone(
        ResourceManager.App.Infrastructure.NativeCore.INativePdhSnapshotSource snapshotSource)
        : base(MonitoringSourceZoneIds.PdhGpuEngine)
    {
        reader = new PdhGpuEngineUsageReader(snapshotSource);
    }

    internal IReadOnlyDictionary<int, double> ReadUsageByAdapterIndex(IReadOnlyList<WindowsGpuAdapter> adapters)
    {
        return CanRead ? reader.ReadUsageByAdapterIndex(adapters) : new Dictionary<int, double>();
    }

    internal PdhGpuEngineUsageRead ReadUsageSnapshotByAdapterIndex(
        IReadOnlyList<WindowsGpuAdapter> adapters)
    {
        return CanRead
            ? reader.ReadUsageSnapshotByAdapterIndex(adapters)
            : PdhGpuEngineUsageRead.NotRequested;
    }

    internal IReadOnlyDictionary<int, GpuEngineSpecializedUsage> ReadSpecializedUsageByAdapterIndex(IReadOnlyList<WindowsGpuAdapter> adapters)
    {
        return CanRead ? reader.ReadSpecializedUsageByAdapterIndex(adapters) : new Dictionary<int, GpuEngineSpecializedUsage>();
    }
}
