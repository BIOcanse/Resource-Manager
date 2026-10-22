using ResourceManager.App.Domain.CpuTopology;
using ResourceManager.App.Domain.GpuPlacement;
using ResourceManager.App.Domain.Optimization;
using ResourceManager.App.Domain.RuntimeSpecialization;

namespace ResourceManager.App.Infrastructure.Optimization;

internal sealed record HostManagerIdealProcessorInput(
    HostManagerAutomaticPlacementProcess Process,
    IReadOnlyList<ThreadIdealProcessorPolicySnapshot> Threads,
    IReadOnlyList<uint> DefaultCpuSetIds);

internal sealed record HostManagerIdealProcessorPlacement(
    HostManagerAutomaticPlacementProcess Process,
    ThreadIdealProcessorPolicySnapshot Thread,
    CpuLogicalProcessorModel Processor);

internal static partial class HostManagerAutomaticPlacementPlanner
{
    internal static IReadOnlyList<HostManagerIdealProcessorPlacement> PlanIdealProcessors(
        CpuTopologySnapshot topology,
        CompiledHardwareScorePlan hardwareScores,
        IReadOnlyList<HostManagerIdealProcessorInput> inputs,
        IReadOnlyList<HostManagerAutomaticCpuPlacement> hardPlacements,
        HostManagerCpuReservations? reservations)
    {
        var cores = topology.PhysicalCores.Select(core => new IdealCoreState(core,
            hardwareScores.ResolveCpuPerformanceScore(core.Index, core.PerformanceScore)))
            .Where(static core => double.IsFinite(core.Capacity) && core.Capacity > 0).ToArray();
        // Explicit placements consume the same position score; there is no second balancing policy.
        foreach (var placement in hardPlacements.Where(static p => p.Process.Policy.CpuManualLockedPositionIds.Count > 0
            || p.Process.Policy.CpuMaximumOccupancyMode.Equals(CpuMaximumOccupancyModes.SingleCcd, StringComparison.OrdinalIgnoreCase)))
        {
            var selected = cores.Where(c => placement.PhysicalCoreIds.Contains(c.Core.Id, StringComparer.OrdinalIgnoreCase)).ToArray();
            foreach (var core in selected) core.AssignedScore += placement.CanonicalProcessScore / selected.Length;
        }
        var result = new List<HostManagerIdealProcessorPlacement>();
        var logicalLoads = new Dictionary<int, double>();
        foreach (var input in inputs.Where(static i => i.Process.CanApplyPhysicalPlacement
                && i.Process.CanonicalCpuScore is >= 0
                && i.Process.Policy.CpuManualLockedPositionIds.Count == 0
                && i.Process.Policy.CpuMaximumOccupancyMode.Equals(CpuMaximumOccupancyModes.AllCores, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(static i => i.Process.CanonicalCpuScore)
            .ThenBy(static i => i.Process.TargetId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static i => i.Process.ProcessId))
        {
            var excluded = reservations?.ExcludedFor(input.Process.SoftwareId);
            var eligible = new List<(ThreadIdealProcessorPolicySnapshot Thread, CpuLogicalProcessorModel[] Processors)>();
            foreach (var thread in input.Threads.OrderBy(static t => t.ThreadId))
            {
                if (thread.ProcessId != input.Process.ProcessId || thread.ProcessStartKey != input.Process.ProcessStartKey
                    || thread.ThreadStartKey == 0 || thread.IdealProcessor.Group != thread.AllowedGroup
                    || thread.IdealProcessor.Number >= 64
                    || (thread.AllowedMask & (1UL << thread.IdealProcessor.Number)) == 0) continue;
                var sets = thread.SelectedCpuSetIds.Count == 0 ? input.DefaultCpuSetIds : thread.SelectedCpuSetIds;
                var processors = topology.LogicalProcessors.Where(p => p.ProcessorGroup == thread.AllowedGroup
                    && p.GroupRelativeIndex is >= 0 and < 64
                    && (thread.AllowedMask & (1UL << p.GroupRelativeIndex)) != 0
                    && excluded?.Contains(p.PhysicalCoreId) != true
                    && (sets.Count == 0 || (p.CpuSetId.HasValue && sets.Contains(p.CpuSetId.Value)))
                    && cores.Any(c => c.Core.Id.Equals(p.PhysicalCoreId, StringComparison.OrdinalIgnoreCase))).ToArray();
                if (processors.Length > 0) eligible.Add((thread, processors));
            }
            if (eligible.Count == 0) continue;
            var share = input.Process.CanonicalCpuScore!.Value / eligible.Count;
            string? preferredCcd = null;
            foreach (var (thread, processors) in eligible)
            {
                var allowedCores = processors.Select(static p => p.PhysicalCoreId).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var chosen = cores.Where(c => allowedCores.Contains(c.Core.Id))
                    .OrderBy(static c => c.AssignedScore / c.Capacity)
                    .ThenBy(c => processors.Any(p => p.PhysicalCoreId.Equals(c.Core.Id, StringComparison.OrdinalIgnoreCase)
                        && p.ProcessorGroup == thread.IdealProcessor.Group && p.GroupRelativeIndex == thread.IdealProcessor.Number) ? 0 : 1)
                    .ThenBy(c => c.Core.CcdId.Equals(preferredCcd, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                    .ThenByDescending(static c => c.Capacity)
                    .ThenBy(static c => c.Core.Index).First();
                var processor = processors.Where(p => p.PhysicalCoreId.Equals(chosen.Core.Id, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(p => logicalLoads.GetValueOrDefault(p.Id))
                    .ThenBy(p => p.GroupRelativeIndex == thread.IdealProcessor.Number ? 0 : 1)
                    .ThenBy(static p => p.GroupRelativeIndex).First();
                result.Add(new(input.Process, thread, processor));
                chosen.AssignedScore += share;
                logicalLoads[processor.Id] = logicalLoads.GetValueOrDefault(processor.Id) + share;
                preferredCcd ??= chosen.Core.CcdId;
            }
        }
        return result;
    }

    private sealed class IdealCoreState(CpuPhysicalCoreModel core, double capacity)
    {
        internal CpuPhysicalCoreModel Core { get; } = core;
        internal double Capacity { get; } = capacity;
        internal double AssignedScore { get; set; }
    }
}
