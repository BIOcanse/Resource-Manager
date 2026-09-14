using ResourceManager.App.Domain.GpuPlacement;

namespace ResourceManager.App.Application.GpuPlacement;

public interface IGpuPlacementPolicyStore
{
    Task<GpuPlacementPolicyDocument> GetAsync(CancellationToken cancellationToken);

    Task<GpuPlacementSoftwarePolicy> GetOrCreateSoftwarePolicyAsync(
        string softwareId,
        string softwareName,
        string? softwareKind,
        CancellationToken cancellationToken);

    Task<GpuPlacementSoftwarePolicy> SaveSoftwarePolicyAsync(
        GpuPlacementSoftwarePolicy policy,
        CancellationToken cancellationToken);

    Task<GpuPlacementProcessPolicy> SaveProcessPolicyAsync(
        GpuPlacementProcessPolicy policy,
        CancellationToken cancellationToken);
}
