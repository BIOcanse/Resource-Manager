using ResourceManager.App.Application.Adaptation;
using ResourceManager.App.Application.Monitoring;
using ResourceManager.App.Domain.Adaptation;

namespace ResourceManager.App.Infrastructure.Monitoring;

public sealed class MonitoringSourceZoneRegistry : IMonitoringSourceZoneRegistry
{
    private readonly object gate = new();
    private IReadOnlyDictionary<string, IResourceManagerSelfComputeZone> zones;
    private IReadOnlyList<IResourceManagerSelfComputeZone> zoneList;
    private long version;

    public event Action? Changed;

    public long Version => Volatile.Read(ref version);

    public MonitoringSourceZoneRegistry()
        : this(MonitoringSourceZoneCatalog.CreateDefaultZones(AppContext.BaseDirectory))
    {
    }

    public MonitoringSourceZoneRegistry(IEnumerable<IResourceManagerSelfComputeZone> sourceZones)
    {
        zones = sourceZones
            .ToDictionary(static zone => zone.DisplayName, StringComparer.OrdinalIgnoreCase);
        zoneList = zones.Values
            .OrderBy(static zone => zone.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public bool CanRead(string sourceId)
    {
        return !zones.TryGetValue(sourceId, out var zone)
            || zone.CurrentMode != ResourceManagerComputeZoneMode.Freeze;
    }

    public bool IsLowPower(string sourceId)
    {
        return zones.TryGetValue(sourceId, out var zone)
            && zone.CurrentMode == ResourceManagerComputeZoneMode.LowPower;
    }

    public ResourceManagerComputeZoneMode GetMode(string sourceId)
    {
        return zones.TryGetValue(sourceId, out var zone)
            ? zone.CurrentMode
            : ResourceManagerComputeZoneMode.Normal;
    }

    public IResourceManagerSelfComputeZone GetZone(string sourceId)
    {
        return zones[sourceId];
    }

    public IReadOnlyList<IResourceManagerSelfComputeZone> GetZones()
    {
        return zoneList;
    }

    public IReadOnlyList<string> GetSourceIds()
    {
        return zoneList
            .Select(static zone => zone.DisplayName)
            .ToArray();
    }

    public bool Register(IResourceManagerSelfComputeZone zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        lock (gate)
        {
            if (zones.TryGetValue(zone.DisplayName, out var existing)
                && ReferenceEquals(existing, zone))
            {
                return false;
            }

            var next = new Dictionary<string, IResourceManagerSelfComputeZone>(zones, StringComparer.OrdinalIgnoreCase)
            {
                [zone.DisplayName] = zone
            };
            Publish(next);
        }

        Changed?.Invoke();
        return true;
    }

    public bool Unregister(string sourceId)
    {
        if (string.IsNullOrWhiteSpace(sourceId))
        {
            return false;
        }

        lock (gate)
        {
            if (!zones.ContainsKey(sourceId))
            {
                return false;
            }

            var next = new Dictionary<string, IResourceManagerSelfComputeZone>(zones, StringComparer.OrdinalIgnoreCase);
            next.Remove(sourceId);
            Publish(next);
        }

        Changed?.Invoke();
        return true;
    }

    private void Publish(IReadOnlyDictionary<string, IResourceManagerSelfComputeZone> next)
    {
        zones = next;
        zoneList = next.Values
            .OrderBy(static zone => zone.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        Interlocked.Increment(ref version);
    }
}
