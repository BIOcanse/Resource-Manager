using System.Text.Json.Serialization;

namespace ResourceManager.App.Domain.CpuTopology;

public static class CpuTopologySelectionKinds
{
    public const string LogicalProcessor = "LogicalProcessor";
    public const string PhysicalCore = "PhysicalCore";
    public const string Ccd = "Ccd";
}

public static class CpuTopologyAffinityTargetKinds
{
    public const string LogicalProcessorMask = "LogicalProcessorMask";
}

public static class CpuTopologyVisualLayoutKinds
{
    public const string RingBus = "RingBus";
    public const string CcdGrid = "CcdGrid";
    public const string Grid = "Grid";
}

public sealed record CpuTopologyVisualLayout(
    string Kind,
    string Source);

public sealed record CpuCorePerformanceOverrideItem(
    int CoreIndex,
    double? PerformanceScore);

public sealed record CpuCorePerformanceOverrideRequest(
    string CpuName,
    IReadOnlyList<CpuCorePerformanceOverrideItem> Scores);

public sealed record CpuCorePerformanceOverrideResult(
    string CpuName,
    IReadOnlyDictionary<int, double> ScoresByCoreIndex,
    DateTimeOffset UpdatedAt,
    string StoragePath);

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
    IReadOnlyList<string> Notes);

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
    string Source);

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

public static class CpuTopologyVisualLayoutResolver
{
    public static CpuTopologyVisualLayout Resolve(
        CpuCorePerformancePreset preset,
        int ccdCount)
    {
        if (preset.Vendor.Equals("Intel", StringComparison.OrdinalIgnoreCase))
        {
            return new CpuTopologyVisualLayout(
                CpuTopologyVisualLayoutKinds.RingBus,
                "vendor:intel");
        }

        if (preset.Vendor.Equals("AMD", StringComparison.OrdinalIgnoreCase))
        {
            return new CpuTopologyVisualLayout(
                CpuTopologyVisualLayoutKinds.CcdGrid,
                "vendor:amd-ccd-grid");
        }

        if (ccdCount > 1)
        {
            return new CpuTopologyVisualLayout(
                CpuTopologyVisualLayoutKinds.CcdGrid,
                "topology:multi-ccd");
        }

        return new CpuTopologyVisualLayout(
            CpuTopologyVisualLayoutKinds.Grid,
            "topology:single-group");
    }
}
