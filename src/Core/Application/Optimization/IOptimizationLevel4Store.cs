using ResourceManager.App.Domain.Optimization;

namespace ResourceManager.App.Application.Optimization;

public interface IOptimizationLevel4Store
{
    Task<IReadOnlyList<OptimizationLevel4Record>> LoadAsync(CancellationToken cancellationToken);

    Task SaveAsync(
        IReadOnlyList<OptimizationLevel4Record> records,
        CancellationToken cancellationToken);
}
