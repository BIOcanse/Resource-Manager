using ResourceManager.App.Domain.Optimization;

namespace ResourceManager.App.Application.Optimization;

public interface IOptimizationLevel3Service
{
    Task<OptimizationLevel3Preview> PreviewAsync(
        OptimizationLevel3Request request,
        CancellationToken cancellationToken);

    Task<OptimizationLevel3ApplyResult> ApplyAsync(
        OptimizationLevel3Request request,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<OptimizationLevel3Record>> GetRecordsAsync(CancellationToken cancellationToken);

    Task<OptimizationLevel3RestoreResult> RestoreAsync(
        OptimizationLevel3RestoreRequest request,
        CancellationToken cancellationToken);
}
