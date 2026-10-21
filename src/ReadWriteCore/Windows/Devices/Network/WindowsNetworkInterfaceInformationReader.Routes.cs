using ResourceManager.ReadWrite.Windows.Devices.Models;

namespace ResourceManager.ReadWrite.Windows.Devices;

public sealed partial class WindowsNetworkInterfaceInformationReader
{
    private IReadOnlyDictionary<ulong, IReadOnlyList<NetworkRoute>?> ReadRoutes(
        IReadOnlyDictionary<ulong, WindowsNetworkInterfaceRow> known)
    {
        IReadOnlyList<WindowsNetworkRouteRow>? routes;
        try { routes = WindowsNetworkInterfaceTableDecoder.Routes(api.ReadRoutes()); }
        catch (Exception) { routes = null; }
        var groups = known.Keys.ToDictionary(static key => key, static _ => new List<NetworkRoute>());
        var failed = new HashSet<ulong>();
        foreach (var route in routes ?? [])
        {
            if (!known.TryGetValue(route.Luid, out var row)) continue;
            if (route.Index != row.Index || route.Route is null) { failed.Add(row.Luid); continue; }
            groups[row.Luid].Add(route.Route);
        }
        return known.Keys.ToDictionary(static key => key,
            key => routes is null || failed.Contains(key) ? null : Normalize(groups[key]));
    }

    private static IReadOnlyList<NetworkRoute> Normalize(IEnumerable<NetworkRoute> values)
        => Array.AsReadOnly(values.OrderBy(static v => v.AddressFamily, StringComparer.Ordinal)
            .ThenBy(static v => v.DestinationAddress, StringComparer.Ordinal).ThenBy(static v => v.DestinationPrefixLength)
            .ThenBy(static v => v.DestinationSocketScopeId).ThenBy(static v => v.NextHopAddress, StringComparer.Ordinal)
            .ThenBy(static v => v.NextHopSocketScopeId).ThenBy(static v => v.SitePrefixLengthRaw)
            .ThenBy(static v => v.ValidLifetimeSeconds).ThenBy(static v => v.PreferredLifetimeSeconds)
            .ThenBy(static v => v.RouteMetricOffsetRaw).ThenBy(static v => v.ProtocolRaw)
            .ThenBy(static v => v.Loopback).ThenBy(static v => v.AutoconfigureAddress).ThenBy(static v => v.Publish)
            .ThenBy(static v => v.Immortal).ThenBy(static v => v.AgeSeconds).ThenBy(static v => v.OriginRaw).ToArray());
}
