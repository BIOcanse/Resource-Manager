namespace ResourceManager.App.Domain.Optimization;

public sealed record CpuAffinityPlan(
    bool Available,
    string Source,
    int LogicalProcessorCount,
    long? AffinityMask,
    IReadOnlyList<int> LogicalProcessorIds,
    string Summary,
    string? DisabledReason,
    IReadOnlyList<uint>? CpuSetIds = null);

public static class CpuAffinityPlanner
{
    public static CpuAffinityPlan CreateExplicitLogicalProcessorPlan(
        int logicalProcessorCount,
        long affinityMask,
        string source,
        string summaryPrefix)
    {
        if (logicalProcessorCount <= 0)
        {
            return Disabled(logicalProcessorCount, source, "逻辑处理器数量不可用，暂不做 affinity 迁移。");
        }

        if (logicalProcessorCount > 64)
        {
            return Disabled(logicalProcessorCount, source, "逻辑处理器数量超过单 processor group affinity 安全范围，后续改用 CPU Sets。");
        }

        var unsignedMask = unchecked((ulong)affinityMask);
        var fullMask = BuildFullMask(logicalProcessorCount);
        if (unsignedMask == 0 || (unsignedMask & ~fullMask) != 0)
        {
            return Disabled(logicalProcessorCount, source, "CPU affinity mask 超出当前逻辑处理器范围，暂不做迁移。");
        }

        var ids = GetProcessorIds(affinityMask, logicalProcessorCount);
        return new CpuAffinityPlan(
            true,
            source,
            logicalProcessorCount,
            affinityMask,
            ids,
            $"{summaryPrefix}：{FormatProcessorIds(ids)}。",
            null);
    }

    public static CpuAffinityPlan CreateUnavailablePlan(
        int logicalProcessorCount,
        string source,
        string reason)
    {
        return Disabled(logicalProcessorCount, source, reason);
    }

    public static CpuAffinityPlan CreateCpuSetPlan(
        int logicalProcessorCount,
        IReadOnlyList<int> logicalProcessorIds,
        IReadOnlyList<uint> cpuSetIds,
        string source,
        string summaryPrefix)
    {
        if (logicalProcessorCount <= 0
            || logicalProcessorIds.Count == 0
            || cpuSetIds.Count != logicalProcessorIds.Count
            || cpuSetIds.Any(static id => id == 0))
        {
            return Disabled(logicalProcessorCount, source, "CPU Sets 映射不完整，暂不做处理器放置。");
        }

        return new CpuAffinityPlan(
            true,
            source,
            logicalProcessorCount,
            null,
            logicalProcessorIds.Distinct().Order().ToArray(),
            $"{summaryPrefix}：{FormatProcessorIds(logicalProcessorIds)}。",
            null,
            cpuSetIds.Distinct().Order().ToArray());
    }

    public static bool TryCreateFullMask(
        int logicalProcessorCount,
        out long mask)
    {
        mask = 0;
        if (logicalProcessorCount <= 0 || logicalProcessorCount > 64)
        {
            return false;
        }

        mask = unchecked((long)BuildFullMask(logicalProcessorCount));
        return true;
    }

    public static CpuAffinityPlan CreateRearLogicalProcessorPlan(int logicalProcessorCount)
    {
        if (logicalProcessorCount < 6)
        {
            return Disabled(logicalProcessorCount, "RearLogicalProcessors", "逻辑处理器数量过少，暂不做 affinity 迁移。");
        }

        if (logicalProcessorCount > 64)
        {
            return Disabled(logicalProcessorCount, "RearLogicalProcessors", "逻辑处理器数量超过单 processor group affinity 安全范围，后续改用 CPU Sets。");
        }

        var takeCount = ResolveProcessorCount(logicalProcessorCount);
        var start = logicalProcessorCount - takeCount;
        var ids = Enumerable.Range(start, takeCount).ToArray();
        var mask = BuildMask(ids);
        return new CpuAffinityPlan(
            true,
            "RearLogicalProcessors",
            logicalProcessorCount,
            mask,
            ids,
            $"移动到后侧逻辑处理器：{FormatProcessorIds(ids)}。",
            null);
    }

    public static CpuAffinityPlan CreatePrimaryLogicalProcessorPlan(int logicalProcessorCount)
    {
        if (logicalProcessorCount < 6)
        {
            return Disabled(logicalProcessorCount, "PrimaryLogicalProcessors", "逻辑处理器数量过少，暂不做 A2 affinity 迁移。");
        }

        if (logicalProcessorCount > 64)
        {
            return Disabled(logicalProcessorCount, "PrimaryLogicalProcessors", "逻辑处理器数量超过单 processor group affinity 安全范围，后续改用 CPU Sets。");
        }

        var reservedCount = ResolveProcessorCount(logicalProcessorCount);
        var takeCount = logicalProcessorCount - reservedCount;
        if (takeCount < 4)
        {
            return Disabled(logicalProcessorCount, "PrimaryLogicalProcessors", "可用主运行逻辑处理器过少，暂不做 A2 affinity 迁移。");
        }

        var ids = Enumerable.Range(0, takeCount).ToArray();
        var mask = BuildMask(ids);
        return new CpuAffinityPlan(
            true,
            "PrimaryLogicalProcessors",
            logicalProcessorCount,
            mask,
            ids,
            $"目标使用主运行逻辑处理器：{FormatProcessorIds(ids)}；后侧逻辑处理器留给系统和后台。",
            null);
    }

    public static string FormatMask(long mask)
    {
        return $"0x{mask:X}";
    }

    public static string FormatProcessorIds(IReadOnlyList<int> ids)
    {
        if (ids.Count == 0)
        {
            return "--";
        }

        return string.Join(", ", ids);
    }

    private static CpuAffinityPlan Disabled(
        int logicalProcessorCount,
        string source,
        string reason)
    {
        return new CpuAffinityPlan(
            false,
            source,
            logicalProcessorCount,
            null,
            [],
            reason,
            reason);
    }

    private static int ResolveProcessorCount(int logicalProcessorCount)
    {
        if (logicalProcessorCount <= 8)
        {
            return 2;
        }

        if (logicalProcessorCount <= 16)
        {
            return 4;
        }

        return Math.Max(4, logicalProcessorCount / 2);
    }

    private static long BuildMask(IEnumerable<int> processorIds)
    {
        long mask = 0;
        foreach (var processorId in processorIds)
        {
            mask |= 1L << processorId;
        }

        return mask;
    }

    private static ulong BuildFullMask(int logicalProcessorCount)
    {
        return logicalProcessorCount == 64
            ? ulong.MaxValue
            : (1UL << logicalProcessorCount) - 1;
    }

    private static IReadOnlyList<int> GetProcessorIds(
        long mask,
        int logicalProcessorCount)
    {
        var ids = new List<int>();
        for (var index = 0; index < logicalProcessorCount; index++)
        {
            if ((mask & (1L << index)) != 0)
            {
                ids.Add(index);
            }
        }

        return ids;
    }
}
