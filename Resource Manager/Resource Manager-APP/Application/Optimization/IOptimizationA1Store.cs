using ResourceManager.App.Domain.Optimization;

namespace ResourceManager.App.Application.Optimization;

public interface IOptimizationA1Store
{
    Task<IReadOnlyList<OptimizationA1Record>> LoadAsync(CancellationToken cancellationToken);

    Task SaveAsync(
        IReadOnlyList<OptimizationA1Record> records,
        CancellationToken cancellationToken);
}
