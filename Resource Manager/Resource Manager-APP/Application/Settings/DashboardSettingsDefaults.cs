using ResourceManager.App.Application.Metrics;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.ResourceBreakdown;
using ResourceManager.App.Domain.ResourceTable;
using ResourceManager.App.Domain.Settings;

namespace ResourceManager.App.Application.Settings;

public static class DashboardSettingsDefaults
{
    public const int MinimumSupportedVersion = 14;
    public const int CurrentVersion = 15;

    public static DashboardSettings Create(HardwareMetricSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var selectableDefinitions = MetricCatalog.FromSnapshot(snapshot)
            .Where(static definition => definition.Selectable)
            .ToArray();
        var selectableItems = new Dictionary<string, MetricValue>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var definition in selectableDefinitions)
        {
            selectableItems[definition.Id] = snapshot.Items.TryGetValue(
                    definition.Id,
                    out var item)
                ? item
                : new MetricValue(
                    definition.Id,
                    definition.Label,
                    definition.Group,
                    "-",
                    null,
                    definition.Unit,
                    null,
                    definition.Detail);
        }
        return Create(
            selectableItems,
            snapshot.Gpus
                .Where(static gpu => !string.IsNullOrWhiteSpace(gpu.IdentityKey))
                .ToDictionary(
                    static gpu => gpu.Index,
                    static gpu => gpu.IdentityKey!));
    }

    public static DashboardSettings Create(
        IReadOnlyDictionary<string, MetricValue>? items = null,
        IReadOnlyDictionary<int, string>? gpuIdentityKeys = null)
    {
        var cards = new List<DashboardCardSettings>
        {
            CreateCpuCard(items),
            CreateMemoryCard(items)
        };

        var gpuIndexes = GetGpuIndexes(items);
        foreach (var index in gpuIndexes)
        {
            AddGpuUsageCard(cards, index, items, gpuIdentityKeys);
        }

        foreach (var index in gpuIndexes)
        {
            AddGpuVramCard(cards, index, items, gpuIdentityKeys);
        }

        AddCpuSensorCard(cards, items);
        foreach (var index in gpuIndexes)
        {
            AddGpuSensorCard(cards, index, items, gpuIdentityKeys);
        }

        return new DashboardSettings(
            CurrentVersion,
            cards,
            CreateResourceBars(items, gpuIdentityKeys),
            CreateResourceTableColumns(items, gpuIdentityKeys),
            CreateResourceTableProcessColumns(items, gpuIdentityKeys));
    }

    internal static int? ParseGpuIndex(string? metricId)
    {
        const string prefix = "gpu.";
        if (metricId?.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) != true)
        {
            return null;
        }

        var start = prefix.Length;
        var end = metricId.IndexOf('.', start);
        return end > start && int.TryParse(metricId[start..end], out var index) ? index : null;
    }

    internal static int[] GetGpuIndexes(IReadOnlyDictionary<string, MetricValue>? items)
    {
        var indexes = items?.Keys
            .Select(ParseGpuIndex)
            .Where(static index => index is not null)
            .Select(static index => index!.Value)
            .Distinct()
            .Order()
            .ToArray() ?? [];

        return indexes;
    }

    internal static void AddGpuUsageCard(
        List<DashboardCardSettings> cards,
        int index,
        IReadOnlyDictionary<string, MetricValue>? items,
        IReadOnlyDictionary<int, string>? gpuIdentityKeys = null)
    {
        var prefix = $"gpu.{index}";
        if (HasMetric(items, $"{prefix}.usage"))
        {
            var small = CreateGpuUsageSmallMetrics(index, items);
            var binding = CreateGpuBinding(index, gpuIdentityKeys);
            cards.Add(new DashboardCardSettings(
                $"gpu{index}",
                $"{prefix}.usage",
                small,
                binding,
                RepeatBinding(binding, small.Length)));
        }
    }

    internal static void AddGpuVramCard(
        List<DashboardCardSettings> cards,
        int index,
        IReadOnlyDictionary<string, MetricValue>? items,
        IReadOnlyDictionary<int, string>? gpuIdentityKeys = null)
    {
        var prefix = $"gpu.{index}";
        if (HasMetric(items, $"{prefix}.vram"))
        {
            var small = GetExistingMetrics(
                items,
                $"{prefix}.vramPercent",
                $"{prefix}.memoryClock",
                $"{prefix}.graphicsClockPercent");
            var binding = CreateGpuBinding(index, gpuIdentityKeys);
            cards.Add(new DashboardCardSettings(
                $"vram{index}",
                $"{prefix}.vram",
                small,
                binding,
                RepeatBinding(binding, small.Length)));
        }
    }

    internal static DashboardCardSettings CreateCpuCard(IReadOnlyDictionary<string, MetricValue>? items)
    {
        return new DashboardCardSettings(
            "cpu",
            "cpu.usage",
            GetExistingMetrics(items, "cpu.frequency", "cpu.temperature", "cpu.actualPower"));
    }

    internal static DashboardCardSettings CreateMemoryCard(IReadOnlyDictionary<string, MetricValue>? items)
    {
        return new DashboardCardSettings(
            "memory",
            "memory.usage",
            GetExistingMetrics(items, "memory.percent", "memory.temperature"));
    }

    internal static string[] CreateGpuUsageSmallMetrics(
        int index,
        IReadOnlyDictionary<string, MetricValue>? items)
    {
        var prefix = $"gpu.{index}";
        return HasMetric(items, $"{prefix}.vram")
            ? GetExistingMetrics(items, $"{prefix}.graphicsClock", $"{prefix}.power", $"{prefix}.temperature")
            : GetExistingMetrics(items, $"{prefix}.graphicsClock", $"{prefix}.temperature");
    }

    internal static void AddCpuSensorCard(
        List<DashboardCardSettings> cards,
        IReadOnlyDictionary<string, MetricValue>? items)
    {
        if (!HasMetric(items, "cpu.fanRpm"))
        {
            return;
        }

        cards.Add(new DashboardCardSettings(
            "cpu-sensors",
            "cpu.fanRpm",
            GetExistingMetrics(items, "cpu.coreVoltage", "cpu.packageCurrent")));
    }

    internal static void AddGpuSensorCard(
        List<DashboardCardSettings> cards,
        int index,
        IReadOnlyDictionary<string, MetricValue>? items,
        IReadOnlyDictionary<int, string>? gpuIdentityKeys = null)
    {
        var prefix = $"gpu.{index}";
        if (!HasMetric(items, $"{prefix}.vram") || !HasMetric(items, $"{prefix}.fanRpm"))
        {
            return;
        }

        var small = GetExistingMetrics(items, $"{prefix}.coreVoltage", $"{prefix}.current");
        var binding = CreateGpuBinding(index, gpuIdentityKeys);
        cards.Add(new DashboardCardSettings(
            $"gpu{index}-sensors",
            $"{prefix}.fanRpm",
            small,
            binding,
            RepeatBinding(binding, small.Length)));
    }

    internal static IReadOnlyList<ResourceBarSettings> CreateResourceBars(
        IReadOnlyDictionary<string, MetricValue>? items = null,
        IReadOnlyDictionary<int, string>? gpuIdentityKeys = null)
    {
        var bars = new List<ResourceBarSettings>
        {
            new("resource-cpu", "cpu.usage", ResourceBreakdownScaleModes.Capacity),
            new("resource-memory", "memory.usage", ResourceBreakdownScaleModes.Capacity)
        };
        if (HasMetric(items, "virtualMemory.usage"))
        {
            bars.Add(new ResourceBarSettings(
                "resource-virtual-memory",
                "virtualMemory.usage",
                ResourceBreakdownScaleModes.Capacity));
        }

        foreach (var index in GetGpuIndexes(items))
        {
            var usageId = $"gpu.{index}.usage";
            if (HasMetric(items, usageId))
            {
                bars.Add(new ResourceBarSettings(
                    $"resource-gpu{index}",
                    usageId,
                    ResourceBreakdownScaleModes.Capacity,
                    CreateGpuBinding(index, gpuIdentityKeys)));
            }

            var vramId = $"gpu.{index}.vram";
            if (HasMetric(items, vramId))
            {
                bars.Add(new ResourceBarSettings(
                    $"resource-vram{index}",
                    vramId,
                    ResourceBreakdownScaleModes.Capacity,
                    CreateGpuBinding(index, gpuIdentityKeys)));
            }
        }

        return bars;
    }

    internal static IReadOnlyList<ResourceTableColumnSettings> CreateResourceTableColumns(
        IReadOnlyDictionary<string, MetricValue>? items = null,
        IReadOnlyDictionary<int, string>? gpuIdentityKeys = null)
    {
        return CreateResourceTableColumns(
            items,
            gpuIdentityKeys,
            includeProcessDetails: false);
    }

    internal static IReadOnlyList<ResourceTableColumnSettings> CreateResourceTableProcessColumns(
        IReadOnlyDictionary<string, MetricValue>? items = null,
        IReadOnlyDictionary<int, string>? gpuIdentityKeys = null)
    {
        return CreateResourceTableColumns(
            items,
            gpuIdentityKeys,
            includeProcessDetails: true);
    }

    private static IReadOnlyList<ResourceTableColumnSettings> CreateResourceTableColumns(
        IReadOnlyDictionary<string, MetricValue>? items,
        IReadOnlyDictionary<int, string>? gpuIdentityKeys,
        bool includeProcessDetails)
    {
        var columns = ResourceTableColumnCatalog.KnownColumns()
            .Where(column => includeProcessDetails || !IsProcessDetailColumn(column.Id))
            .Select(static column => new ResourceTableColumnSettings(
                column.Id,
                column.Visible,
                column.Width))
            .ToList();
        var gpuColumns = (items?.Keys ?? [])
            .Where(ResourceTableColumnCatalog.IsGpuColumnId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static id => ParseGpuIndex(id))
            .ThenBy(static id => id.EndsWith(".usage", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .Select(id =>
            {
                var column = ResourceTableColumnCatalog.CreateGpuColumn(id);
                return new ResourceTableColumnSettings(
                    column.Id,
                    column.Visible,
                    column.Width,
                    CreateGpuBinding(ParseGpuIndex(id) ?? -1, gpuIdentityKeys));
            })
            .ToArray();
        var insertionIndex = columns.FindIndex(static column =>
            column.Id.Equals(ResourceTableColumnIds.Disk, StringComparison.OrdinalIgnoreCase));
        columns.InsertRange(insertionIndex >= 0 ? insertionIndex : columns.Count, gpuColumns);
        return columns;
    }

    internal static bool IsProcessDetailColumn(string columnId)
    {
        return columnId.Equals(ResourceTableColumnIds.ProcessId, StringComparison.OrdinalIgnoreCase)
            || columnId.Equals(ResourceTableColumnIds.User, StringComparison.OrdinalIgnoreCase)
            || columnId.Equals(ResourceTableColumnIds.Architecture, StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsResourceBarMetricSupported(
        string? metricId,
        IReadOnlyDictionary<string, MetricValue>? availableMetrics = null)
    {
        if (string.IsNullOrWhiteSpace(metricId))
        {
            return false;
        }

        var normalized = metricId.Trim();
        var structurallySupported = normalized.Equals("cpu.usage", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("memory.usage", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("virtualMemory.usage", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals(ResourceBreakdownMetricIds.DiskIo, StringComparison.OrdinalIgnoreCase)
            || normalized.Equals(ResourceBreakdownMetricIds.DiskRead, StringComparison.OrdinalIgnoreCase)
            || normalized.Equals(ResourceBreakdownMetricIds.DiskWrite, StringComparison.OrdinalIgnoreCase)
            || normalized.Equals(ResourceBreakdownMetricIds.NetworkTraffic, StringComparison.OrdinalIgnoreCase)
            || normalized.Equals(ResourceBreakdownMetricIds.NetworkReceive, StringComparison.OrdinalIgnoreCase)
            || normalized.Equals(ResourceBreakdownMetricIds.NetworkSend, StringComparison.OrdinalIgnoreCase)
            || normalized.Equals(ResourceBreakdownMetricIds.NetworkRawTraffic, StringComparison.OrdinalIgnoreCase)
            || normalized.Equals(ResourceBreakdownMetricIds.NetworkRawReceive, StringComparison.OrdinalIgnoreCase)
            || normalized.Equals(ResourceBreakdownMetricIds.NetworkRawSend, StringComparison.OrdinalIgnoreCase)
            || (ParseGpuIndex(normalized) is not null
                && (normalized.EndsWith(".usage", StringComparison.OrdinalIgnoreCase)
                    || normalized.EndsWith(".vram", StringComparison.OrdinalIgnoreCase)));
        if (!structurallySupported)
        {
            return false;
        }

        return availableMetrics is null || availableMetrics.ContainsKey(normalized);
    }

    private static bool HasMetric(IReadOnlyDictionary<string, MetricValue>? items, string metricId)
    {
        return items is null || items.ContainsKey(metricId);
    }

    private static string[] GetExistingMetrics(IReadOnlyDictionary<string, MetricValue>? items, params string[] metricIds)
    {
        return metricIds
            .Where(metricId => HasMetric(items, metricId))
            .Take(3)
            .ToArray();
    }

    private static DashboardMetricBinding? CreateGpuBinding(
        int index,
        IReadOnlyDictionary<int, string>? gpuIdentityKeys)
    {
        return gpuIdentityKeys is not null
            && gpuIdentityKeys.TryGetValue(index, out var identityKey)
            && !string.IsNullOrWhiteSpace(identityKey)
                ? new DashboardMetricBinding("gpu", identityKey)
                : null;
    }

    private static DashboardMetricBinding?[] RepeatBinding(
        DashboardMetricBinding? binding,
        int count)
    {
        return Enumerable.Repeat(binding, count).ToArray();
    }
}
