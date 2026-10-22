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
            (MetricFieldCode.CpuTemperature, "amd.smu") => new("cpu.control_temperature", 10),
            (MetricFieldCode.CpuTemperature, "hardware-monitor.core")
                => new(declaredQuantity ?? "cpu.temperature_reported", 20),
            (MetricFieldCode.CpuIgpuTemperature, "amd.smu") => new("gpu.edge_temperature", 10, 1),
            (MetricFieldCode.GpuTemperature, "amd.smu") => new("gpu.edge_temperature", 10),
            (MetricFieldCode.GpuTemperature, "amd.adlx") => new("gpu.edge_temperature", 20),
            (MetricFieldCode.NvmeCompositeTemperature, "windows.nvme-health")
                => new("storage.composite_temperature", 10),
            (MetricFieldCode.DiskTemperature, "windows.storage-sensors") when nvmeDevice
                => new("storage.composite_temperature", 20),
            (MetricFieldCode.GpuMemoryUsed, "amd.adlx") => new("gpu.dedicated_memory_used_reported", 10),
            (MetricFieldCode.AdlxDedicatedMemoryMb, "amd.adlx") => new("gpu.dedicated_memory_used_reported", 10, 1),
            _ => new(field.Name, 100)
        };
}
