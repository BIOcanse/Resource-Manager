using ResourceManager.App.Domain.GpuPlacement;

namespace ResourceManager.App.Application.GpuPlacement;

public interface IGpuPlacementCapabilityReader
{
    GpuPlacementProcessCapabilities Evaluate(
        string processKey,
        string? executablePath,
        string? observedArchitecture,
        GpuGraphicsApi? graphicsApi);
}
