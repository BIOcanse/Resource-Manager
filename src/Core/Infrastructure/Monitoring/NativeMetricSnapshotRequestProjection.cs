using System.Collections.Immutable;
using ResourceManager.App.Domain.Metrics;

namespace ResourceManager.App.Infrastructure.Monitoring;

internal static class NativeMetricSnapshotRequestProjection
{
    internal static ImmutableArray<ulong> CreateMetricHandles(
        MetricSampleRequest request,
        NativeMetricSnapshotCatalogProjection catalog)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(catalog);
        if (request.IsEmpty && !request.IsCatalogProbe)
        {
            return [];
        }

        var handles = catalog.MetricHandles
            .Where(pair => request.Includes(pair.Key)
                || IsRequiredSupportMetric(pair.Key, request))
            .Select(static pair => pair.Value)
            .Distinct()
            .Order()
            .ToImmutableArray();
        return handles;
    }

    private static bool IsRequiredSupportMetric(
        string metricId,
        MetricSampleRequest request)
    {
        if (metricId.Equals(
                "memory.total",
                StringComparison.OrdinalIgnoreCase))
        {
            return request.Includes("memory.usage");
        }
        if (metricId.Equals(
                "virtualMemory.total",
                StringComparison.OrdinalIgnoreCase))
        {
            return request.Includes("virtualMemory.usage");
        }

        const string suffix = ".vramTotal";
        return metricId.EndsWith(
                suffix,
                StringComparison.OrdinalIgnoreCase)
            && request.Includes(
                metricId[..^"Total".Length]);
    }
}
