using ResourceManager.ReadWrite.ProcessFields;

namespace ResourceManager.App.Domain.ResourceBreakdown;

public static class ProcessMemoryFactFields
{
    public static string? TotalMetricId(string field) => ProcessMetricFieldRegistry.Find(field)?.ResourceFactTotalMetricId;
}

public sealed record ProcessMemoryFact(string MetricId, string TotalMetricId, double? TotalValue,
    double? CapacityValue, double ProcessSumValue, IReadOnlyList<ProcessMemoryValue> Processes)
{
    public string Unit { get; init; } = "B";
    public bool AdditiveAcrossProcesses { get; init; } = true;
    public DateTimeOffset? CapturedAt { get; init; }
    public long? WindowMilliseconds { get; init; }
    public ProcessGpuEngineScope? GpuEngine { get; init; }
    public string? EngineClass { get; init; }
    public IReadOnlyList<ResourceSoftwareSegment> Residuals { get; init; } = [];
}

public sealed record ProcessMemoryValue(int ProcessId, long ProcessStartKey, double Value);
