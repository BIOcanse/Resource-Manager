using ResourceManager.App.Domain.Metrics;

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
            new("cpu.usage", "CPU 占用率", "CPU", "%", "main", snapshot.Cpu.Name),
            new("cpu.frequency", "CPU 频率", "CPU", "MHz", "small", cpuFrequencyDetail),
            new("cpu.frequencyPercent", "CPU 频率百分比", "CPU", "%", "small", cpuFrequencyDetail),
            new("memory.usage", "内存占用", "Memory", "GB", "main", snapshot.Memory.HardwareDescription),
            new("memory.percent", "内存占用率", "Memory", "%", "small", snapshot.Memory.HardwareDescription)
        };

        AddSystemIoDefinitions(definitions, snapshot.Items);
        AddCpuSensorDefinitions(definitions, snapshot.Items, cpuSensorComponentId, cpuSensorComponentName);
        AddMemoryAndSystemSensorDefinitions(definitions, snapshot.Items);
        AddIfAvailable(definitions, snapshot.Items, "virtualMemory.usage", "虚拟内存占用", "Virtual Memory", "GB", "main", snapshot.VirtualMemory.Detail);
        AddIfAvailable(definitions, snapshot.Items, "virtualMemory.percent", "虚拟内存占用率", "Virtual Memory", "%", "small", snapshot.VirtualMemory.Detail);

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
                $"GPU{index}",
                detail,
                snapshot.Items,
                gpu?.IdentityKey,
                usageCapabilityKnown: gpu is not null));
        }

        return definitions;
    }
}
