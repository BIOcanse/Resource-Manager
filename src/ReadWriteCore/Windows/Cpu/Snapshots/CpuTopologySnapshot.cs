using System.Text.Json.Serialization;
using ResourceManager.ReadWrite.Windows.Cpu.Models;
using ResourceManager.ReadWrite.Windows.Devices.Models;

namespace ResourceManager.App.Domain.CpuTopology;

public sealed record CpuTopologySnapshot(
    DateTimeOffset CapturedAt,
    string CpuName,
    CpuSpecificationModel Specification,
    string TopologySource,
    string UsageSource,
    string AffinityTargetKind,
    string VisualLayoutKind,
    string VisualLayoutSource,
    int PhysicalCoreCount,
    int LogicalProcessorCount,
    int CcdCount,
    bool SimultaneousMultithreading,
    IReadOnlyList<CpuCcdModel> Ccds,
    IReadOnlyList<CpuPhysicalCoreModel> PhysicalCores,
    IReadOnlyList<CpuLogicalProcessorModel> LogicalProcessors,
    IReadOnlyList<string> Notes)
{
    public IReadOnlyList<CpuCacheInformation> Caches { get; init; } = [];
    // Internal provenance for canonical fields; the legacy wire model keeps its existing shape.
    [JsonIgnore] public CpuOsTopologyInformation? OsTopologyInformation { get; init; }
}

public sealed record CpuSpecificationModel(
    string Name,
    string Vendor,
    string Family,
    int PhysicalCoreCount,
    int LogicalProcessorCount,
    int? MaxClockSpeedMhz,
    int? CurrentClockSpeedMhz,
    int? L2CacheSizeKb,
    int? L3CacheSizeKb,
    string Source)
{
    public CpuIdentityInformation? Cpuid { get; init; }
}

public sealed record CpuCcdModel(
    string Id,
    int Index,
    string Label,
    double? UsagePercent,
    IReadOnlyList<int> PhysicalCoreIndexes,
    IReadOnlyList<int> LogicalProcessorIds,
    string Source,
    [property: JsonIgnore] double CoreAveragePerformanceScore = 0);

public sealed record CpuPhysicalCoreModel(
    string Id,
    int Index,
    string Label,
    string CcdId,
    int EfficiencyClass,
    double PerformanceScore,
    double? UsagePercent,
    IReadOnlyList<int> LogicalProcessorIds,
    IReadOnlyList<CpuCoreCacheLevelModel> CacheLevels);

public sealed record CpuCoreCacheLevelModel(
    int Level,
    int? SizeKb,
    IReadOnlyList<int> LogicalProcessorIds,
    string Scope);

public sealed record CpuLogicalProcessorModel(
    int Id,
    int ProcessorGroup,
    int GroupRelativeIndex,
    string PhysicalCoreId,
    string CcdId,
    double PerformanceScore,
    double? UsagePercent,
    bool AffinitySelectable,
    uint? CpuSetId = null);

public static class CpuTopologyMath
{
    public static double? AverageUsage(IEnumerable<double?> usageValues)
    {
        var values = usageValues
            .Where(static value => value.HasValue)
            .Select(static value => value!.Value)
            .ToArray();
        if (values.Length == 0)
        {
            return null;
        }

        return Math.Round(values.Average(), 2);
    }

}


