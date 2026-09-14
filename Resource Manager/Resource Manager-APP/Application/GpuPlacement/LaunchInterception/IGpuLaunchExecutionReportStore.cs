using ResourceManager.App.Domain.GpuPlacement;

namespace ResourceManager.App.Application.GpuPlacement;

public interface IGpuLaunchExecutionReportStore
{
    Task<IReadOnlyList<GpuLaunchExecutionReport>> GetLatestAsync(CancellationToken cancellationToken);

    Task<GpuLaunchExecutionReport> RecordAsync(
        GpuLaunchExecutionReport report,
        CancellationToken cancellationToken);
}
