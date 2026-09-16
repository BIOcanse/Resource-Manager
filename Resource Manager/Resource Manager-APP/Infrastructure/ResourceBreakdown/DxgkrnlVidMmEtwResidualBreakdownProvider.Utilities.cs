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

}
