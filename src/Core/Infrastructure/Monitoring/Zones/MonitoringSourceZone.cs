using ResourceManager.Adapter;
using ResourceManager.App.Application.Adaptation;
using ResourceManager.App.Domain.Adaptation;

namespace ResourceManager.App.Infrastructure.Monitoring;

public class MonitoringSourceZone : IResourceManagerSelfComputeZone
{
    private readonly object gate = new();
    private ResourceManagerComputeZoneMode currentMode = ResourceManagerComputeZoneMode.Normal;

    public MonitoringSourceZone(string sourceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);
        DisplayName = sourceId;
        ZoneKey = AdapterResourceKey.FromString($"resource-manager:zone:{sourceId}");
    }

    public ulong ZoneKey { get; }

    public string DisplayName { get; }

    public ResourceManagerComputeZoneMode CurrentMode
    {
        get
        {
            lock (gate)
            {
                return currentMode;
            }
        }
    }

    public bool CanRead => CurrentMode != ResourceManagerComputeZoneMode.Freeze;

    public bool IsLowPower => CurrentMode == ResourceManagerComputeZoneMode.LowPower;

    public void ApplyMode(ResourceManagerComputeZoneMode mode)
    {
        lock (gate)
        {
            currentMode = mode;
        }
    }
}
