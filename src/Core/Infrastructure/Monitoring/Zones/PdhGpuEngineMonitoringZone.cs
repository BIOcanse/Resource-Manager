using ResourceManager.App.Application.Monitoring;

namespace ResourceManager.App.Infrastructure.Monitoring;

public sealed class PdhGpuEngineMonitoringZone : MonitoringSourceZone
{
    private readonly PdhGpuEngineUsageReader reader;
    private readonly ResourceManager.ReadWrite.Windows.Pdh.PdhGpuAdapterSharedMemoryReader sharedMemoryReader;

    public PdhGpuEngineMonitoringZone()
        : this(UnavailableNativePdhSnapshotSource.Instance,
            new ResourceManager.ReadWrite.Windows.Pdh.PdhGpuAdapterSharedMemoryReader())
    {
    }

    internal PdhGpuEngineMonitoringZone(
        ResourceManager.ReadWrite.Native.Pdh.INativePdhSnapshotSource snapshotSource)
        : this(snapshotSource, new ResourceManager.ReadWrite.Windows.Pdh.PdhGpuAdapterSharedMemoryReader())
    {
    }

    internal PdhGpuEngineMonitoringZone(
        ResourceManager.ReadWrite.Native.Pdh.INativePdhSnapshotSource snapshotSource,
        ResourceManager.ReadWrite.Windows.Pdh.PdhGpuAdapterSharedMemoryReader sharedMemoryReader)
        : base(MonitoringSourceZoneIds.PdhGpuEngine)
    {
        reader = new PdhGpuEngineUsageReader(snapshotSource);
        this.sharedMemoryReader = sharedMemoryReader ?? throw new ArgumentNullException(nameof(sharedMemoryReader));
    }

    internal IReadOnlyDictionary<int, double> ReadUsageByAdapterIndex(IReadOnlyList<WindowsGpuAdapter> adapters)
    {
        return reader.ReadUsageByAdapterIndex(adapters);
    }

    internal PdhGpuEngineUsageRead ReadUsageSnapshotByAdapterIndex(
        IReadOnlyList<WindowsGpuAdapter> adapters)
    {
        return reader.ReadUsageSnapshotByAdapterIndex(adapters);
    }

    internal ResourceManager.ReadWrite.Windows.Pdh.PdhGpuAdapterSharedMemoryRead
        ReadSharedMemoryByAdapterIndex(IReadOnlyList<WindowsGpuAdapter> adapters)
        => sharedMemoryReader.ReadByAdapterIndex(adapters);

    internal IReadOnlyDictionary<int, GpuEngineSpecializedUsage> ReadSpecializedUsageByAdapterIndex(IReadOnlyList<WindowsGpuAdapter> adapters)
    {
        return reader.ReadSpecializedUsageByAdapterIndex(adapters);
    }
}
