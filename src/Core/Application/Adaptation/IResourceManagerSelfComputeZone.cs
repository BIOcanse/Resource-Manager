using ResourceManager.App.Domain.Adaptation;

namespace ResourceManager.App.Application.Adaptation;

public interface IResourceManagerSelfComputeZone
{
    ulong ZoneKey { get; }
    string DisplayName { get; }
    ResourceManagerComputeZoneMode CurrentMode { get; }
    void ApplyMode(ResourceManagerComputeZoneMode mode);
}
