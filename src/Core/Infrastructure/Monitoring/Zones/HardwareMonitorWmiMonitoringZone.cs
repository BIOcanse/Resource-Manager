using ResourceManager.App.Domain.Metrics;
using ResourceManager.ReadWrite.Windows.Platform;
using ResourceManager.ReadWrite.Windows.Metrics.Resources;

namespace ResourceManager.App.Infrastructure.Monitoring;

public sealed partial class HardwareMonitorWmiMonitoringZone
{
    internal static NativeMetricSnapshotOrdinalTopologyRead ReadFanIdentities(ulong generation,
        Func<string, string, QueryResult>? query = null)
    {
        var identities = query is null
            ? new HardwareMonitorFanIdentityRead(false, [])
            : new HardwareMonitorWmiReader((path, statement, _) =>
                { var result = query(path, statement); return new(result.Rows, result.Error); })
                .ReadFanIdentities(TimeSpan.FromSeconds(2));
        // The product owns the core-library session; injected WMI remains
        // an explicit SDK test/query path, never a production fallback.
        return WindowsNotebookFanProjection.CreateFanCatalog(identities,
            enableMechrevo: false, generation);
    }

    internal NativeMetricSnapshotOrdinalTopologyRead ReadFanCatalog(ulong generation)
    {
        lock (gate)
            return WindowsNotebookFanProjection.CreateFanCatalog(
                coreReader.ReadFanIdentities(TimeSpan.FromSeconds(2)),
                enableMechrevo: false, generation);
    }

    internal HardwareMonitorSensorSnapshot ReadSensors()
    {
        lock (gate) return coreReader.Read(TimeSpan.FromSeconds(2));
    }

}
