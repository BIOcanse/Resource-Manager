using ResourceManager.App.Domain.Optimization;

namespace ResourceManager.App.Application.Optimization;

public interface IOptimizationLevel1Store
{
    Task<IReadOnlyList<OptimizationLevel1Record>> LoadAsync(CancellationToken cancellationToken);

    Task SaveAsync(
        IReadOnlyList<OptimizationLevel1Record> records,
        CancellationToken cancellationToken);
}
