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
        // 核显的频率/电压/温度**不在这里露出**。
        //
        // 核显是一块 GPU，它在目录里的位置就是 GPU 那一组（本机是 GPU0）。
        // 先前这三项挂在 CPU 组、叫"核显频率/电压/温度"，和 GPU0 组里的
        // 同名指标指的是**同一颗核显**，只是走了另一条源（这边 amd.smu，
        // 那边 amd.adlx）—— 于是同一个东西在两个组里各出现一次，
        // 而且两边的可用性还不一致，用户看到的就是"一个能选一个置灰"。
        //
        // 按设备归组，不按数据来源归组。采集层那几个字段留着，
        // 等 SMU 这条路真能读出来的时候，接到 GPU 那一组里去。
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
