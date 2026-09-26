using ResourceManager.App.Domain.Optimization;

namespace ResourceManager.App.Application.Optimization;

public interface IOptimizationProtectionPlacementStore
{
    Task<IReadOnlyList<OptimizationProtectionPlacementRecord>> LoadAsync(CancellationToken cancellationToken);

    Task SaveAsync(
        IReadOnlyList<OptimizationProtectionPlacementRecord> records,
        CancellationToken cancellationToken);
}
