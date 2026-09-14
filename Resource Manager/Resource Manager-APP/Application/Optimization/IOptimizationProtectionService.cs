using ResourceManager.App.Domain.Optimization;

namespace ResourceManager.App.Application.Optimization;

public interface IOptimizationProtectionService
{
    int ProtectedTargetCount { get; }

    Task<ProtectedOptimizationTarget?> ProtectReportAsync(
        OptimizationReportItem report,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<ProtectedOptimizationTarget>> GetProtectedTargetsAsync(
        CancellationToken cancellationToken);

    Task<IReadOnlyList<ProtectedOptimizationTarget>> RefreshProtectedTargetsAsync(
        CancellationToken cancellationToken);

    Task<bool> RemoveProtectedTargetAsync(
        string protectionId,
        CancellationToken cancellationToken);

    Task<ProtectedOptimizationTarget?> UpdateProtectedTargetLevelAsync(
        string protectionId,
        int protectionLevel,
        CancellationToken cancellationToken);

    Task<bool> IsTargetProtectedAsync(
        OptimizationReportTarget target,
        CancellationToken cancellationToken);
}
