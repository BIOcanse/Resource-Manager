using ResourceManager.ReadWrite.ProcessFields;

namespace ResourceManager.App.Domain.ResourceBreakdown;

public static class ProcessMemoryFactFields
{
    public static string? TotalMetricId(string field) => ProcessMetricFieldRegistry.Find(field)?.Code switch
    {
        ProcessMetricFieldCode.WorkingSet or ProcessMetricFieldCode.PrivateWorkingSet => "memory.usage",
        ProcessMetricFieldCode.PrivateCommit => "virtualMemory.usage",
        _ => null
    };
}

public sealed record ProcessMemoryFact(string MetricId, string TotalMetricId, double? TotalValue,
    double? CapacityValue, double ProcessSumValue, IReadOnlyList<ProcessMemoryValue> Processes)
{
    public IReadOnlyList<ResourceSoftwareSegment> Residuals { get; init; } = [];
}

public sealed record ProcessMemoryValue(int ProcessId, long ProcessStartKey, double Value);
