using ResourceManager.App.Domain.Optimization;

namespace ResourceManager.App.Application.Optimization;

public interface IOptimizationLevel3Store
{
    Task<IReadOnlyList<OptimizationLevel3Record>> LoadAsync(CancellationToken cancellationToken);

    Task SaveAsync(
        IReadOnlyList<OptimizationLevel3Record> records,
        CancellationToken cancellationToken);
}
