namespace ResourceManager.ReadWrite.Windows.Devices.Models;

public sealed record DeviceTopologyNetworkEndpointInformation(
    IReadOnlyList<NetworkTcpEndpoint>? TcpIpv4,
    IReadOnlyList<NetworkTcpEndpoint>? TcpIpv6,
    IReadOnlyList<NetworkUdpEndpoint>? UdpIpv4,
    IReadOnlyList<NetworkUdpEndpoint>? UdpIpv6)
{
    public static DeviceTopologyNetworkEndpointInformation Unavailable { get; } = new(null, null, null, null);
}

public sealed record NetworkSocketAddress(string Address, uint? ScopeIdRaw, ushort Port);
public sealed record NetworkEndpointProcessIdentity(uint ProcessId, string CreationWindowsFileTime100Ns);

/// <summary>OS OWNER_PID record. LISTEN has no meaningful remote endpoint; raw PID alone is not a process identity.</summary>
public sealed record NetworkTcpEndpoint(string AddressFamily, NetworkSocketAddress Local,
    NetworkSocketAddress? Remote, uint StateRaw, uint OwningProcessIdReported,
    NetworkEndpointProcessIdentity? ProcessIdentity = null);

/// <summary>OS local UDP binding, without an invented TCP state or remote endpoint.</summary>
public sealed record NetworkUdpEndpoint(string AddressFamily, NetworkSocketAddress Local,
    uint OwningProcessIdReported, NetworkEndpointProcessIdentity? ProcessIdentity = null);
