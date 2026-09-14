using ResourceManager.App.Domain.Optimization;

namespace ResourceManager.App.Application.Optimization;

public interface IOptimizationLevel2Service
{
    Task<OptimizationLevel2Preview> PreviewAsync(
        OptimizationLevel2Request request,
        CancellationToken cancellationToken);

    Task<OptimizationLevel2ApplyResult> ApplyAsync(
        OptimizationLevel2Request request,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<OptimizationLevel2Record>> GetRecordsAsync(CancellationToken cancellationToken);

    Task<OptimizationLevel2RestoreResult> RestoreAsync(
        OptimizationLevel2RestoreRequest request,
        CancellationToken cancellationToken);
}
