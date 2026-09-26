using ResourceManager.App.Application.ResourceBreakdown;
using ResourceManager.App.Domain.ResourceBreakdown;

namespace ResourceManager.App.Infrastructure.ResourceBreakdown;

public sealed class StaticResourceResidualBreakdownProvider : IResourceResidualBreakdownProvider
{
    public IReadOnlyList<ResourceProcessSegment> CreateResidualSegments(ResourceResidualBreakdownRequest request)
    {
        if (request.Value <= 0)
        {
            return [];
        }

        return
        [
            new ResourceProcessSegment(
                -1,
                CategoryLabel(request.MetricId),
                null,
                request.Value,
                request.SystemPercent,
                100,
                null,
                null,
                ResourceProcessAttributionKinds.SystemResidual)
        ];
    }

    private static string CategoryLabel(string metricId)
    {
        if (metricId.StartsWith(ResourceBreakdownMetricIds.GpuPrefix, StringComparison.OrdinalIgnoreCase)
            && metricId.EndsWith(".vram", StringComparison.OrdinalIgnoreCase))
        {
            return "GPU 驱动 / WDDM / 桌面合成保留（未细分）";
        }

        if (metricId.Equals(ResourceBreakdownMetricIds.MemoryUsage, StringComparison.OrdinalIgnoreCase))
        {
            return "系统 / 内核 / 驱动保留（未细分）";
        }

        if (metricId.Equals(ResourceBreakdownMetricIds.VirtualMemoryUsage, StringComparison.OrdinalIgnoreCase))
        {
            return "系统提交 / 内核保留（未细分）";
        }

        return "系统 / 驱动保留（未细分）";
    }
}
