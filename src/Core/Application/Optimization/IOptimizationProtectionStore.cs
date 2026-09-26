using ResourceManager.App.Domain.Optimization;

namespace ResourceManager.App.Application.Optimization;

public interface IOptimizationProtectionStore
{
    Task<IReadOnlyList<ProtectedOptimizationTarget>> LoadAsync(CancellationToken cancellationToken);

    Task SaveAsync(
        IReadOnlyList<ProtectedOptimizationTarget> targets,
        CancellationToken cancellationToken);
}
