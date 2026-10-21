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

// Passive snapshot records and CpuTopologyMath are owned by ReadWriteCore; original namespace is preserved.

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

