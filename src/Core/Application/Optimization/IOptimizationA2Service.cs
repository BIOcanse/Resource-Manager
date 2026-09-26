using ResourceManager.App.Domain.Optimization;

namespace ResourceManager.App.Application.Optimization;

public interface IOptimizationA2Service
{
    Task<OptimizationA2Preview> PreviewAsync(
        OptimizationA2Request request,
        CancellationToken cancellationToken);

    Task<OptimizationA2ApplyResult> ApplyAsync(
        OptimizationA2Request request,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<OptimizationA2Record>> GetRecordsAsync(CancellationToken cancellationToken);

    Task<OptimizationA2RestoreResult> RestoreAsync(
        OptimizationA2RestoreRequest request,
        CancellationToken cancellationToken);
}
