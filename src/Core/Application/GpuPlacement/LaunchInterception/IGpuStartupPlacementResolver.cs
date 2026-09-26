using ResourceManager.App.Domain.GpuPlacement;

namespace ResourceManager.App.Application.GpuPlacement;

public interface IGpuStartupPlacementResolver
{
    Task<GpuStartupPlacementDecision> ResolveAsync(
        GpuStartupPlacementRequest request,
        CancellationToken cancellationToken);
}
