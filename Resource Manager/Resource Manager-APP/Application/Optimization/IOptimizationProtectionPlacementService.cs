using ResourceManager.App.Domain.Optimization;

namespace ResourceManager.App.Application.Optimization;

public interface IOptimizationProtectionPlacementService
{
    Task<OptimizationProtectionPlacementPreview> PreviewAsync(
        OptimizationProtectionPlacementRequest request,
        CancellationToken cancellationToken);

    Task<OptimizationProtectionPlacementApplyResult> ApplyAsync(
        OptimizationProtectionPlacementRequest request,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<OptimizationProtectionPlacementRecord>> GetRecordsAsync(CancellationToken cancellationToken);

    Task<OptimizationProtectionPlacementRestoreResult> RestoreAsync(
        OptimizationProtectionPlacementRestoreRequest request,
        CancellationToken cancellationToken);
}
