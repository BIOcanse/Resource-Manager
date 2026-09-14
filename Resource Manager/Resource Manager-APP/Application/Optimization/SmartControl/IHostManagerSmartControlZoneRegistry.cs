using ResourceManager.App.Application.Adaptation;
using ResourceManager.App.Domain.Adaptation;

namespace ResourceManager.App.Application.Optimization.SmartControl;

public interface IHostManagerSmartControlZoneRegistry
{
    bool CanRun(string zoneId);

    bool IsLowPower(string zoneId);

    bool HasAnyLowPowerZone();

    ResourceManagerComputeZoneMode GetMode(string zoneId);

    IReadOnlyList<IResourceManagerSelfComputeZone> GetZones();
}
