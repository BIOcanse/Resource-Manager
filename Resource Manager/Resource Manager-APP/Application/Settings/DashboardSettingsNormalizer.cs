using ResourceManager.App.Domain.Settings;
using ResourceManager.App.Domain.ResourceBreakdown;
using ResourceManager.App.Domain.ResourceTable;

namespace ResourceManager.App.Application.Settings;

public static class DashboardSettingsNormalizer
{
    public static DashboardSettings Normalize(DashboardSettings? settings, bool preserveVersion = false)
    {
        if (settings is null)
        {
            return DashboardSettingsDefaults.Create();
        }

        var cards = settings.Cards?
            .Select(NormalizeCard)
            .Where(static card => card is not null)
            .Select(static card => card!)
            .ToArray() ?? [];
        var resourceBars = settings.ResourceBars?
            .Select(NormalizeResourceBar)
            .Where(static bar => bar is not null)
            .Select(static bar => bar!)
            .GroupBy(static bar => bar.MetricId, StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.First())
            .ToArray() ?? [];
        var resourceTableColumns = NormalizeResourceTableColumns(
            settings.ResourceTableColumns,
            DashboardSettingsDefaults.CreateResourceTableColumns(),
            IsSoftwareResourceTableColumn);
        var resourceTableProcessColumns = NormalizeResourceTableColumns(
            settings.ResourceTableProcessColumns,
            DashboardSettingsDefaults.CreateResourceTableProcessColumns(),
            ResourceTableColumnCatalog.IsKnownColumnId);
        var defaults = DashboardSettingsDefaults.Create();

        return new DashboardSettings(
            preserveVersion ? settings.Version : DashboardSettingsDefaults.CurrentVersion,
            cards.Length == 0 ? defaults.Cards : cards,
            resourceBars.Length == 0 ? defaults.ResourceBars : resourceBars,
            resourceTableColumns.Length == 0 ? defaults.ResourceTableColumns : resourceTableColumns,
            resourceTableProcessColumns.Length == 0 ? defaults.ResourceTableProcessColumns : resourceTableProcessColumns);
    }

    private static DashboardCardSettings? NormalizeCard(DashboardCardSettings? card)
    {
        if (card is null)
        {
            return null;
        }

        var id = string.IsNullOrWhiteSpace(card.Id)
            ? Guid.NewGuid().ToString("N")
            : card.Id.Trim();

        var main = string.IsNullOrWhiteSpace(card.Main) ? null : card.Main.Trim();
        var sourceBindings = card.SmallBindings ?? [];
        var small = new List<string>(3);
        var smallBindings = new List<DashboardMetricBinding?>(3);
        for (var index = 0;
             index < (card.Small?.Count ?? 0) && small.Count < 3;
             index++)
        {
            var metricId = card.Small![index];
            if (string.IsNullOrWhiteSpace(metricId))
            {
                continue;
            }

            var normalizedMetricId = metricId.Trim();
            small.Add(normalizedMetricId);
            smallBindings.Add(NormalizeBinding(
                normalizedMetricId,
                index < sourceBindings.Count ? sourceBindings[index] : null));
        }
        var mainBinding = NormalizeBinding(main, card.MainBinding);

        return new DashboardCardSettings(
            id,
            main,
            small.ToArray(),
            mainBinding,
            smallBindings.ToArray());
    }

    private static DashboardMetricBinding? NormalizeBinding(
        string? metricId,
        DashboardMetricBinding? binding)
    {
        if (string.IsNullOrWhiteSpace(metricId)
            || binding is null
            || !string.Equals(binding.ScopeKind, "gpu", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(binding.ScopeKey)
            || DashboardSettingsDefaults.ParseGpuIndex(metricId) is null)
        {
            return null;
        }

        return new DashboardMetricBinding("gpu", binding.ScopeKey.Trim());
    }

    private static ResourceBarSettings? NormalizeResourceBar(ResourceBarSettings? bar)
    {
        if (bar is null || string.IsNullOrWhiteSpace(bar.MetricId))
        {
            return null;
        }

        var metricId = bar.MetricId.Trim();
        if (!DashboardSettingsDefaults.IsResourceBarMetricSupported(metricId))
        {
            return null;
        }

        var id = string.IsNullOrWhiteSpace(bar.Id)
            ? $"resource-{metricId.Replace('.', '-')}"
            : bar.Id.Trim();
        var scaleMode = ResourceBreakdownScaleModes.Normalize(metricId, bar.ScaleMode);

        return new ResourceBarSettings(
            id,
            metricId,
            scaleMode,
            NormalizeBinding(metricId, bar.Binding));
    }

    private static ResourceTableColumnSettings[] NormalizeResourceTableColumns(
        IReadOnlyList<ResourceTableColumnSettings>? columns,
        IReadOnlyList<ResourceTableColumnSettings> defaults,
        Func<string, bool> isAllowed)
    {
        var normalized = columns?
            .Where(static item => item is not null && !string.IsNullOrWhiteSpace(item.Id))
            .Select(static item => NormalizeResourceTableColumn(item))
            .Where(item => isAllowed(item.Id))
            .GroupBy(static item => item.Id, StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.First())
            .ToList() ?? [];

        if (normalized.Count == 0)
        {
            return [];
        }

        var existing = normalized.Select(static item => item.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var column in defaults)
        {
            if (!existing.Contains(column.Id))
            {
                normalized.Add(column);
            }
        }

        return normalized.ToArray();
    }

    private static ResourceTableColumnSettings NormalizeResourceTableColumn(ResourceTableColumnSettings item)
    {
        var id = item.Id.Trim();
        var defaultWidth = ResourceTableColumnCatalog.IsGpuColumnId(id)
            ? ResourceTableColumnCatalog.CreateGpuColumn(id).Width
            : ResourceTableColumnCatalog.KnownColumns()
                .FirstOrDefault(column => column.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
                ?.Width ?? 100;
        var width = item.Width is > 0
            ? Math.Clamp(item.Width.Value, 56, 520)
            : defaultWidth;
        return new ResourceTableColumnSettings(
            id,
            item.Visible,
            width,
            NormalizeBinding(id, item.Binding));
    }

    private static bool IsSoftwareResourceTableColumn(string columnId)
    {
        return ResourceTableColumnCatalog.IsKnownColumnId(columnId)
            && !DashboardSettingsDefaults.IsProcessDetailColumn(columnId);
    }
}
