using ResourceManager.App.Domain.CpuTopology;
using ResourceManager.App.Domain.Optimization;

namespace ResourceManager.App.Application.Optimization.Scheduling;

public static class CpuAllowedSetPlanBuilder
{
    public static CpuAffinityPlan CreatePlan(
        CpuTopologySnapshot topology,
        IReadOnlyList<string> allowedPhysicalCoreIds)
    {
        ArgumentNullException.ThrowIfNull(topology);
        ArgumentNullException.ThrowIfNull(allowedPhysicalCoreIds);

        var allowedSet = allowedPhysicalCoreIds
            .Where(static id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (allowedSet.Count == 0)
        {
            return CpuAffinityPlanner.CreateUnavailablePlan(
                topology.LogicalProcessorCount,
                "CpuPhysicalAvoidance",
                "CPU 排除计划没有剩余物理核心，不写入进程允许集合。");
        }

        var logicalProcessors = topology.LogicalProcessors
            .Where(logical => allowedSet.Contains(logical.PhysicalCoreId))
            .DistinctBy(static logical => logical.Id)
            .OrderBy(static logical => logical.Id)
            .ToArray();
        if (logicalProcessors.Length == 0 || logicalProcessors.Any(static logical => !logical.AffinitySelectable))
        {
            return CpuAffinityPlanner.CreateUnavailablePlan(
                topology.LogicalProcessorCount,
                "CpuPhysicalAvoidance",
                "CPU 排除计划的逻辑处理器映射不完整或不可选择。");
        }

        var cpuSetIds = logicalProcessors.Select(static logical => logical.CpuSetId).ToArray();
        if (cpuSetIds.All(static id => id.HasValue && id.Value != 0))
        {
            return CpuAffinityPlanner.CreateCpuSetPlan(
                topology.LogicalProcessorCount,
                logicalProcessors.Select(static logical => logical.Id).ToArray(),
                cpuSetIds.Select(static id => id!.Value).ToArray(),
                "CpuPhysicalAvoidanceCpuSets",
                "CPU 排除式调度允许的逻辑处理器");
        }

        if (logicalProcessors.Any(static logical => logical.ProcessorGroup != 0 || logical.GroupRelativeIndex is < 0 or >= 64)
            || topology.LogicalProcessorCount > 64)
        {
            return CpuAffinityPlanner.CreateUnavailablePlan(
                topology.LogicalProcessorCount,
                "CpuPhysicalAvoidanceCpuSets",
                "CPU 排除计划跨 processor group，但 CPU Set ID 映射不完整。");
        }

        long mask = 0;
        foreach (var logical in logicalProcessors)
        {
            mask |= 1L << logical.GroupRelativeIndex;
        }

        return CpuAffinityPlanner.CreateExplicitLogicalProcessorPlan(
            topology.LogicalProcessorCount,
            mask,
            "CpuPhysicalAvoidanceAffinity",
            "CPU 排除式调度允许的逻辑处理器");
    }
}
