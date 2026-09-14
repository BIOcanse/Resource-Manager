using System.Diagnostics;
using ResourceManager.App.Domain.ResourceBreakdown;

namespace ResourceManager.App.Infrastructure.ResourceBreakdown;

public sealed partial class DxgkrnlVidMmEtwResidualBreakdownProvider
{
    private static bool IsProcessAlive(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsGpuVramMetric(string metricId)
    {
        return metricId.StartsWith(ResourceBreakdownMetricIds.GpuPrefix, StringComparison.OrdinalIgnoreCase)
            && metricId.EndsWith(".vram", StringComparison.OrdinalIgnoreCase);
    }

    private static double Percent(double value, double denominator)
    {
        return denominator > 0 ? Math.Max(0, value) * 100 / denominator : 0;
    }

    private static string FormatBytesWithCapacity(double value, double capacity)
    {
        return capacity > 0 ? $"{FormatBytes(value)} / {FormatBytes(capacity)}" : FormatBytes(value);
    }

    private static string FormatBytes(double value)
    {
        var sanitized = Math.Max(0, value);
        const double kib = 1024d;
        const double mib = kib * 1024d;
        const double gib = mib * 1024d;
        if (sanitized >= gib)
        {
            return $"{sanitized / gib:0.##} GB";
        }

        if (sanitized >= mib)
        {
            return $"{sanitized / mib:0.#} MB";
        }

        if (sanitized >= kib)
        {
            return $"{sanitized / kib:0} KB";
        }

        return $"{sanitized:0} B";
    }
}
