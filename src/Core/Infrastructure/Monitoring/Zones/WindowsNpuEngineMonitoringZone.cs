using ResourceManager.App.Application.Monitoring;
using ResourceManager.ReadWrite.Windows.Devices.Npu;
using ResourceManager.ReadWrite.Windows.Metrics.Resources;
using ResourceManager.ReadWrite.Windows.Metrics.Topology;

namespace ResourceManager.App.Infrastructure.Monitoring;

public sealed class WindowsNpuEngineMonitoringZone : MonitoringSourceZone
{
    private readonly Func<uint, IWindowsNpuEngineDirectory?, NpuEngineTopologyRead> open;
    private readonly Func<IReadOnlyList<NpuEngineCatalogIdentity>, IReadOnlySet<string>, uint,
        CancellationToken, IReadOnlyList<NpuEngineRead>> read;

    public WindowsNpuEngineMonitoringZone() : this(new WindowsMetricResourceOptions()) { }
    public WindowsNpuEngineMonitoringZone(WindowsMetricResourceOptions options)
        : this(new WindowsNpuEngineReader(options.MaximumNpuEngineNameCharacters)) { }
    private WindowsNpuEngineMonitoringZone(WindowsNpuEngineReader reader)
        : this((maximum, prior) => WindowsNpuEngineTopology.OpenDirectory(limit => reader.OpenDirectory(limit), maximum, prior), reader.Read) { }
    internal WindowsNpuEngineMonitoringZone(Func<uint, IWindowsNpuEngineDirectory?, NpuEngineTopologyRead> open,
        Func<IReadOnlyList<NpuEngineCatalogIdentity>, IReadOnlySet<string>, uint, CancellationToken, IReadOnlyList<NpuEngineRead>> read)
        : base(MonitoringSourceZoneIds.WindowsNpuEngine) { this.open = open; this.read = read; }
    internal NpuEngineTopologyRead OpenDirectory(uint maximum, IWindowsNpuEngineDirectory? prior)
        => open(maximum, prior);
    internal IReadOnlyList<NpuEngineRead> Read(IReadOnlyList<NpuEngineCatalogIdentity> devices,
        IReadOnlySet<string> requested, uint maximum, CancellationToken token)
        => read(devices, requested, maximum, token);
}
