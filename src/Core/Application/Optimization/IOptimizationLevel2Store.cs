using ResourceManager.App.Domain.Optimization;

namespace ResourceManager.App.Application.Optimization;

public interface IOptimizationLevel2Store
{
    Task<IReadOnlyList<OptimizationLevel2Record>> LoadAsync(CancellationToken cancellationToken);

    Task SaveAsync(
        IReadOnlyList<OptimizationLevel2Record> records,
        CancellationToken cancellationToken);
}
