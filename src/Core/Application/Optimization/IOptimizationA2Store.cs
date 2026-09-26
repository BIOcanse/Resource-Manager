using ResourceManager.App.Domain.Optimization;

namespace ResourceManager.App.Application.Optimization;

public interface IOptimizationA2Store
{
    Task<IReadOnlyList<OptimizationA2Record>> LoadAsync(CancellationToken cancellationToken);

    Task SaveAsync(
        IReadOnlyList<OptimizationA2Record> records,
        CancellationToken cancellationToken);
}
