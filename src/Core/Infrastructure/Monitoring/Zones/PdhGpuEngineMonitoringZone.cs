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
        return CanRead ? reader.ReadUsageByAdapterIndex(adapters) : new Dictionary<int, double>();
    }

    internal PdhGpuEngineUsageRead ReadUsageSnapshotByAdapterIndex(
        IReadOnlyList<WindowsGpuAdapter> adapters)
    {
        return CanRead
            ? reader.ReadUsageSnapshotByAdapterIndex(adapters)
            : PdhGpuEngineUsageRead.NotRequested;
    }

    internal ResourceManager.ReadWrite.Windows.Pdh.PdhGpuAdapterSharedMemoryRead
        ReadSharedMemoryByAdapterIndex(IReadOnlyList<WindowsGpuAdapter> adapters)
        => CanRead
            ? sharedMemoryReader.ReadByAdapterIndex(adapters)
            : ResourceManager.ReadWrite.Windows.Pdh.PdhGpuAdapterSharedMemoryRead.NotRequested;

    internal IReadOnlyDictionary<int, GpuEngineSpecializedUsage> ReadSpecializedUsageByAdapterIndex(IReadOnlyList<WindowsGpuAdapter> adapters)
    {
        return CanRead ? reader.ReadSpecializedUsageByAdapterIndex(adapters) : new Dictionary<int, GpuEngineSpecializedUsage>();
    }
}
