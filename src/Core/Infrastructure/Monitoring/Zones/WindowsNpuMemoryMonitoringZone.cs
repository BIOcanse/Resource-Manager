using ResourceManager.App.Application.Monitoring;
using ResourceManager.ReadWrite.Windows.Devices.Npu;
using ResourceManager.ReadWrite.Windows.Metrics.Topology;

namespace ResourceManager.App.Infrastructure.Monitoring;

public sealed class WindowsNpuMemoryMonitoringZone : MonitoringSourceZone
{
    private readonly Func<IReadOnlyList<NpuMemoryCatalogIdentity>, IReadOnlyDictionary<string, NpuMemoryFields>, uint,
        CancellationToken, IReadOnlyList<NpuMemoryRead>> read;
    public WindowsNpuMemoryMonitoringZone() : this(new WindowsNpuMemoryReader().Read) { }
    internal WindowsNpuMemoryMonitoringZone(Func<IReadOnlyList<NpuMemoryCatalogIdentity>,
        IReadOnlyDictionary<string, NpuMemoryFields>, uint, CancellationToken, IReadOnlyList<NpuMemoryRead>> read)
        : base(MonitoringSourceZoneIds.WindowsNpuMemory) => this.read = read;
    internal NpuMemoryTopologyRead ReadIdentities(uint effectiveMaximumInstanceCount) => WindowsNpuMemoryTopology.Read(effectiveMaximumInstanceCount);
    internal IReadOnlyList<NpuMemoryRead> Read(IReadOnlyList<NpuMemoryCatalogIdentity> devices,
        IReadOnlyDictionary<string, NpuMemoryFields> requested, uint effectiveMaximumInstanceCount, CancellationToken cancellationToken)
        => read(devices, requested, effectiveMaximumInstanceCount, cancellationToken);
}
