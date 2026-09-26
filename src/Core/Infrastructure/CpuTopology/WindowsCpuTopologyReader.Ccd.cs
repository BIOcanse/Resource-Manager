using ResourceManager.App.Infrastructure.Windows;

namespace ResourceManager.App.Infrastructure.CpuTopology;

public sealed partial class WindowsCpuTopologyReader
{
    private IReadOnlyList<CcdRecord> ReadCcdRecords(
        string cpuName,
        IReadOnlyList<PhysicalCoreRecord> coreRecords,
        int logicalProcessorCount,
        IReadOnlyList<CpuSetRecord> cpuSetRecords,
        ICollection<string> notes)
    {
        var dieRecords = TryReadProcessorRelationshipGroups(NativeMethods.RelationProcessorDie, "Die", notes);
        if (dieRecords.Count > 1)
        {
            return CreateCcdRecords(dieRecords, "RelationProcessorDie");
        }

        var cpuSetLastLevelCacheRecords = TryReadCpuSetLastLevelCacheGroups(cpuSetRecords);
        if (cpuSetLastLevelCacheRecords.Count > 1)
        {
            notes.Add("OS 未暴露多 Die；使用 Windows CPU Sets LastLevelCacheIndex 近似 CCD/L3 分组。");
            return CreateCcdRecords(cpuSetLastLevelCacheRecords, "CpuSetLastLevelCacheIndex");
        }

        var l3Records = TryReadL3CacheGroups(notes);
        if (l3Records.Count > 1)
        {
            return CreateCcdRecords(l3Records, "L3CacheGroup");
        }

        if (IsAmdRyzen(cpuName)
            && coreRecords.Count == 16
            && logicalProcessorCount == 32)
        {
            notes.Add("OS 未暴露多 Die/L3 分组；AMD Ryzen 16C/32T fallback 按 2 个 8 核 CCD 建模。");
            var first = coreRecords
                .Where(static core => core.Index < 8)
                .SelectMany(static core => core.LogicalProcessors)
                .Select(static logical => logical.GlobalId)
                .ToArray();
            var second = coreRecords
                .Where(static core => core.Index >= 8)
                .SelectMany(static core => core.LogicalProcessors)
                .Select(static logical => logical.GlobalId)
                .ToArray();
            return CreateCcdRecords([first, second], "Amd16CoreFallback");
        }

        notes.Add("未识别到多个 CCD；全部核心归入单个 CPU group。");
        return CreateCcdRecords([Enumerable.Range(0, logicalProcessorCount).ToArray()], "SingleCpuGroup");
    }

    private IReadOnlyList<int[]> TryReadProcessorRelationshipGroups(
        int relationship,
        string displayName,
        ICollection<string> notes)
    {
        try
        {
            return QueryLogicalProcessorRecords(relationship)
                .Select(static record => record.LogicalProcessorIds.Distinct().Order().ToArray())
                .Where(static ids => ids.Length > 0)
                .ToArray();
        }
        catch (Exception ex) when (IsExpectedNativeException(ex))
        {
            notes.Add($"读取 {displayName} 分组失败：{ex.Message}");
            return [];
        }
    }

    private static IReadOnlyList<int[]> TryReadCpuSetLastLevelCacheGroups(IReadOnlyList<CpuSetRecord> cpuSetRecords)
    {
        return cpuSetRecords
            .Where(static record => record.LastLevelCacheIndex >= 0)
            .GroupBy(static record => new { record.Group, record.LastLevelCacheIndex })
            .Select(static group => group
                .Select(static record => record.GlobalLogicalProcessorId)
                .Distinct()
                .Order()
                .ToArray())
            .Where(static ids => ids.Length > 0)
            .ToArray();
    }

    private IReadOnlyList<int[]> TryReadL3CacheGroups(ICollection<string> notes)
    {
        try
        {
            return QueryCacheRecords()
                .Where(static record => record.Level == 3)
                .Select(static record => record.LogicalProcessorIds.Distinct().Order().ToArray())
                .Where(static ids => ids.Length > 0)
                .ToArray();
        }
        catch (Exception ex) when (IsExpectedNativeException(ex))
        {
            notes.Add($"读取 L3 cache 分组失败：{ex.Message}");
            return [];
        }
    }

    private static IReadOnlyList<CcdRecord> CreateCcdRecords(
        IReadOnlyList<int[]> groups,
        string source)
    {
        return groups
            .Select((ids, index) => new CcdRecord(
                $"ccd:{index}",
                index,
                ids.Distinct().Order().ToArray(),
                source))
            .ToArray();
    }
}
