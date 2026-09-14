using ResourceManager.App.Domain.Optimization;

namespace ResourceManager.App.Infrastructure.Optimization.Reports;

public static class OptimizationReportMaintenancePolicy
{
    private static readonly TimeSpan DefaultActiveWindow = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan DefaultRetentionWindow = TimeSpan.FromDays(1);

    public static bool IsWithinActiveWindow(
        OptimizationObservationRecord record,
        DateTimeOffset now,
        TimeSpan? minimumActiveWindow = null)
    {
        if (IsRollingObservationType(record.Type))
        {
            return true;
        }

        var activeWindow = ActiveWindow(record);
        if (minimumActiveWindow is { } floor && floor > activeWindow)
        {
            activeWindow = floor;
        }

        return now - record.LastObservedAt <= activeWindow;
    }

    public static bool ShouldRetain(OptimizationObservationRecord record, DateTimeOffset now)
    {
        return now - record.LastObservedAt <= RetentionWindow(record);
    }

    private static TimeSpan ActiveWindow(OptimizationObservationRecord record)
    {
        if (record.StaleAfterSeconds is > 0)
        {
            return TimeSpan.FromSeconds(record.StaleAfterSeconds.Value);
        }

        return record.Type switch
        {
            OptimizationReportTypes.BackgroundPersistentMicroUsage => TimeSpan.FromMinutes(15),
            OptimizationReportTypes.DiskPressure => TimeSpan.FromMinutes(15),
            OptimizationReportTypes.SoftwareFootprint => TimeSpan.FromHours(2),
            _ => DefaultActiveWindow
        };
    }

    private static TimeSpan RetentionWindow(OptimizationObservationRecord record)
    {
        if (IsRollingObservationType(record.Type))
        {
            return TimeSpan.FromDays(8);
        }

        if (!string.IsNullOrWhiteSpace(record.AdvisoryFamily))
        {
            var active = ActiveWindow(record);
            return active > TimeSpan.FromHours(6) ? TimeSpan.FromDays(7) : TimeSpan.FromDays(1);
        }

        return record.Type == OptimizationReportTypes.SoftwareFootprint
            ? TimeSpan.FromDays(7)
            : DefaultRetentionWindow;
    }

    private static bool IsRollingObservationType(string type)
    {
        return type is OptimizationReportTypes.RollingDiskTraffic
            or OptimizationReportTypes.RollingDiskWrite
            or OptimizationReportTypes.RollingNetworkTraffic
            or OptimizationReportTypes.RollingNetworkActivity;
    }
}
