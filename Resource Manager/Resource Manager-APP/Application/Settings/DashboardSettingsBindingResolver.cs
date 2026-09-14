using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.ResourceTable;
using ResourceManager.App.Domain.Settings;

namespace ResourceManager.App.Application.Settings;

internal static class DashboardSettingsBindingResolver
{
    internal static DashboardSettings Resolve(
        DashboardSettings settings,
        HardwareMetricSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(snapshot);

        var gpuIndexesByIdentity = snapshot.Gpus
            .Where(static gpu => !string.IsNullOrWhiteSpace(gpu.IdentityKey))
            .ToDictionary(
                static gpu => gpu.IdentityKey!,
                static gpu => gpu.Index,
                StringComparer.Ordinal);
        var gpuIdentitiesByIndex = snapshot.Gpus
            .Where(static gpu => !string.IsNullOrWhiteSpace(gpu.IdentityKey))
            .ToDictionary(
                static gpu => gpu.Index,
                static gpu => gpu.IdentityKey!);
        if (gpuIndexesByIdentity.Count == 0)
        {
            return settings;
        }

        var availableGpuMetricIds = snapshot.Items.Keys
            .Where(static metricId => TryParseGpuMetric(metricId, out _, out _))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return settings with
        {
            Cards = settings.Cards
                .Select(card => ResolveCard(
                    card,
                    gpuIndexesByIdentity,
                    gpuIdentitiesByIndex))
                .ToArray(),
            ResourceBars = ResolveResourceBars(
                settings.ResourceBars,
                snapshot,
                gpuIndexesByIdentity,
                gpuIdentitiesByIndex,
                availableGpuMetricIds),
            ResourceTableColumns = ResolveTableColumns(
                settings.ResourceTableColumns,
                snapshot,
                gpuIndexesByIdentity,
                gpuIdentitiesByIndex,
                availableGpuMetricIds,
                includeProcessDetails: false),
            ResourceTableProcessColumns = ResolveTableColumns(
                settings.ResourceTableProcessColumns ?? [],
                snapshot,
                gpuIndexesByIdentity,
                gpuIdentitiesByIndex,
                availableGpuMetricIds,
                includeProcessDetails: true)
        };
    }

    private static IReadOnlyList<ResourceBarSettings> ResolveResourceBars(
        IReadOnlyList<ResourceBarSettings> bars,
        HardwareMetricSnapshot snapshot,
        IReadOnlyDictionary<string, int> gpuIndexesByIdentity,
        IReadOnlyDictionary<int, string> gpuIdentitiesByIndex,
        IReadOnlySet<string> availableGpuMetricIds)
    {
        var resolved = new List<ResourceBarSettings>(bars.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var bar in bars)
        {
            var metric = ResolveMonitoringMetric(
                bar.MetricId,
                bar.Binding,
                gpuIndexesByIdentity,
                gpuIdentitiesByIndex,
                availableGpuMetricIds);
            if (string.IsNullOrWhiteSpace(metric.MetricId)
                || !seen.Add(metric.MetricId))
            {
                continue;
            }

            resolved.Add(bar with
            {
                MetricId = metric.MetricId,
                Binding = metric.Binding
            });
        }

        var currentGpuBars = DashboardSettingsDefaults.CreateResourceBars(
                snapshot.Items,
                gpuIdentitiesByIndex)
            .Where(static bar => TryParseGpuMetric(bar.MetricId, out _, out _));
        foreach (var bar in currentGpuBars)
        {
            if (seen.Add(bar.MetricId))
            {
                resolved.Add(bar);
            }
        }

        return resolved;
    }

    private static IReadOnlyList<ResourceTableColumnSettings> ResolveTableColumns(
        IReadOnlyList<ResourceTableColumnSettings> columns,
        HardwareMetricSnapshot snapshot,
        IReadOnlyDictionary<string, int> gpuIndexesByIdentity,
        IReadOnlyDictionary<int, string> gpuIdentitiesByIndex,
        IReadOnlySet<string> availableGpuMetricIds,
        bool includeProcessDetails)
    {
        var resolved = new List<ResourceTableColumnSettings>(columns.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var column in columns)
        {
            var metric = ResolveMonitoringMetric(
                column.Id,
                column.Binding,
                gpuIndexesByIdentity,
                gpuIdentitiesByIndex,
                availableGpuMetricIds);
            if (string.IsNullOrWhiteSpace(metric.MetricId)
                || !seen.Add(metric.MetricId))
            {
                continue;
            }

            resolved.Add(column with
            {
                Id = metric.MetricId,
                Binding = metric.Binding
            });
        }

        var defaults = includeProcessDetails
            ? DashboardSettingsDefaults.CreateResourceTableProcessColumns(
                snapshot.Items,
                gpuIdentitiesByIndex)
            : DashboardSettingsDefaults.CreateResourceTableColumns(
                snapshot.Items,
                gpuIdentitiesByIndex);
        var missingGpuColumns = defaults
            .Where(static column => ResourceTableColumnCatalog.IsGpuColumnId(column.Id))
            .Where(column => seen.Add(column.Id))
            .ToArray();
        var insertionIndex = resolved.FindIndex(static column =>
            column.Id.Equals(ResourceTableColumnIds.Disk, StringComparison.OrdinalIgnoreCase));
        resolved.InsertRange(
            insertionIndex >= 0 ? insertionIndex : resolved.Count,
            missingGpuColumns);
        return resolved;
    }

    private static DashboardCardSettings ResolveCard(
        DashboardCardSettings card,
        IReadOnlyDictionary<string, int> gpuIndexesByIdentity,
        IReadOnlyDictionary<int, string> gpuIdentitiesByIndex)
    {
        var main = ResolveSlot(
            card.Main,
            card.MainBinding,
            gpuIndexesByIdentity,
            gpuIdentitiesByIndex);
        var sourceBindings = card.SmallBindings ?? [];
        var small = new string[card.Small.Count];
        var smallBindings = new DashboardMetricBinding?[card.Small.Count];
        for (var index = 0; index < card.Small.Count; index++)
        {
            var resolved = ResolveSlot(
                card.Small[index],
                index < sourceBindings.Count ? sourceBindings[index] : null,
                gpuIndexesByIdentity,
                gpuIdentitiesByIndex);
            small[index] = resolved.MetricId!;
            smallBindings[index] = resolved.Binding;
        }

        return card with
        {
            Main = main.MetricId,
            MainBinding = main.Binding,
            Small = small,
            SmallBindings = smallBindings
        };
    }

    private static ResolvedSlot ResolveSlot(
        string? metricId,
        DashboardMetricBinding? binding,
        IReadOnlyDictionary<string, int> gpuIndexesByIdentity,
        IReadOnlyDictionary<int, string> gpuIdentitiesByIndex)
    {
        if (!TryParseGpuMetric(metricId, out var currentIndex, out var suffix))
        {
            return new ResolvedSlot(metricId, null);
        }

        if (binding is not null
            && binding.ScopeKind.Equals("gpu", StringComparison.OrdinalIgnoreCase))
        {
            return gpuIndexesByIdentity.TryGetValue(
                binding.ScopeKey,
                out var reboundIndex)
                    ? new ResolvedSlot(
                        $"gpu.{reboundIndex}.{suffix}",
                        new DashboardMetricBinding("gpu", binding.ScopeKey))
                    : new ResolvedSlot(
                        metricId,
                        new DashboardMetricBinding("gpu", binding.ScopeKey));
        }

        return gpuIdentitiesByIndex.TryGetValue(currentIndex, out var inferredIdentity)
            ? new ResolvedSlot(
                metricId,
                new DashboardMetricBinding("gpu", inferredIdentity))
            : new ResolvedSlot(metricId, binding);
    }

    private static ResolvedMonitoringMetric ResolveMonitoringMetric(
        string metricId,
        DashboardMetricBinding? binding,
        IReadOnlyDictionary<string, int> gpuIndexesByIdentity,
        IReadOnlyDictionary<int, string> gpuIdentitiesByIndex,
        IReadOnlySet<string> availableGpuMetricIds)
    {
        if (!TryParseGpuMetric(metricId, out var currentIndex, out var suffix))
        {
            return new ResolvedMonitoringMetric(metricId, null);
        }

        if (binding is not null
            && binding.ScopeKind.Equals("gpu", StringComparison.OrdinalIgnoreCase))
        {
            if (!gpuIndexesByIdentity.TryGetValue(binding.ScopeKey, out var reboundIndex))
            {
                return new ResolvedMonitoringMetric(
                    metricId,
                    new DashboardMetricBinding("gpu", binding.ScopeKey));
            }

            var reboundMetricId = $"gpu.{reboundIndex}.{suffix}";
            return new ResolvedMonitoringMetric(
                reboundMetricId,
                new DashboardMetricBinding("gpu", binding.ScopeKey));
        }

        if (availableGpuMetricIds.Contains(metricId))
        {
            return new ResolvedMonitoringMetric(
                metricId,
                gpuIdentitiesByIndex.TryGetValue(currentIndex, out var currentIdentity)
                    ? new DashboardMetricBinding("gpu", currentIdentity)
                    : null);
        }

        var candidates = availableGpuMetricIds
            .Where(candidate => TryParseGpuMetric(candidate, out _, out var candidateSuffix)
                && candidateSuffix.Equals(suffix, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (candidates.Length != 1
            || !TryParseGpuMetric(candidates[0], out var candidateIndex, out _))
        {
            return new ResolvedMonitoringMetric(metricId, null);
        }

        return new ResolvedMonitoringMetric(
            candidates[0],
            gpuIdentitiesByIndex.TryGetValue(candidateIndex, out var inferredIdentity)
                ? new DashboardMetricBinding("gpu", inferredIdentity)
                : null);
    }

    private static bool TryParseGpuMetric(
        string? metricId,
        out int index,
        out string suffix)
    {
        index = -1;
        suffix = string.Empty;
        if (string.IsNullOrWhiteSpace(metricId)
            || !metricId.StartsWith("gpu.", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var indexStart = "gpu.".Length;
        var suffixStart = metricId.IndexOf('.', indexStart);
        if (suffixStart <= indexStart
            || suffixStart == metricId.Length - 1
            || !int.TryParse(metricId[indexStart..suffixStart], out index)
            || index < 0)
        {
            return false;
        }

        suffix = metricId[(suffixStart + 1)..];
        return true;
    }

    private readonly record struct ResolvedSlot(
        string? MetricId,
        DashboardMetricBinding? Binding);

    private readonly record struct ResolvedMonitoringMetric(
        string MetricId,
        DashboardMetricBinding? Binding);
}
