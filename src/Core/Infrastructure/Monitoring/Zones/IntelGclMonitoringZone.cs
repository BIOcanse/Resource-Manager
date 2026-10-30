using ResourceManager.App.Application.Monitoring;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.ReadWrite.Vendors.Intel;
using ResourceManager.ReadWrite.Windows.Metrics.Topology;
using ResourceManager.ReadWrite.Native;

namespace ResourceManager.App.Infrastructure.Monitoring;

public sealed class IntelGclMonitoringZone : MonitoringSourceZone
{
    private readonly Func<WindowsGpuAdapterInventoryRead, IReadOnlyDictionary<int, IReadOnlySet<MetricFieldCode>>, CancellationToken, IReadOnlyList<IntelGclMetricRead>> read;
    private readonly Func<WindowsGpuAdapterInventoryRead, uint, IntelGclFanDirectoryLease?> openFans;
    private readonly Func<IReadOnlyList<IntelGclFanIdentity>, WindowsGpuAdapterInventoryRead, uint, CancellationToken, IReadOnlyList<IntelGclFanReading>> readFans;
    public IntelGclMonitoringZone() : this(new IntelGclReader(IntelGclNativeSession.Create)) { }
    internal IntelGclMonitoringZone(IntelGclReader reader)
        : this(reader.Read, (inventory, maximum) => reader.OpenFanDirectory(inventory, maximum), reader.ReadFans) { }
    internal IntelGclMonitoringZone(
        Func<WindowsGpuAdapterInventoryRead, IReadOnlyDictionary<int, IReadOnlySet<MetricFieldCode>>, CancellationToken, IReadOnlyList<IntelGclMetricRead>> read,
        Func<WindowsGpuAdapterInventoryRead, uint, IntelGclFanDirectoryLease?> openFans,
        Func<IReadOnlyList<IntelGclFanIdentity>, WindowsGpuAdapterInventoryRead, uint, CancellationToken, IReadOnlyList<IntelGclFanReading>> readFans)
        : base(MonitoringSourceZoneIds.VendorIntelGcl)
        => (this.read, this.openFans, this.readFans) = (read, openFans, readFans);
    internal IReadOnlyList<IntelGclMetricRead> Read(WindowsGpuAdapterInventoryRead inventory,
        IReadOnlyDictionary<int, IReadOnlySet<MetricFieldCode>> requested, CancellationToken cancellationToken)
        => read(inventory, requested, cancellationToken);
    internal IntelGclFanDirectoryLease? OpenFanDirectory(WindowsGpuAdapterInventoryRead inventory, uint maximum)
        => openFans(inventory, maximum);
    internal IntelFanTopologyRead OpenFanTopology(WindowsGpuAdapterInventoryRead inventory, uint maximum, IntelGclFanDirectoryLease? prior)
        => WindowsIntelFanTopology.OpenDirectory(openFans, inventory, maximum, prior);
    internal IReadOnlyList<IntelGclFanReading> ReadFans(IReadOnlyList<IntelGclFanIdentity> targets,
        WindowsGpuAdapterInventoryRead inventory, uint maximum, CancellationToken cancellationToken)
        => readFans(targets, inventory, maximum, cancellationToken);
}
