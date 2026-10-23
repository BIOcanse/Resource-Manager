namespace ResourceManager.App.Domain.Metrics;

internal sealed record MetricPhysicalReadingRule(string Quantity, int SourceRank, int AliasRank = 0);

public static partial class MetricFieldRegistry
{
    // Equivalence and accuracy order are declared here. Unrelated vendor
    // quantities (including reserved/committed/resident memory) stay distinct.
    internal static MetricPhysicalReadingRule ReadingRule(MetricFieldDefinition field,
        string source, bool nvmeDevice, string? declaredQuantity = null)
        => (field.Code, source) switch
        {
            (MetricFieldCode.AmdCcdTdieTemperature, "amd.smn") => new("cpu.ccd_die_temperature", 10),
            (MetricFieldCode.CpuTemperature, "amd.smu") => new("cpu.control_temperature", 10),
            (MetricFieldCode.CpuTemperature, "hardware-monitor.core")
                => new(declaredQuantity ?? "cpu.temperature_reported", 20),
            (MetricFieldCode.IntelPackageDtsTemperature, "intel.msr") => new("cpu.package_temperature", 10),
            (MetricFieldCode.IgclGpuTemperature, "intel.igcl") => new("gpu.highest_sensor_temperature", 10),
            (MetricFieldCode.IgclGpuIntervalPower, "intel.igcl") => new("gpu.chip_interval_power", 10),
            (MetricFieldCode.IgclGpuVoltage, "intel.igcl") => new("gpu.supply_voltage", 10),
            (MetricFieldCode.CpuIgpuTemperature, "amd.smu") => new("gpu.edge_temperature", 10, 1),
            (MetricFieldCode.GpuTemperature, "amd.smu") => new("gpu.edge_temperature", 10),
            (MetricFieldCode.GpuTemperature, "amd.adlx") => new("gpu.edge_temperature", 20),
            (MetricFieldCode.NvmeCompositeTemperature, "windows.nvme-health")
                => new("storage.composite_temperature", 10),
            (MetricFieldCode.DiskTemperature, "windows.storage-sensors") when nvmeDevice
                => new("storage.composite_temperature", 20),
            (MetricFieldCode.GpuMemoryUsed, "amd.adlx") => new("gpu.dedicated_memory_used_reported", 10),
            (MetricFieldCode.AdlxDedicatedMemoryMb, "amd.adlx") => new("gpu.dedicated_memory_used_reported", 10, 1),
            (MetricFieldCode.CpuUsage, _) => new("cpu.busy_percent", 10),
            (MetricFieldCode.CpuFrequency, _) => new("cpu.frequency_reported", 10),
            (MetricFieldCode.CpuFrequencyPercent, _) => new("cpu.frequency_reference_percent", 10),
            (MetricFieldCode.CpuActualPower, "amd.smu") => new("cpu.package_power_actual", 10),
            (MetricFieldCode.CpuCoreVoltage, "amd.smu") => new("cpu.core_voltage_reported", 10),
            (MetricFieldCode.CpuPackageCurrent, "amd.smu") => new("cpu.package_current_reported", 10),
            (MetricFieldCode.CpuFanRpm, _) => new("cpu.cooling_fan_rpm", 10),
            (MetricFieldCode.CpuFanPercent, _) => new("cpu.cooling_fan_speed_percent", 10),
            (MetricFieldCode.MemoryUsed, _) => new("memory.physical_used", 10),
            (MetricFieldCode.MemoryPercent, _) => new("memory.physical_used_percent", 10),
            (MetricFieldCode.CommitUsed, _) => new("memory.commit_used", 10),
            (MetricFieldCode.CommitPercent, _) => new("memory.commit_used_percent", 10),
            (MetricFieldCode.MemoryTemperature, _) => new("memory.temperature_reported", 10),
            (MetricFieldCode.GpuUsage, "pdh.gpu-engine") => new("gpu.busiest_engine_busy_percent", 10),
            (MetricFieldCode.GpuGraphicsClock, _) => new("gpu.graphics_clock_reported", 10),
            (MetricFieldCode.GpuGraphicsClockPercent, _) => new("gpu.graphics_clock_reference_percent", 10),
            (MetricFieldCode.GpuMemoryClock, _) => new("gpu.memory_clock_reported", 10),
            (MetricFieldCode.GpuFanRpm, _) => new("gpu.cooling_fan_rpm", 10),
            (MetricFieldCode.GpuFanPercent, _) => new("gpu.cooling_fan_speed_percent", 10),
            (MetricFieldCode.GpuCurrent, _) => new("gpu.current_reported", 10),
            (MetricFieldCode.GpuVoltage, _) => new("gpu.core_voltage_reported", 10),
            (MetricFieldCode.GpuPower, "amd.adlx") => new("gpu.chip_power_reported", 10),
            (MetricFieldCode.GpuPower, "nvidia.nvml") => new("gpu.power_nvml_reported", 10),
            (MetricFieldCode.GpuTemperature, "nvidia.nvml") => new("gpu.core_temperature", 10),
            (MetricFieldCode.GpuMemoryUsed, "nvidia.nvml") => new("gpu.fb_used_including_reserved", 10),
            (MetricFieldCode.GpuMemoryPercent, "nvidia.nvml") => new("gpu.fb_used_including_reserved_percent", 10),
            (MetricFieldCode.GpuMemoryPercent, "amd.adlx") => new("gpu.dedicated_memory_used_reported_percent", 10),
            _ => new(field.Name, 100)
        };
}
