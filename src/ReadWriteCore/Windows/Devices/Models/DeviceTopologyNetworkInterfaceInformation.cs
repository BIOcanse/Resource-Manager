namespace ResourceManager.ReadWrite.Windows.Devices.Models;

public sealed record DeviceTopologyNetworkInterfaceInformation(
    string DeviceIdentity,
    string? InterfaceGuid,
    ulong? InterfaceLuid,
    uint? InterfaceIndex,
    IReadOnlyList<NetworkUnicastAddress>? UnicastAddresses)
{
    public IReadOnlyList<NetworkRoute>? Routes { get; init; }
}

/// <summary>OS unicast table record; DAD state and SkipAsSource determine usability, not this record's presence.</summary>
public sealed record NetworkUnicastAddress(
    string AddressFamily,
    string Address,
    byte OnLinkPrefixLength,
    uint? SocketScopeId,
    uint ScopeIdRaw,
    uint PrefixOriginRaw,
    uint SuffixOriginRaw,
    uint DadStateRaw,
    uint ValidLifetimeSeconds,
    uint PreferredLifetimeSeconds,
    bool SkipAsSource,
    string CreationWindowsFileTime100NsRaw);

/// <summary>OS routing table record. The metric is the route offset, not the combined interface/route preference.</summary>
public sealed record NetworkRoute(
    string AddressFamily,
    string DestinationAddress,
    byte DestinationPrefixLength,
    uint? DestinationSocketScopeId,
    string NextHopAddress,
    uint? NextHopSocketScopeId,
    byte SitePrefixLengthRaw,
    uint ValidLifetimeSeconds,
    uint PreferredLifetimeSeconds,
    uint RouteMetricOffsetRaw,
    uint ProtocolRaw,
    bool Loopback,
    bool AutoconfigureAddress,
    bool Publish,
    bool Immortal,
    uint AgeSeconds,
    uint OriginRaw);
