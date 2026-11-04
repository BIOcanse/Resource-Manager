using ResourceManager.App.Application.Optimization;
using ResourceManager.App.Application.SoftwareIssues;

namespace ResourceManager.App.Infrastructure.DeviceActions;

// F3 seam: register another adapter for persisted display-reset entries. Only trusted
// report producers supply device ids; neither endpoint accepts an id from the caller.
public sealed record DeviceRestartReportTarget(string ReportType, string? DriverModule, string? DeviceInstanceId);
public interface IDeviceRestartReportBridge
{
    Task<DeviceRestartReportTarget?> ReadAsync(string reportEntryId, CancellationToken cancellationToken);
}

internal sealed class SystemInterruptDeviceReportBridge(
    IHostManagerReportService reports,
    IOptimizationSoftwareIssueSignalSource issues) : IDeviceRestartReportBridge
{
    public async Task<DeviceRestartReportTarget?> ReadAsync(string reportEntryId, CancellationToken cancellationToken)
    {
        var report = await reports.GetReportAsync(reportEntryId, cancellationToken);
        if (report is null) return null;
        // event: borrow the existing report producer's named driver, never resample ETW.
        var signal = issues.ReadSoftwareIssueSnapshot().Signals.FirstOrDefault(item => item.ReportId == report.Id);
        return new(report.Type, signal?.ArtifactPath, null);
    }
}
