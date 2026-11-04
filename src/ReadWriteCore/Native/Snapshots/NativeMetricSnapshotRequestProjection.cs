using System.Collections.Immutable;
using ResourceManager.App.Domain.Metrics;

namespace ResourceManager.App.Infrastructure.Monitoring;

public static class NativeMetricSnapshotRequestProjection
{
    public static ImmutableArray<ulong> CreateMetricHandles(
        MetricSampleRequest request,
        NativeMetricSnapshotCatalogProjection catalog)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(catalog);
        if (request.IsEmpty && !request.IsCatalogProbe)
        {
            return [];
        }

        var handles = catalog.MetricHandles.Where(pair => request.Includes(pair.Key))
            .Select(static pair => pair.Value).ToHashSet();
        if (request.IncludesAllGpuCoreMetrics)
        {
            handles.UnionWith(catalog.LegacyMetricHandles.Values.Where(handle =>
                catalog.MetricBindings[handle].FieldCode is MetricFieldCode.GpuUsage
                    or MetricFieldCode.GpuMemoryUsed or MetricFieldCode.GpuMemoryPercent
                    or MetricFieldCode.GpuEngineBusy));
            handles.UnionWith(catalog.MetricBindings.Values
                .Where(static binding => binding.FieldCode == MetricFieldCode.GpuEngineBusy)
                .Select(static binding => binding.MetricHandle));
        }
        foreach (var handle in handles.ToArray())
        {
            var binding = catalog.MetricBindings[handle];
            var support = binding.FieldCode switch
            {
                MetricFieldCode.MemoryUsed => MetricFieldCode.MemoryTotal,
                MetricFieldCode.CommitUsed => MetricFieldCode.CommitLimit,
                MetricFieldCode.GpuMemoryUsed => MetricFieldCode.GpuMemoryTotal,
                _ => MetricFieldCode.Unknown
            };
            if (support == MetricFieldCode.Unknown) continue;
            handles.UnionWith(catalog.MetricBindings.Where(pair => pair.Value.ScopeHandle == binding.ScopeHandle
                && pair.Value.FieldCode == support).Select(static pair => pair.Key));
        }
        return handles.Order().ToImmutableArray();
    }

}
