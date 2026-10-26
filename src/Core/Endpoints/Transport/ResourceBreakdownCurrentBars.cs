using ResourceManager.App.Domain.Monitoring;
using ResourceManager.App.Domain.ResourceBreakdown;

namespace ResourceManager.App.Endpoints.Transport;

internal static class ResourceBreakdownCurrentBars
{
    internal static ResourceBreakdownBar[] Select(ResourceBreakdownSnapshot source,
        IReadOnlyList<string> ids)
    {
        var bars = source.Bars
            .ToDictionary(static bar => bar.MetricId, StringComparer.OrdinalIgnoreCase);
        var datasets = source.Datasets.ToDictionary(static item => item.DatasetId, StringComparer.OrdinalIgnoreCase);
        return ids.Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(id => ProcessMemoryFactFields.TotalMetricId(id) is null
                && (ResourceBreakdownMetricIds.IsSupported(id) || bars.ContainsKey(id)))
            .Select(id =>
            {
                var dataset = datasets.GetValueOrDefault(SamplingDatasetIds.ForProcessMetric(id));
                var bar = bars.GetValueOrDefault(id) ?? new ResourceBreakdownBar(id,
                    id.StartsWith("disk.", StringComparison.OrdinalIgnoreCase) || id.StartsWith("network.", StringComparison.OrdinalIgnoreCase) ? "B/s"
                    : id.EndsWith("usage", StringComparison.OrdinalIgnoreCase)
                        && (id.StartsWith("gpu.", StringComparison.OrdinalIgnoreCase) || id.Equals("cpu.usage", StringComparison.OrdinalIgnoreCase)) ? "%" : "B",
                    ResourceBreakdownScaleModes.Normalize(id, null),
                    null, null, null, []);
                if (dataset?.Status != ResourceBreakdownSamplingStatuses.Ready || dataset.CapturedAt is null
                    || bar.TotalValue is null || bar.CapacityValue is null || bar.TotalSystemPercent is null)
                    return WithoutReading(bar, UnavailableReason(source, id, bar.UnavailableReason));
                return bar;
            }).ToArray();
    }

    internal static ResourceBreakdownBar WithoutReading(ResourceBreakdownBar bar, string reason)
        => bar with { TotalValue = null, CapacityValue = null, TotalSystemPercent = null,
            SharedValue = null, ProcessSumValue = null, ResidualValue = null, Software = [], UnavailableReason = reason };

    internal static string UnavailableReason(ResourceBreakdownSnapshot source, string id,
        string? observedReason = null, string fallbackReason = "resource-breakdown-total-unavailable")
    {
        if (!string.IsNullOrWhiteSpace(observedReason)) return observedReason;
        var dataset = source.Datasets.FirstOrDefault(item => item.DatasetId.Equals(
            SamplingDatasetIds.ForProcessMetric(id), StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(dataset?.FailureCode)) return dataset.FailureCode;
        if (dataset?.Status == ResourceBreakdownSamplingStatuses.Warming) return "resource-breakdown-warming";
        if (dataset?.Status == ResourceBreakdownSamplingStatuses.Ready
            && id.StartsWith("gpu.", StringComparison.OrdinalIgnoreCase) && id.EndsWith(".vram", StringComparison.OrdinalIgnoreCase)
            && source.Bars.Any(bar => bar.MetricId.Equals(id, StringComparison.OrdinalIgnoreCase) && bar.TotalValue is null))
            return "gpu-dedicated-memory-total-unavailable";
        return fallbackReason;
    }
}
