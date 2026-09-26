using ResourceManager.App.Application.Adaptation;
using ResourceManager.App.Domain.Adaptation;

namespace ResourceManager.App.Application.Monitoring;

public interface IMonitoringSourceZoneRegistry
{
    event Action? Changed;

    long Version { get; }

    bool CanRead(string sourceId);

    bool IsLowPower(string sourceId);

    ResourceManagerComputeZoneMode GetMode(string sourceId);

    IResourceManagerSelfComputeZone GetZone(string sourceId);

    IReadOnlyList<IResourceManagerSelfComputeZone> GetZones();

    IReadOnlyList<string> GetSourceIds();

    bool Register(IResourceManagerSelfComputeZone zone);

    bool Unregister(string sourceId);
}
