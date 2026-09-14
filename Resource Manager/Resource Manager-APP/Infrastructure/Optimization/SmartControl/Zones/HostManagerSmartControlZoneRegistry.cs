using ResourceManager.App.Application.Adaptation;
using ResourceManager.App.Application.Optimization.SmartControl;
using ResourceManager.App.Domain.Adaptation;

namespace ResourceManager.App.Infrastructure.Optimization;

public sealed class HostManagerSmartControlZoneRegistry : IHostManagerSmartControlZoneRegistry
{
    private readonly IReadOnlyDictionary<string, HostManagerSmartControlZone> zones;
    private readonly IReadOnlyList<IResourceManagerSelfComputeZone> zoneList;

    public HostManagerSmartControlZoneRegistry(IEnumerable<HostManagerSmartControlZone> zones)
    {
        this.zones = zones.ToDictionary(static zone => zone.DisplayName, StringComparer.OrdinalIgnoreCase);
        zoneList = this.zones.Values
            .OrderBy(static zone => zone.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public bool CanRun(string zoneId)
    {
        return !zones.TryGetValue(zoneId, out var zone)
            || zone.CanRun;
    }

    public bool IsLowPower(string zoneId)
    {
        return zones.TryGetValue(zoneId, out var zone)
            && zone.IsLowPower;
    }

    public bool HasAnyLowPowerZone()
    {
        return zoneList.Any(static zone => zone.CurrentMode == ResourceManagerComputeZoneMode.LowPower);
    }

    public ResourceManagerComputeZoneMode GetMode(string zoneId)
    {
        return zones.TryGetValue(zoneId, out var zone)
            ? zone.CurrentMode
            : ResourceManagerComputeZoneMode.Normal;
    }

    public IReadOnlyList<IResourceManagerSelfComputeZone> GetZones()
    {
        return zoneList;
    }
}
