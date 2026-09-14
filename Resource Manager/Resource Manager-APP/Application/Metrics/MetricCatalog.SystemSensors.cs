using ResourceManager.App.Domain.Metrics;

namespace ResourceManager.App.Application.Metrics;

public static partial class MetricCatalog
{
    private static void AddSystemIoDefinitions(
        List<MetricDefinition> definitions,
        IReadOnlyDictionary<string, MetricValue> items)
    {
        definitions.AddRange([
            new("disk.total.activePercent", "磁盘活动时间", "Disk", "%", "main", items.GetValueOrDefault("disk.total.activePercent")?.Detail ?? "PhysicalDisk(_Total)"),
            new("disk.total.readBytesPerSec", "磁盘读取", "Disk", "B/s", "small", items.GetValueOrDefault("disk.total.readBytesPerSec")?.Detail ?? "PhysicalDisk(_Total)"),
            new("disk.total.writeBytesPerSec", "磁盘写入", "Disk", "B/s", "small", items.GetValueOrDefault("disk.total.writeBytesPerSec")?.Detail ?? "PhysicalDisk(_Total)"),
            new("disk.total.queueLength", "磁盘队列", "Disk", "", "small", items.GetValueOrDefault("disk.total.queueLength")?.Detail ?? "PhysicalDisk(_Total)"),
            new("network.total.receiveBytesPerSec", "网络接收", "Network", "B/s", "small", items.GetValueOrDefault("network.total.receiveBytesPerSec")?.Detail ?? "Network Interface(*)"),
            new("network.total.sendBytesPerSec", "网络发送", "Network", "B/s", "small", items.GetValueOrDefault("network.total.sendBytesPerSec")?.Detail ?? "Network Interface(*)"),
            new("network.total.utilizationPercent", "网络利用率", "Network", "%", "main", items.GetValueOrDefault("network.total.utilizationPercent")?.Detail ?? "Network Interface(*)")
        ]);
    }

    private static void AddMemoryAndSystemSensorDefinitions(
        List<MetricDefinition> definitions,
        IReadOnlyDictionary<string, MetricValue> items)
    {
        AddSnapshotMetric(
            definitions,
            items,
            "memory.temperature",
            "内存温度",
            "Memory",
            "°C",
            "small",
            requiredComponentId: RequiredComponentWhenUnavailable(items, "memory.temperature", "librehardwaremonitor-provider"));
        AddMotherboardSensorDefinitions(definitions, items);
        AddDiskTemperatureDefinitions(definitions, items);
        AddSystemFanDefinitions(definitions, items);
    }

    private static void AddMotherboardSensorDefinitions(
        List<MetricDefinition> definitions,
        IReadOnlyDictionary<string, MetricValue> items)
    {
        AddSnapshotMetric(
            definitions,
            items,
            "system.motherboardTemperature",
            "主板温度",
            "Motherboard",
            "°C",
            "small",
            requiredComponentId: RequiredComponentWhenUnavailable(items, "system.motherboardTemperature", "librehardwaremonitor-provider"));
        AddSnapshotMetric(
            definitions,
            items,
            "system.vrmTemperature",
            "供电温度",
            "Motherboard",
            "°C",
            "small",
            requiredComponentId: RequiredComponentWhenUnavailable(items, "system.vrmTemperature", "librehardwaremonitor-provider"));
        AddSnapshotMetric(
            definitions,
            items,
            "system.chipsetTemperature",
            "芯片组温度",
            "Motherboard",
            "°C",
            "small",
            requiredComponentId: RequiredComponentWhenUnavailable(items, "system.chipsetTemperature", "librehardwaremonitor-provider"));
        AddSnapshotMetric(
            definitions,
            items,
            "system.motherboardVoltage",
            "主板电压",
            "Motherboard",
            "V",
            "small",
            requiredComponentId: RequiredComponentWhenUnavailable(items, "system.motherboardVoltage", "librehardwaremonitor-provider"));
    }

    private static void AddDiskTemperatureDefinitions(
        List<MetricDefinition> definitions,
        IReadOnlyDictionary<string, MetricValue> items)
    {
        foreach (var item in items
            .Where(static item => TryParseIndexedMetric(item.Key, "disk.", ".temperature") is not null)
            .OrderBy(static item => TryParseIndexedMetric(item.Key, "disk.", ".temperature")))
        {
            var index = TryParseIndexedMetric(item.Key, "disk.", ".temperature")!.Value;
            var selectable = IsMetricSelectable(item.Value);
            var requiredComponentId = selectable ? null : "librehardwaremonitor-provider";
            definitions.Add(new MetricDefinition(
                item.Key,
                $"磁盘{index} 温度",
                "Disk",
                "°C",
                "small",
                item.Value.Detail,
                requiredComponentId,
                ComponentName(requiredComponentId),
                selectable,
                selectable ? null : DisabledReason(item.Value, ComponentName(requiredComponentId))));
        }
    }

    private static void AddSystemFanDefinitions(
        List<MetricDefinition> definitions,
        IReadOnlyDictionary<string, MetricValue> items)
    {
        AddSystemFanDefinitions(definitions, items, ".speed", "RPM", static index => $"风扇{index}");
        AddSystemFanDefinitions(definitions, items, ".speedPercent", "%", static index => $"风扇{index} 百分比");
    }

    private static void AddSystemFanDefinitions(
        List<MetricDefinition> definitions,
        IReadOnlyDictionary<string, MetricValue> items,
        string suffix,
        string unit,
        Func<int, string> labelFactory)
    {
        foreach (var item in items
            .Where(item => TryParseIndexedMetric(item.Key, "fan.", suffix) is not null)
            .OrderBy(item => TryParseIndexedMetric(item.Key, "fan.", suffix)))
        {
            var index = TryParseIndexedMetric(item.Key, "fan.", suffix)!.Value;
            var selectable = IsMetricSelectable(item.Value);
            var requiredComponentId = selectable ? null : "librehardwaremonitor-provider";
            definitions.Add(new MetricDefinition(
                item.Key,
                labelFactory(index),
                "Fan",
                unit,
                "small",
                item.Value.Detail,
                requiredComponentId,
                ComponentName(requiredComponentId),
                selectable,
                selectable ? null : DisabledReason(item.Value, ComponentName(requiredComponentId))));
        }
    }

    private static int? TryParseIndexedMetric(string metricId, string prefix, string suffix)
    {
        if (!metricId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            || !metricId.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var start = prefix.Length;
        var length = metricId.Length - prefix.Length - suffix.Length;
        return length > 0 && int.TryParse(metricId.Substring(start, length), out var index) ? index : null;
    }
}
