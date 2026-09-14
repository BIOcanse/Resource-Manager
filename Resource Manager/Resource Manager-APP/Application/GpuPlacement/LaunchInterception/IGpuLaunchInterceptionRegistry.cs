using ResourceManager.App.Domain.GpuPlacement;

namespace ResourceManager.App.Application.GpuPlacement;

public interface IGpuLaunchInterceptionRegistry
{
    GpuLaunchInterceptionStatus Apply(GpuPlacementProcessPolicy policy);

    GpuLaunchInterceptionStatus GetStatus(GpuPlacementProcessPolicy policy);

    IReadOnlyList<GpuLaunchInterceptionStatus> Reconcile(GpuPlacementPolicyDocument document);

    GpuLaunchInterceptionCleanupResult RemoveAllOwnedRules();
}
