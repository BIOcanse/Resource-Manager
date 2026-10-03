using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.Overlay;
using ResourceManager.App.Domain.Messages;

namespace ResourceManager.App.Application.Metrics;

public static partial class MetricCatalog
{
    public static IReadOnlyList<MetricDefinition> FromSnapshot(HardwareMetricSnapshot snapshot)
    {
        var cpuSensorComponentId = ResolveCpuSensorComponentId(snapshot.Cpu.Name);
        var cpuSensorComponentName = ComponentName(cpuSensorComponentId);
        var cpuFrequencyDetail =
            $"{snapshot.Cpu.FrequencySource} / 参考 {snapshot.Cpu.MaxFrequencyMhz:N0} MHz";
        var definitions = new List<MetricDefinition>
        {
            new("cpu.usage", "CPU 占用率", MetricGroups.Cpu, "%", "main", snapshot.Cpu.Name),
            new("cpu.frequency", "CPU 频率", MetricGroups.Cpu, "MHz", "small", cpuFrequencyDetail),
            new("cpu.frequencyPercent", "CPU 频率百分比", MetricGroups.Cpu, "%", "small", cpuFrequencyDetail),
            new("memory.usage", "物理内存实际占用", MetricGroups.Memory, MetricUnits.Bytes, "main", snapshot.Memory.HardwareDescription),
            new("memory.percent", "物理内存实际占用率", MetricGroups.Memory, "%", "small", snapshot.Memory.HardwareDescription)
        };

        AddSystemIoDefinitions(definitions, snapshot.Items);
        AddCpuSensorDefinitions(definitions, snapshot.Items, cpuSensorComponentId, cpuSensorComponentName);
        AddMemoryAndSystemSensorDefinitions(definitions, snapshot.Items);
        AddMetric(definitions, snapshot.Items, "virtualMemory.usage", "虚拟内存提交量", MetricGroups.VirtualMemory, MetricUnits.Bytes, "main", snapshot.VirtualMemory.Detail);
        AddMetric(definitions, snapshot.Items, "virtualMemory.percent", "虚拟内存提交率", MetricGroups.VirtualMemory, "%", "small", snapshot.VirtualMemory.Detail);

        var gpuIndexes = snapshot.Gpus
            .Select(static gpu => gpu.Index)
            .Concat(GetGpuIndexes(snapshot.Items))
            .Where(static index => index >= 0)
            .Distinct()
            .Order()
            .ToArray();
        if (gpuIndexes.Length == 0)
        {
            definitions.AddRange(CreateGpuDefinitions(0, "GPU0", "No NVIDIA GPU telemetry"));
            return definitions;
        }

        foreach (var index in gpuIndexes)
        {
            var gpu = snapshot.Gpus.FirstOrDefault(candidate => candidate.Index == index);
            var detail = snapshot.Items.TryGetValue($"gpu.{index}.usage", out var usage)
                ? usage.Detail ?? $"GPU{index}"
                : $"GPU{index}";
            definitions.AddRange(CreateGpuDefinitions(
                index,
                MetricGroups.Gpu(index),
                detail,
                snapshot.Items,
                gpu?.IdentityKey,
                usageCapabilityKnown: gpu is not null));
        }

        return definitions;
    }

    public static IReadOnlyList<MetricDefinition> ForPerformanceOverlay(HardwareMetricSnapshot? snapshot)
        => snapshot is null
            ? TargetSoftwareDefinitions(null)
            : FromSnapshot(snapshot).Concat(TargetSoftwareDefinitions(snapshot)).ToArray();

    public static IReadOnlyList<MetricDefinition> TargetSoftwareDefinitions(HardwareMetricSnapshot? snapshot) =>
    [
        new(PerformanceOverlayMetricIds.Fps, "目标帧率", "target-software", "FPS", "main"),
        new(PerformanceOverlayMetricIds.FrameTime, "目标帧时间", "target-software", "ms", "small"),
        new(PerformanceOverlayMetricIds.OnePercentLow, "目标 1% Low", "target-software", "FPS", "small"),
        new(PerformanceOverlayMetricIds.PointOnePercentLow, "目标 0.1% Low", "target-software", "FPS", "small"),
        new(PerformanceOverlayMetricIds.Cpu, "目标进程 CPU", "target-software", "%", "small"),
        new(PerformanceOverlayMetricIds.Gpu, "目标进程 GPU", "target-software", "%", "small",
            Selectable: snapshot is null || snapshot.Gpus.Count > 0,
            DisabledReason: snapshot is not null && snapshot.Gpus.Count == 0
                ? BackendMessage.Create(BackendMessageDomains.Metric, BackendMessageCodes.Metric.NotExposed)
                : null),
        new(PerformanceOverlayMetricIds.Memory, "目标进程内存", "target-software", MetricUnits.Bytes, "small"),
        new(PerformanceOverlayMetricIds.Vram, "目标进程显存", "target-software", MetricUnits.Bytes, "small",
            Selectable: snapshot is null || snapshot.Gpus.Count > 0,
            DisabledReason: snapshot is not null && snapshot.Gpus.Count == 0
                ? BackendMessage.Create(BackendMessageDomains.Metric, BackendMessageCodes.Metric.NotExposed)
                : null)
    ];
}
