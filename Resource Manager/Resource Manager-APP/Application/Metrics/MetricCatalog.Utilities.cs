using ResourceManager.App.Domain.Metrics;

namespace ResourceManager.App.Application.Metrics;

public static partial class MetricCatalog
{
    private static void AddIfAvailable(
        List<MetricDefinition> definitions,
        IReadOnlyDictionary<string, MetricValue>? items,
        string id,
        string label,
        string group,
        string unit,
        string preferredSlot,
        string detail,
        string? requiredComponentId = null,
        string? requiredComponentName = null,
        string? scopeKind = null,
        string? scopeKey = null)
    {
        MetricValue? item = null;
        if (items is not null && !items.TryGetValue(id, out item))
        {
            return;
        }

        var itemDetail = item?.Detail ?? detail;
        var dependencyId = requiredComponentId;
        var selectable = items is null || IsMetricSelectable(item);
        definitions.Add(new MetricDefinition(
            id,
            label,
            group,
            string.IsNullOrWhiteSpace(item?.Unit) ? unit : item.Unit,
            preferredSlot,
            itemDetail,
            dependencyId,
            requiredComponentName ?? ComponentName(dependencyId),
            selectable,
            selectable ? null : DisabledReason(item, requiredComponentName ?? ComponentName(dependencyId)),
            scopeKind,
            scopeKey));
    }

    private static void AddSnapshotMetric(
        List<MetricDefinition> definitions,
        IReadOnlyDictionary<string, MetricValue> items,
        string id,
        string label,
        string group,
        string unit,
        string preferredSlot,
        string? detail = null,
        string? requiredComponentId = null,
        string? requiredComponentName = null)
    {
        items.TryGetValue(id, out var item);
        var selectable = IsMetricSelectable(item);
        var componentName = requiredComponentName ?? ComponentName(requiredComponentId);
        definitions.Add(new MetricDefinition(
            id,
            label,
            group,
            unit,
            preferredSlot,
            item?.Detail ?? detail,
            requiredComponentId,
            componentName,
            selectable,
            selectable ? null : DisabledReason(item, componentName)));
    }

    private static bool IsMetricSelectable(MetricValue? item)
    {
        if (item is null)
        {
            return false;
        }

        if (item.NumericValue is not null || item.Percent is not null)
        {
            return true;
        }

        return !string.IsNullOrWhiteSpace(item.DisplayValue)
            && !item.DisplayValue.Equals("-", StringComparison.Ordinal)
            && !item.DisplayValue.Equals("N/A", StringComparison.OrdinalIgnoreCase)
            && !item.DisplayValue.Equals("--", StringComparison.OrdinalIgnoreCase);
    }

    private static string DisabledReason(MetricValue? item, string? requiredComponentName)
    {
        if (item is null)
        {
            return "当前硬件或 Provider 未暴露这个读数。";
        }

        if (!string.IsNullOrWhiteSpace(requiredComponentName))
        {
            return $"当前 Provider 未返回有效读数，需要更完整的 {requiredComponentName} 或对应硬件/OEM 组件。";
        }

        return "当前硬件或 Provider 未返回有效读数。";
    }

    private static string? RequiredComponentWhenUnavailable(
        IReadOnlyDictionary<string, MetricValue> items,
        string metricId,
        string componentId)
    {
        return items.TryGetValue(metricId, out var item) && IsMetricSelectable(item)
            ? null
            : componentId;
    }

    private static string? RequiredFanComponentWhenUnavailable(
        IReadOnlyDictionary<string, MetricValue> items,
        string metricId)
    {
        if (items.TryGetValue(metricId, out var item) && IsMetricSelectable(item))
        {
            return null;
        }

        if (item?.Detail?.Contains("Mechrevo", StringComparison.OrdinalIgnoreCase) == true
            || item?.Detail?.Contains("UWACPI", StringComparison.OrdinalIgnoreCase) == true
            || item?.Detail?.Contains("Notebook OEM", StringComparison.OrdinalIgnoreCase) == true)
        {
            return "notebook-oem-fan-provider";
        }

        return "librehardwaremonitor-provider";
    }

    private static string? ComponentName(string? componentId)
    {
        return componentId switch
        {
            "amd-smu-pawnio-provider" => "AMD SMU / PawnIO Provider",
            "amd-ryzen-master-monitoring-sdk" => "AMD Ryzen Master Monitoring SDK",
            "intel-pcm-provider" => "Intel PCM / MSR Provider",
            "nvidia-nvml-provider" => "NVIDIA NVML Provider",
            "nvidia-nvapi-provider" => "NVIDIA NVAPI Provider",
            "amd-adlx-provider" => "AMD ADLX / ADL Provider",
            "librehardwaremonitor-provider" => "LibreHardwareMonitor Provider",
            "notebook-fancontrol-provider" => "Notebook FanControl Provider",
            "notebook-oem-fan-provider" => "Notebook OEM Fan Provider",
            _ => null
        };
    }
}
