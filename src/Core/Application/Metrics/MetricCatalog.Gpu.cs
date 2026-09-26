using ResourceManager.App.Domain.Metrics;

namespace ResourceManager.App.Application.Metrics;

public static partial class MetricCatalog
{
    private static IReadOnlyList<int> GetGpuIndexes(IReadOnlyDictionary<string, MetricValue> items)
    {
        return items.Keys
            .Select(ParseGpuIndex)
            .Where(static index => index is not null)
            .Select(static index => index!.Value)
            .Distinct()
            .Order()
            .ToArray();
    }

    private static int? ParseGpuIndex(string metricId)
    {
        const string prefix = "gpu.";
        if (!metricId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var start = prefix.Length;
        var end = metricId.IndexOf('.', start);
        return end > start && int.TryParse(metricId[start..end], out var index) ? index : null;
    }

    /// <summary>
    /// 一块显卡的全部监控项。
    ///
    /// <paramref name="group"/> 是**分组键**（稳定、给程序看），
    /// 标签里出现的是 <c>GPU{index}</c> 这个**显示名**（给人看）。
    /// 先前这两件事共用一个字符串，于是分组键一改，每一条标签跟着变成
    /// "gpu.1 风扇转速"。键和字样是两回事，分开写。
    /// </summary>
    private static IReadOnlyList<MetricDefinition> CreateGpuDefinitions(
        int index,
        string group,
        string detail,
        IReadOnlyDictionary<string, MetricValue>? items = null,
        string? identityKey = null,
        bool usageCapabilityKnown = false)
    {
        var prefix = $"gpu.{index}";
        var displayName = $"GPU{index}";
        var definitions = new List<MetricDefinition>();
        var gpuProviderComponentId = ResolveGpuProviderComponentId(detail);
        var gpuProviderComponentName = ComponentName(gpuProviderComponentId);
        var gpuFanRpmComponentId = ResolveGpuSensorComponentId(MetricDetail(items, $"{prefix}.fanRpm") ?? detail, "fanRpm", gpuProviderComponentId);
        var gpuFanPercentComponentId = ResolveGpuSensorComponentId(MetricDetail(items, $"{prefix}.fanPercent") ?? detail, "fanPercent", gpuProviderComponentId);
        var gpuElectricalComponentId = ResolveGpuSensorComponentId(detail, "electrical", gpuProviderComponentId);
        if (usageCapabilityKnown)
        {
            AddTopologyBackedGpuUsageMetric(
                definitions,
                items,
                $"{prefix}.usage",
                $"{displayName} 占用率",
                group,
                detail,
                identityKey);
        }
        else
        {
            AddGpuMetric(definitions, items, $"{prefix}.usage", $"{displayName} 占用率", group, "%", "main", detail, null, null, identityKey);
        }
        AddGpuMetric(definitions, items, $"{prefix}.graphicsClock", $"{displayName} 频率", group, "MHz", "small", detail, gpuProviderComponentId, gpuProviderComponentName, identityKey);
        AddGpuMetric(definitions, items, $"{prefix}.graphicsClockPercent", $"{displayName} 频率百分比", group, "%", "small", detail, gpuProviderComponentId, gpuProviderComponentName, identityKey);
        AddGpuMetric(definitions, items, $"{prefix}.power", $"{displayName} 功耗", group, "W", "small", detail, gpuProviderComponentId, gpuProviderComponentName, identityKey);
        AddGpuMetric(definitions, items, $"{prefix}.boardPower", $"{displayName} 总板功耗", group, "W", "small", detail, gpuProviderComponentId, gpuProviderComponentName, identityKey);
        AddGpuMetric(definitions, items, $"{prefix}.powerLimit", $"{displayName} 功耗限制", group, "W", "small", detail, gpuProviderComponentId, gpuProviderComponentName, identityKey);
        AddGpuMetric(definitions, items, $"{prefix}.temperature", $"{displayName} 温度", group, "°C", "small", detail, gpuProviderComponentId, gpuProviderComponentName, identityKey);
        AddGpuMetric(definitions, items, $"{prefix}.hotspotTemperature", $"{displayName} 热点温度", group, "°C", "small", detail, gpuProviderComponentId, gpuProviderComponentName, identityKey);
        AddGpuMetric(definitions, items, $"{prefix}.intakeTemperature", $"{displayName} 进风温度", group, "°C", "small", detail, gpuProviderComponentId, gpuProviderComponentName, identityKey);
        // 风扇归风扇组，不归这块卡 —— 笔记本往往整机共用一套散热，
        // 控制面那边也是把风扇当独立实例的，两边口径一致。
        AddGpuMetric(definitions, items, $"{prefix}.fanRpm", $"{displayName} 风扇转速", MetricGroups.Fan, "RPM", "small", detail, gpuFanRpmComponentId, ComponentName(gpuFanRpmComponentId), identityKey);
        AddGpuMetric(definitions, items, $"{prefix}.fanPercent", $"{displayName} 风扇百分比", MetricGroups.Fan, "%", "small", detail, gpuFanPercentComponentId, ComponentName(gpuFanPercentComponentId), identityKey);
        AddGpuMetric(definitions, items, $"{prefix}.coreVoltage", $"{displayName} 电压", group, "V", "small", detail, gpuElectricalComponentId, ComponentName(gpuElectricalComponentId), identityKey);
        AddGpuMetric(definitions, items, $"{prefix}.current", $"{displayName} 电流", group, "A", "small", detail, gpuElectricalComponentId, ComponentName(gpuElectricalComponentId), identityKey);
        AddGpuMetric(definitions, items, $"{prefix}.vram", $"{displayName} 显存占用", group, MetricUnits.Bytes, "main", detail, gpuProviderComponentId, gpuProviderComponentName, identityKey);
        AddGpuMetric(definitions, items, $"{prefix}.vramPercent", $"{displayName} 显存占用率", group, "%", "small", detail, gpuProviderComponentId, gpuProviderComponentName, identityKey);
        AddGpuMetric(definitions, items, $"{prefix}.memoryClock", $"{displayName} 显存频率", group, "MHz", "small", detail, gpuProviderComponentId, gpuProviderComponentName, identityKey);
        return definitions;
    }

    private static void AddTopologyBackedGpuUsageMetric(
        List<MetricDefinition> definitions,
        IReadOnlyDictionary<string, MetricValue>? items,
        string id,
        string label,
        string group,
        string detail,
        string? identityKey)
    {
        MetricValue? item = null;
        if (items is not null)
        {
            items.TryGetValue(id, out item);
        }
        definitions.Add(new MetricDefinition(
            id,
            label,
            group,
            string.IsNullOrWhiteSpace(item?.Unit) ? "%" : item.Unit,
            "main",
            item?.Detail ?? detail,
            Selectable: true,
            ScopeKind: string.IsNullOrWhiteSpace(identityKey) ? null : "gpu",
            ScopeKey: identityKey));
    }

    private static void AddGpuMetric(
        List<MetricDefinition> definitions,
        IReadOnlyDictionary<string, MetricValue>? items,
        string id,
        string label,
        string group,
        string unit,
        string preferredSlot,
        string detail,
        string? requiredComponentId,
        string? requiredComponentName,
        string? identityKey)
    {
        AddMetric(
            definitions,
            items,
            id,
            label,
            group,
            unit,
            preferredSlot,
            detail,
            requiredComponentId,
            requiredComponentName,
            string.IsNullOrWhiteSpace(identityKey) ? null : "gpu",
            identityKey);
    }

    private static string? MetricDetail(IReadOnlyDictionary<string, MetricValue>? items, string id)
    {
        return items is not null && items.TryGetValue(id, out var value)
            ? value.Detail
            : null;
    }

    private static string? ResolveGpuProviderComponentId(string detail)
    {
        if (detail.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase))
        {
            return "nvidia-nvml-provider";
        }

        if (detail.Contains("AMD", StringComparison.OrdinalIgnoreCase)
            || detail.Contains("Radeon", StringComparison.OrdinalIgnoreCase))
        {
            return "amd-adlx-provider";
        }

        return null;
    }

    private static string? ResolveGpuSensorComponentId(string detail, string sensorKind, string? fallbackComponentId)
    {
        if (sensorKind.Equals("fanRpm", StringComparison.OrdinalIgnoreCase))
        {
            if (detail.Contains("Mechrevo", StringComparison.OrdinalIgnoreCase)
                || detail.Contains("UWACPI", StringComparison.OrdinalIgnoreCase)
                || detail.Contains("Notebook OEM", StringComparison.OrdinalIgnoreCase))
            {
                return "notebook-oem-fan-provider";
            }

            if (detail.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase))
            {
                return "nvidia-nvapi-provider";
            }

            return "librehardwaremonitor-provider";
        }

        if (sensorKind.Equals("fanPercent", StringComparison.OrdinalIgnoreCase)
            && (detail.Contains("Mechrevo", StringComparison.OrdinalIgnoreCase)
                || detail.Contains("UWACPI", StringComparison.OrdinalIgnoreCase)
                || detail.Contains("Notebook OEM", StringComparison.OrdinalIgnoreCase)))
        {
            return "notebook-oem-fan-provider";
        }

        if (detail.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)
            && (sensorKind.Equals("fanPercent", StringComparison.OrdinalIgnoreCase)
                || sensorKind.Equals("electrical", StringComparison.OrdinalIgnoreCase)))
        {
            return "nvidia-nvapi-provider";
        }

        return fallbackComponentId;
    }
}
