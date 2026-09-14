using ResourceManager.App.Domain.Metrics;

namespace ResourceManager.App.Application.Metrics;

public static partial class MetricCatalog
{
    private static void AddCpuSensorDefinitions(
        List<MetricDefinition> definitions,
        IReadOnlyDictionary<string, MetricValue> items,
        string? cpuSensorComponentId,
        string? cpuSensorComponentName)
    {
        AddSnapshotMetric(definitions, items, "cpu.packagePower", "CPU 功耗", "CPU", "W", "small", null, cpuSensorComponentId, cpuSensorComponentName);
        AddSnapshotMetric(definitions, items, "cpu.coreVoltage", "CPU 电压", "CPU", "V", "small", null, cpuSensorComponentId, cpuSensorComponentName);
        AddSnapshotMetric(definitions, items, "cpu.packageCurrent", "CPU 电流", "CPU", "A", "small", null, cpuSensorComponentId, cpuSensorComponentName);
        AddSnapshotMetric(definitions, items, "cpu.temperature", "CPU 温度", "CPU", "°C", "small", null, cpuSensorComponentId, cpuSensorComponentName);
        AddSnapshotMetric(definitions, items, "cpu.stapmPower", "CPU STAPM 功耗", "CPU", "W", "small", null, cpuSensorComponentId, cpuSensorComponentName);
        AddSnapshotMetric(definitions, items, "cpu.actualPower", "CPU 实际功耗", "CPU", "W", "small", null, cpuSensorComponentId, cpuSensorComponentName);
        AddSnapshotMetric(definitions, items, "cpu.averagePower", "CPU 平均功耗", "CPU", "W", "small", null, cpuSensorComponentId, cpuSensorComponentName);
        AddSnapshotMetric(definitions, items, "cpu.tdcCurrent", "CPU TDC 电流", "CPU", "A", "small", null, cpuSensorComponentId, cpuSensorComponentName);
        AddSnapshotMetric(definitions, items, "cpu.edcCurrent", "CPU EDC 电流", "CPU", "A", "small", null, cpuSensorComponentId, cpuSensorComponentName);
        AddSnapshotMetric(definitions, items, "cpu.platformPower", "平台功耗", "CPU", "W", "small", null, cpuSensorComponentId, cpuSensorComponentName);
        AddSnapshotMetric(definitions, items, "cpu.platformVoltage", "平台电压", "CPU", "V", "small", null, cpuSensorComponentId, cpuSensorComponentName);
        AddSnapshotMetric(definitions, items, "cpu.igpuFrequency", "核显频率", "CPU", "MHz", "small", null, cpuSensorComponentId, cpuSensorComponentName);
        AddSnapshotMetric(definitions, items, "cpu.igpuVoltage", "核显电压", "CPU", "V", "small", null, cpuSensorComponentId, cpuSensorComponentName);
        AddSnapshotMetric(definitions, items, "cpu.igpuTemperature", "核显温度", "CPU", "°C", "small", null, cpuSensorComponentId, cpuSensorComponentName);
        AddSnapshotMetric(definitions, items, "cpu.smuFrequency", "CPU SMU 频率", "CPU", "MHz", "small", null, cpuSensorComponentId, cpuSensorComponentName);
        AddSnapshotMetric(
            definitions,
            items,
            "cpu.fanRpm",
            "CPU 风扇转速",
            "CPU",
            "RPM",
            "small",
            requiredComponentId: RequiredFanComponentWhenUnavailable(items, "cpu.fanRpm"));
        AddSnapshotMetric(
            definitions,
            items,
            "cpu.fanPercent",
            "CPU 风扇百分比",
            "CPU",
            "%",
            "small",
            requiredComponentId: RequiredFanComponentWhenUnavailable(items, "cpu.fanPercent"));
    }

    private static string? ResolveCpuSensorComponentId(string cpuName)
    {
        if (cpuName.Contains("AMD", StringComparison.OrdinalIgnoreCase)
            || cpuName.Contains("Ryzen", StringComparison.OrdinalIgnoreCase))
        {
            return "amd-smu-pawnio-provider";
        }

        if (cpuName.Contains("Intel", StringComparison.OrdinalIgnoreCase))
        {
            return "intel-pcm-provider";
        }

        return null;
    }
}
