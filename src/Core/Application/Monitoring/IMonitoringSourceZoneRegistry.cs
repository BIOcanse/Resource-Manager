
namespace ResourceManager.App.Application.Monitoring;

public interface IMonitoringSourceZoneRegistry
{
    event Action? Changed;

    long Version { get; }

    IMonitoringSourceZone GetZone(string sourceId);

    IReadOnlyList<IMonitoringSourceZone> GetZones();

    IReadOnlyList<string> GetSourceIds();

    bool Register(IMonitoringSourceZone zone);

    bool Unregister(string sourceId);
}
