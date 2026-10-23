using ResourceManager.App.Infrastructure.Monitoring;

namespace ResourceManager.ReadWrite.Windows.Metrics.Topology;

public static class WindowsAmdCcdTopology
{
    public static uint EffectiveMaximumInstanceCount(IEnumerable<NativeMetricSnapshotRuleTemplate> templates)
        => templates.Where(static template => template.SourceId == NativeMetricSnapshotSources.AmdSmn
                && template.ScopeExpansion == 13)
            .Select(static template => template.MaximumInstanceCount).DefaultIfEmpty(0U).Min();
}
