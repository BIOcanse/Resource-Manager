using ResourceManager.App.Domain.Optimization;

namespace ResourceManager.App.Application.Optimization;

public interface IOptimizationLevel4Service
{
    Task<OptimizationLevel4Preview> PreviewAsync(
        OptimizationLevel4Request request,
        CancellationToken cancellationToken);

    Task<OptimizationLevel4ApplyResult> ApplyAsync(
        OptimizationLevel4Request request,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<OptimizationLevel4Record>> GetRecordsAsync(CancellationToken cancellationToken);

    Task<OptimizationLevel4RestoreResult> RestoreAsync(
        OptimizationLevel4RestoreRequest request,
        CancellationToken cancellationToken);
}
