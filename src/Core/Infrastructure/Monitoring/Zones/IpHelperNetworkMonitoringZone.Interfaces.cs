using ResourceManager.App.Domain.Metrics;
using ResourceManager.ReadWrite.Windows.Devices;
using ResourceManager.ReadWrite.Windows.Metrics.Resources;
namespace ResourceManager.App.Infrastructure.Monitoring;
public sealed partial class IpHelperNetworkMonitoringZone
{
    private readonly WindowsNetworkInterfaceMetricResource interfaceResource;
    internal IpHelperNetworkMonitoringZone(IWindowsNetworkInterfaceCounterApi api, Func<long>? monotonicTicks = null, ulong? ticksPerSecond = null)
        : base(Application.Monitoring.MonitoringSourceZoneIds.IpHelperNetwork)
        => interfaceResource = new(api, monotonicTicks, ticksPerSecond);
    internal NetworkInterfaceTopologyRead ReadInterfaceIdentities() => interfaceResource.ReadInterfaceIdentities();
    internal IReadOnlyList<NetworkInterfaceMetricRead> ReadInterfaces(IReadOnlyList<NetworkInterfaceCatalogIdentity> identities,
        IReadOnlyDictionary<string, IReadOnlySet<MetricFieldCode>> requested) => interfaceResource.ReadInterfaces(identities, requested);
    internal static int CounterOffset(MetricFieldCode code) => WindowsNetworkInterfaceMetricResource.CounterOffset(code);
}
