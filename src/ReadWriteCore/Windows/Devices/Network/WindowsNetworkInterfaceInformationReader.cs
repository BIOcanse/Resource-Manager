using ResourceManager.ReadWrite.Windows.Devices.Models;

namespace ResourceManager.ReadWrite.Windows.Devices;

internal interface IWindowsNetworkInterfaceTableApi
{
    byte[]? ReadInterfaces();
    byte[]? ReadUnicast();
    byte[]? ReadRoutes();
}

public sealed partial class WindowsNetworkInterfaceInformationReader
{
    private readonly IWindowsNetworkInterfaceTableApi api;
    public WindowsNetworkInterfaceInformationReader() : this(new WindowsNetworkInterfaceTableApi()) { }
    internal WindowsNetworkInterfaceInformationReader(IWindowsNetworkInterfaceTableApi api) => this.api = api;

    public IReadOnlyList<DeviceTopologyNetworkInterfaceInformation>? Read()
    {
        IReadOnlyList<WindowsNetworkInterfaceRow>? interfaces;
        try { interfaces = WindowsNetworkInterfaceTableDecoder.Interfaces(api.ReadInterfaces()); }
        catch (Exception) { return null; }
        if (interfaces is null) return null;
        if (interfaces.Count == 0) return [];
        var known = new Dictionary<ulong, WindowsNetworkInterfaceRow>();
        var guids = new HashSet<Guid>();
        foreach (var row in interfaces)
            if (row.Luid == 0 || row.Guid == Guid.Empty || !known.TryAdd(row.Luid, row) || !guids.Add(row.Guid)) return null;
        IReadOnlyList<WindowsNetworkUnicastRow>? addresses;
        try { addresses = WindowsNetworkInterfaceTableDecoder.Unicast(api.ReadUnicast()); }
        catch (Exception) { addresses = null; }
        var groups = known.Keys.ToDictionary(static key => key, static _ => new List<NetworkUnicastAddress>());
        var failed = new HashSet<ulong>();
        foreach (var address in addresses ?? [])
        {
            if (!known.TryGetValue(address.Luid, out var row)) continue;
            if (address.Index != row.Index || address.Address is null) { failed.Add(row.Luid); continue; }
            groups[row.Luid].Add(address.Address);
        }
        var routes = ReadRoutes(known);
        return interfaces.Select(row => {
            var guid = row.Guid.ToString("D").ToUpperInvariant();
            return new DeviceTopologyNetworkInterfaceInformation("windows-network-interface|" + guid, guid, row.Luid, row.Index,
                addresses is null || failed.Contains(row.Luid) ? null : Normalize(groups[row.Luid])) { Routes = routes[row.Luid] };
        }).OrderBy(static row => row.DeviceIdentity, StringComparer.Ordinal).ToArray();
    }

    public Information.DeviceInformationSnapshot ReadFields()
    {
        var rows = Read();
        return Information.WindowsInformationFieldProjection.NetworkInterfaces(rows, rows is null ? null : DateTimeOffset.UtcNow);
    }

    private static IReadOnlyList<NetworkUnicastAddress> Normalize(IEnumerable<NetworkUnicastAddress> values)
        => Array.AsReadOnly(values.OrderBy(static v => v.AddressFamily, StringComparer.Ordinal)
            .ThenBy(static v => v.Address, StringComparer.Ordinal).ThenBy(static v => v.OnLinkPrefixLength)
            .ThenBy(static v => v.SocketScopeId).ThenBy(static v => v.ScopeIdRaw)
            .ThenBy(static v => v.PrefixOriginRaw).ThenBy(static v => v.SuffixOriginRaw).ThenBy(static v => v.DadStateRaw)
            .ThenBy(static v => v.ValidLifetimeSeconds).ThenBy(static v => v.PreferredLifetimeSeconds)
            .ThenBy(static v => v.SkipAsSource).ThenBy(static v => v.CreationWindowsFileTime100NsRaw, StringComparer.Ordinal).ToArray());
}
