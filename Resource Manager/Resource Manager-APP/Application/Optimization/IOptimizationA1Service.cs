using ResourceManager.App.Domain.Optimization;

namespace ResourceManager.App.Application.Optimization;

public interface IOptimizationA1Service
{
    Task<OptimizationA1Preview> PreviewAsync(
        OptimizationA1Request request,
        CancellationToken cancellationToken);

    Task<OptimizationA1ApplyResult> ApplyAsync(
        OptimizationA1Request request,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<OptimizationA1Record>> GetRecordsAsync(CancellationToken cancellationToken);

    Task<OptimizationA1RestoreResult> RestoreAsync(
        OptimizationA1RestoreRequest request,
        CancellationToken cancellationToken);
}
