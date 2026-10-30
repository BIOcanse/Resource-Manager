using ResourceManager.App.Application.Monitoring;

namespace ResourceManager.App.Infrastructure.Monitoring;

public sealed class MonitoringSourceZoneRegistry : IMonitoringSourceZoneRegistry
{
    private readonly object gate = new();
    private IReadOnlyDictionary<string, IMonitoringSourceZone> zones;
    private IReadOnlyList<IMonitoringSourceZone> zoneList;
    private long version;

    public event Action? Changed;

    public long Version => Volatile.Read(ref version);

    public MonitoringSourceZoneRegistry()
        : this(MonitoringSourceZoneCatalog.CreateDefaultZones(AppContext.BaseDirectory))
    {
    }

    public MonitoringSourceZoneRegistry(IEnumerable<IMonitoringSourceZone> sourceZones)
    {
        zones = sourceZones
            .ToDictionary(static zone => zone.DisplayName, StringComparer.OrdinalIgnoreCase);
        zoneList = zones.Values
            .OrderBy(static zone => zone.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public IMonitoringSourceZone GetZone(string sourceId)
    {
        return zones[sourceId];
    }

    public IReadOnlyList<IMonitoringSourceZone> GetZones()
    {
        return zoneList;
    }

    public IReadOnlyList<string> GetSourceIds()
    {
        return zoneList
            .Select(static zone => zone.DisplayName)
            .ToArray();
    }

    public bool Register(IMonitoringSourceZone zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        lock (gate)
        {
            if (zones.TryGetValue(zone.DisplayName, out var existing)
                && ReferenceEquals(existing, zone))
            {
                return false;
            }

            var next = new Dictionary<string, IMonitoringSourceZone>(zones, StringComparer.OrdinalIgnoreCase)
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

            var next = new Dictionary<string, IMonitoringSourceZone>(zones, StringComparer.OrdinalIgnoreCase);
            next.Remove(sourceId);
            Publish(next);
        }

        Changed?.Invoke();
        return true;
    }

    private void Publish(IReadOnlyDictionary<string, IMonitoringSourceZone> next)
    {
        zones = next;
        zoneList = next.Values
            .OrderBy(static zone => zone.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        Interlocked.Increment(ref version);
    }
}
