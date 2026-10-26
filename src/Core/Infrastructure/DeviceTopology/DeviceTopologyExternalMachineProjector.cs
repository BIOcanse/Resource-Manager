using ResourceManager.App.Domain.DeviceTopology;
using ResourceManager.ReadWrite.Windows.Devices.Models;

namespace ResourceManager.App.Infrastructure.DeviceTopology;

/// <summary>Uses documented USB4NET compatible ID and current adapter state, without guessing from names.</summary>
public static class DeviceTopologyExternalMachineProjector
{
    public const string Usb4NetCompatibleId = @"USB4\USB4NET";

    public static IReadOnlyList<DeviceTopologyExternalMachine>? Project(
        DeviceTopologyNativeSnapshot native,
        DeviceTopologyNetworkAdapterSnapshot network)
    {
        var incomplete = native.Error is not null;
        var candidates = native.Devices.Values.Where(static device =>
            device.CompatibleIds.Contains(Usb4NetCompatibleId, StringComparer.OrdinalIgnoreCase)).ToArray();
        if (candidates.Length == 0) return incomplete ? null : [];
        incomplete |= network.Error is not null;

        var peers = new List<DeviceTopologyExternalMachine>();
        foreach (var device in candidates.OrderBy(static device => device.DeviceId, StringComparer.Ordinal))
        {
            if (!network.Adapters.TryGetValue(device.DeviceId, out var adapter)
                || adapter.MediaConnectState is not (1 or 2))
            {
                incomplete = true;
                continue;
            }
            if (adapter.MediaConnectState == 2) continue;
            // USB4NET confirms a peer, not its model or silicon. These are intentionally unavailable.
            // Workstation + one SoC is the user's external-host category, not a hardware classification.
            peers.Add(new(device.DeviceId, null, "workstation", null, [new("soc", null)],
                new("windows-usb4net", Usb4NetCompatibleId, 1, "windows-peer-properties-unavailable")));
        }
        return peers.Count > 0 || !incomplete ? peers : null;
    }
}
