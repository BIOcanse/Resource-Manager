using System.Globalization;
using ResourceManager.ReadWrite.Windows.Devices.Models;

namespace ResourceManager.ReadWrite.Windows.Devices;

public sealed class WindowsNetworkEndpointInformationReader
{
    private readonly IWindowsNetworkEndpointTableApi api;
    public WindowsNetworkEndpointInformationReader() : this(new WindowsNetworkEndpointTableApi()) { }
    internal WindowsNetworkEndpointInformationReader(IWindowsNetworkEndpointTableApi api) => this.api = api;

    [RuntimeRead(ReadVolatilityKind.Sampled)]
    public DeviceTopologyNetworkEndpointInformation Read()
    {
        var discovered = ReadTables();
        var pids = (discovered.TcpIpv4 ?? []).Concat(discovered.TcpIpv6 ?? []).Select(static row => row.OwningProcessIdReported)
            .Concat((discovered.UdpIpv4 ?? []).Concat(discovered.UdpIpv6 ?? []).Select(static row => row.OwningProcessIdReported))
            .Where(static pid => pid != 0).Distinct().Order().ToArray();
        var leases = new Dictionary<uint, (IWindowsNetworkEndpointProcessLease Lease, ulong Created)>();
        try
        {
            foreach (var pid in pids)
            {
                IWindowsNetworkEndpointProcessLease? lease = null;
                try
                {
                    lease = api.OpenProcess(pid);
                    if (lease is not null && lease.ProcessId == pid && lease.TryReadCreationFileTime(out var created) && created != 0)
                    { leases.Add(pid, (lease, created)); lease = null; }
                }
                catch (Exception) { }
                finally { lease?.Dispose(); }
            }
            var current = leases.Count > 0 ? ReadTables() : discovered;
            var identities = new Dictionary<uint, NetworkEndpointProcessIdentity>();
            foreach (var (pid, opened) in leases)
            {
                try
                {
                    if (opened.Lease.ProcessId == pid && opened.Lease.TryReadCreationFileTime(out var created) && created == opened.Created)
                        identities.Add(pid, new(pid, created.ToString(CultureInfo.InvariantCulture)));
                }
                catch (Exception) { }
            }
            return current with {
                TcpIpv4 = Tcp(current.TcpIpv4, identities), TcpIpv6 = Tcp(current.TcpIpv6, identities),
                UdpIpv4 = Udp(current.UdpIpv4, identities), UdpIpv6 = Udp(current.UdpIpv6, identities) };
        }
        finally { foreach (var opened in leases.Values) opened.Lease.Dispose(); }
    }

    public Information.DeviceInformationSnapshot ReadFields()
        => Information.WindowsInformationFieldProjection.NetworkEndpoints(Read(), DateTimeOffset.UtcNow);

    private DeviceTopologyNetworkEndpointInformation ReadTables() => new(
        ReadTcp(WindowsNetworkEndpointTableKind.TcpIpv4, false), ReadTcp(WindowsNetworkEndpointTableKind.TcpIpv6, true),
        ReadUdp(WindowsNetworkEndpointTableKind.UdpIpv4, false), ReadUdp(WindowsNetworkEndpointTableKind.UdpIpv6, true));
    private IReadOnlyList<NetworkTcpEndpoint>? ReadTcp(WindowsNetworkEndpointTableKind kind, bool ipv6)
    { try { return WindowsNetworkEndpointTableDecoder.Tcp(api.Read(kind), ipv6); } catch (Exception) { return null; } }
    private IReadOnlyList<NetworkUdpEndpoint>? ReadUdp(WindowsNetworkEndpointTableKind kind, bool ipv6)
    { try { return WindowsNetworkEndpointTableDecoder.Udp(api.Read(kind), ipv6); } catch (Exception) { return null; } }

    private static IReadOnlyList<NetworkTcpEndpoint>? Tcp(IReadOnlyList<NetworkTcpEndpoint>? rows,
        IReadOnlyDictionary<uint, NetworkEndpointProcessIdentity> identities)
        => rows is null ? null : Array.AsReadOnly(rows.Select(row => row with { ProcessIdentity = identities.GetValueOrDefault(row.OwningProcessIdReported) })
            .OrderBy(static row => row.Local.Address, StringComparer.Ordinal).ThenBy(static row => row.Local.ScopeIdRaw).ThenBy(static row => row.Local.Port)
            .ThenBy(static row => row.Remote?.Address, StringComparer.Ordinal).ThenBy(static row => row.Remote?.ScopeIdRaw).ThenBy(static row => row.Remote?.Port)
            .ThenBy(static row => row.StateRaw).ThenBy(static row => row.OwningProcessIdReported).ToArray());
    private static IReadOnlyList<NetworkUdpEndpoint>? Udp(IReadOnlyList<NetworkUdpEndpoint>? rows,
        IReadOnlyDictionary<uint, NetworkEndpointProcessIdentity> identities)
        => rows is null ? null : Array.AsReadOnly(rows.Select(row => row with { ProcessIdentity = identities.GetValueOrDefault(row.OwningProcessIdReported) })
            .OrderBy(static row => row.Local.Address, StringComparer.Ordinal).ThenBy(static row => row.Local.ScopeIdRaw).ThenBy(static row => row.Local.Port)
            .ThenBy(static row => row.OwningProcessIdReported).ToArray());
}
