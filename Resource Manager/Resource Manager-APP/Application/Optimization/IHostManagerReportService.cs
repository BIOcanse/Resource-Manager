using ResourceManager.App.Domain.Optimization;

namespace ResourceManager.App.Application.Optimization;

public interface IHostManagerReportService
{
    Task<OptimizationReportOverview> GetReportsAsync(CancellationToken cancellationToken);

    Task<OptimizationReportOverview> RefreshReportsAsync(CancellationToken cancellationToken);

    Task<OptimizationReportItem?> GetReportAsync(
        string reportId,
        CancellationToken cancellationToken);

    Task<TrustedOptimizationTarget?> DismissReportAsync(
        string reportId,
        CancellationToken cancellationToken);

    Task<TrustedOptimizationTarget?> TrustReportAsync(
        string reportId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<TrustedOptimizationTarget>> GetTrustedTargetsAsync(
        CancellationToken cancellationToken);

    Task<IReadOnlyList<TrustedOptimizationTarget>> RefreshTrustedTargetsAsync(
        CancellationToken cancellationToken);

    Task<bool> RemoveTrustedTargetAsync(
        string trustId,
        CancellationToken cancellationToken);

    OptimizationRecorderStatus GetStatus();
}
