using ResourceManager.App.Application.Monitoring;
using ResourceManager.App.Domain.Metrics;

namespace ResourceManager.App.Infrastructure.Monitoring;

public sealed class WindowsBatteryMonitoringZone : MonitoringSourceZone
{
    private readonly WindowsBatteryReader reader;
    public WindowsBatteryMonitoringZone() : this(new WindowsBatteryReader()) { }
    internal WindowsBatteryMonitoringZone(WindowsBatteryReader reader) : base(MonitoringSourceZoneIds.WindowsBattery)
        => this.reader = reader;
    internal BatteryTopologyRead ReadIdentities() => CanRead ? reader.ReadIdentities()
        : new(NativeMetricSnapshotCatalogTopologyStatus.Unavailable, []);
    internal BatteryTopologyRead ReadDeclarations() => CanRead ? reader.ReadIdentities(includeDeclarations: true)
        : new(NativeMetricSnapshotCatalogTopologyStatus.Unavailable, []);
    internal IReadOnlyList<BatteryMetricRead> Read(IReadOnlyList<BatteryCatalogIdentity> devices,
        IReadOnlyDictionary<int, IReadOnlySet<MetricFieldCode>> requested)
        => CanRead ? devices.Where(device => requested.ContainsKey(device.DisplayIndex))
            .Select(device => reader.Read(device, requested[device.DisplayIndex])).ToArray() : [];
}
