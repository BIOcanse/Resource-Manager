using ResourceManager.App.Domain.Metrics;
using ResourceManager.ReadWrite.Windows.Cpu.Models;
using ResourceManager.ReadWrite.Windows.Metrics.Resources;
namespace ResourceManager.App.Infrastructure.Monitoring;
public sealed partial class WindowsMemoryMonitoringZone
{
    internal WindowsMemoryMonitoringZone(Func<ushort, ulong?> readNumaAvailable, Func<MemoryMetrics>? readMemory = null,
        Func<IReadOnlyList<ushort>, IReadOnlyDictionary<ushort, ulong>>? readNumaCapacityMiB = null) : this()
        => resource = new(readNumaAvailable, readMemory, readNumaCapacityMiB);
    internal static NumaMemoryTopologyRead ReadNumaIdentities() => WindowsMemoryMetricResource.ReadNumaIdentities();
    internal static NumaMemoryTopologyRead ProjectNumaIdentities(IReadOnlyList<CpuOsNumaNode>? nodes) => WindowsMemoryMetricResource.ProjectNumaIdentities(nodes);
    internal IReadOnlyDictionary<string, ulong> ReadNumaAvailable(IReadOnlyList<NumaMemoryCatalogIdentity> nodes, IReadOnlySet<string> requestedDevices)
        => resource.ReadNumaAvailable(nodes, requestedDevices);
    internal IReadOnlyDictionary<string, ulong> ReadNumaCapacityMiB(IReadOnlyList<NumaMemoryCatalogIdentity> nodes, IReadOnlySet<string> requestedDevices)
        => resource.ReadNumaCapacityMiB(nodes, requestedDevices);
}
