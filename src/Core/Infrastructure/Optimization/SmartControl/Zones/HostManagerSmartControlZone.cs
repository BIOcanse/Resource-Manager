using ResourceManager.Adapter;
using ResourceManager.App.Application.Adaptation;
using ResourceManager.App.Domain.Adaptation;

namespace ResourceManager.App.Infrastructure.Optimization;

public abstract class HostManagerSmartControlZone : IResourceManagerSelfComputeZone
{
    private readonly object gate = new();
    private ResourceManagerComputeZoneMode currentMode = ResourceManagerComputeZoneMode.Normal;

    protected HostManagerSmartControlZone(string zoneId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(zoneId);
        DisplayName = zoneId;
        ZoneKey = AdapterResourceKey.FromString($"resource-manager:zone:{zoneId}");
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

    public bool CanRun => CurrentMode != ResourceManagerComputeZoneMode.Freeze;

    public bool IsLowPower => CurrentMode == ResourceManagerComputeZoneMode.LowPower;

    public void ApplyMode(ResourceManagerComputeZoneMode mode)
    {
        lock (gate)
        {
            currentMode = mode;
        }
    }
}
