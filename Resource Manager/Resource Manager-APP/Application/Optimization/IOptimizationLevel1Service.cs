using ResourceManager.App.Domain.Optimization;

namespace ResourceManager.App.Application.Optimization;

public interface IOptimizationLevel1Service
{
    Task<OptimizationLevel1Preview> PreviewAsync(
        OptimizationLevel1Request request,
        CancellationToken cancellationToken);

    Task<OptimizationLevel1ApplyResult> ApplyAsync(
        OptimizationLevel1Request request,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<OptimizationLevel1Record>> GetRecordsAsync(CancellationToken cancellationToken);

    Task<OptimizationLevel1RestoreResult> RestoreAsync(
        OptimizationLevel1RestoreRequest request,
        CancellationToken cancellationToken);
}
