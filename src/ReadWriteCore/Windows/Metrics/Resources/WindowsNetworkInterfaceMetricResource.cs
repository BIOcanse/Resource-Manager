using System.Buffers.Binary;
using System.Diagnostics;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.ReadWrite.Native;
using ResourceManager.ReadWrite.Windows.Devices;
namespace ResourceManager.ReadWrite.Windows.Metrics.Resources;
public sealed class WindowsNetworkInterfaceMetricResource
{
    private readonly IWindowsNetworkInterfaceCounterApi interfaceApi;
    private readonly Func<long> monotonicTicks;
    private readonly ulong ticksPerSecond;

    public WindowsNetworkInterfaceMetricResource() : this(new WindowsNetworkInterfaceCounterApi()) { }
    internal WindowsNetworkInterfaceMetricResource(IWindowsNetworkInterfaceCounterApi api,
        Func<long>? monotonicTicks = null, ulong? ticksPerSecond = null)
    {
        interfaceApi = api;
        this.monotonicTicks = monotonicTicks ?? Stopwatch.GetTimestamp;
        this.ticksPerSecond = ticksPerSecond ?? checked((ulong)Stopwatch.Frequency);
    }

    public NetworkInterfaceTopologyRead ReadInterfaceIdentities()
    {

        try
        {
            var rows = WindowsNetworkInterfaceTableDecoder.Interfaces(interfaceApi.ReadInterfaces());
            if (rows is null || rows.Any(static row => row.Luid == 0 || row.Guid == Guid.Empty)
                || rows.Select(static row => row.Luid).Distinct().Count() != rows.Count
                || rows.Select(static row => row.Guid).Distinct().Count() != rows.Count)
                return new(NativeMetricSnapshotCatalogTopologyStatus.Unavailable, []);
            return new(NativeMetricSnapshotCatalogTopologyStatus.Complete,
                rows.OrderBy(static row => row.Guid).Select(static (row, ordinal) =>
                    new NetworkInterfaceCatalogIdentity(ordinal, row.Guid, row.Luid, row.Index)).ToArray());
        }
        catch (Exception) { return new(NativeMetricSnapshotCatalogTopologyStatus.Unavailable, []); }
    }

    public IReadOnlyList<NetworkInterfaceMetricRead> ReadInterfaces(
        IReadOnlyList<NetworkInterfaceCatalogIdentity> identities,
        IReadOnlyDictionary<string, IReadOnlySet<MetricFieldCode>> requested)
    {
        if (requested.Count == 0) return [];
        var result = new List<NetworkInterfaceMetricRead>();
        foreach (var identity in identities)
        {
            if (!requested.TryGetValue(identity.ExactIdentityKey, out var fields)
                || !fields.Any(static code => CounterOffset(code) >= 0)) continue;
            var counters = new Dictionary<MetricFieldCode, ulong>();
            var rates = new Dictionary<MetricFieldCode, NativeMetricSnapshotTimedCounter>();
            var changedIdentity = false;
            try
            {
                var row = interfaceApi.ReadInterface(identity.Luid);
                if (row is { Length: WindowsNetworkInterfaceTableDecoder.InterfaceRowSize })
                {
                    var luid = BinaryPrimitives.ReadUInt64LittleEndian(row);
                    var index = BinaryPrimitives.ReadUInt32LittleEndian(row.AsSpan(8));
                    var guid = new Guid(row.AsSpan(12, 16));
                    changedIdentity = luid != identity.Luid || index != identity.Index || guid != identity.Guid;
                    if (!changedIdentity)
                    {
                        var ticks = monotonicTicks();
                        foreach (var field in fields)
                        {
                            var offset = CounterOffset(field);
                            if (offset < 0) continue;
                            var value = BinaryPrimitives.ReadUInt64LittleEndian(row.AsSpan(offset));
                            if (field is MetricFieldCode.NetworkRxRate or MetricFieldCode.NetworkTxRate)
                            {
                                if (ticks >= 0 && ticksPerSecond > 0) rates[field] = new(value, (ulong)ticks, ticksPerSecond);
                            }
                            else counters[field] = value;
                        }
                    }
                }
            }
            catch (Exception) { counters.Clear(); rates.Clear(); }
            result.Add(new(identity.ExactIdentityKey, counters, rates, changedIdentity));
        }
        return result;
    }

    public static int CounterOffset(MetricFieldCode code) => code switch
    {
        MetricFieldCode.NetworkRxOctets or MetricFieldCode.NetworkRxRate => 1208,
        MetricFieldCode.NetworkRxUnicastPackets => 1216,
        MetricFieldCode.NetworkRxNonUnicastPackets => 1224,
        MetricFieldCode.NetworkRxDiscards => 1232,
        MetricFieldCode.NetworkRxErrors => 1240,
        MetricFieldCode.NetworkRxUnknownProtocolPackets => 1248,
        MetricFieldCode.NetworkRxUnicastOctets => 1256,
        MetricFieldCode.NetworkRxMulticastOctets => 1264,
        MetricFieldCode.NetworkRxBroadcastOctets => 1272,
        MetricFieldCode.NetworkTxOctets or MetricFieldCode.NetworkTxRate => 1280,
        MetricFieldCode.NetworkTxUnicastPackets => 1288,
        MetricFieldCode.NetworkTxNonUnicastPackets => 1296,
        MetricFieldCode.NetworkTxDiscards => 1304,
        MetricFieldCode.NetworkTxErrors => 1312,
        MetricFieldCode.NetworkTxUnicastOctets => 1320,
        MetricFieldCode.NetworkTxMulticastOctets => 1328,
        MetricFieldCode.NetworkTxBroadcastOctets => 1336,
        _ => -1
    };
}

