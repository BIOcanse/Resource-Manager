using ResourceManager.App.Application.Optimization;
using ResourceManager.App.Domain.CpuTopology;
using ResourceManager.App.Domain.GpuPlacement;
using ResourceManager.App.Domain.Optimization;
using ResourceManager.App.Domain.RuntimeSpecialization;

namespace ResourceManager.App.Infrastructure.Optimization;

internal readonly record struct HostManagerIdealProcessorInput(
    HostManagerAutomaticPlacementProcess Process,
    IReadOnlyList<ThreadIdealProcessorPolicySnapshot> Threads,
    IReadOnlyList<uint> DefaultCpuSetIds);

internal readonly record struct HostManagerIdealProcessorPlacement(
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
        var workspace = new HostManagerIdealProcessorWorkspace();
        workspace.Inputs.AddRange(inputs);
        PlanIdealProcessorsIntoWorkspace(topology, hardwareScores, hardPlacements, reservations, workspace);
        return workspace.Plans.Count == 0
            ? Array.Empty<HostManagerIdealProcessorPlacement>()
            : workspace.Plans.ToArray();
    }

    internal static void PlanIdealProcessorsIntoWorkspace(
        CpuTopologySnapshot topology,
        CompiledHardwareScorePlan hardwareScores,
        IReadOnlyList<HostManagerAutomaticCpuPlacement> hardPlacements,
        HostManagerCpuReservations? reservations,
        HostManagerIdealProcessorWorkspace workspace)
    {
        workspace.ResetPlannerScratch();
        for (var coreIndex = 0; coreIndex < topology.PhysicalCores.Count; coreIndex++)
        {
            var core = topology.PhysicalCores[coreIndex];
            var capacity = hardwareScores.ResolveCpuPerformanceScore(core.Index, core.PerformanceScore);
            if (double.IsFinite(capacity) && capacity > 0)
            {
                workspace.Cores.Add(new(core, capacity));
            }
        }

        // Explicit placements consume the same position score; there is no second balancing policy.
        for (var placementIndex = 0; placementIndex < hardPlacements.Count; placementIndex++)
        {
            var placement = hardPlacements[placementIndex];
            if (placement.Process.Policy.CpuManualLockedPositionIds.Count == 0
                && !placement.Process.Policy.CpuMaximumOccupancyMode.Equals(
                    CpuMaximumOccupancyModes.SingleCcd,
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var selectedCount = 0;
            foreach (var core in workspace.Cores)
            {
                if (ContainsCoreId(placement.PhysicalCoreIds, core.Core.Id)) selectedCount++;
            }
            if (selectedCount == 0) continue;

            var share = placement.CanonicalProcessScore / selectedCount;
            for (var coreIndex = 0; coreIndex < workspace.Cores.Count; coreIndex++)
            {
                var core = workspace.Cores[coreIndex];
                if (!ContainsCoreId(placement.PhysicalCoreIds, core.Core.Id)) continue;
                core.AssignedScore += share;
                workspace.Cores[coreIndex] = core;
            }
        }

        workspace.LogicalLoads.Clear();
        for (var inputIndex = 0; inputIndex < workspace.Inputs.Count; inputIndex++)
        {
            var input = workspace.Inputs[inputIndex];
            if (IsEligibleInput(input))
            {
                workspace.OrderedInputs.Add(new(input, inputIndex));
            }
        }
        workspace.OrderedInputs.Sort(HostManagerIdealProcessorWorkspace.OrderedInputComparer.Comparison);

        foreach (var orderedInput in workspace.OrderedInputs)
        {
            var input = orderedInput.Input;
            FillExcludedCoreIds(reservations, input.Process.SoftwareId, input.Process.TargetId, workspace.ExcludedCoreIds);
            workspace.OrderedThreads.Clear();
            for (var threadIndex = 0; threadIndex < input.Threads.Count; threadIndex++)
            {
                workspace.OrderedThreads.Add(new(input.Threads[threadIndex], threadIndex));
            }
            workspace.OrderedThreads.Sort(HostManagerIdealProcessorWorkspace.OrderedThreadComparer.Comparison);

            var eligibleCount = 0;
            foreach (var orderedThread in workspace.OrderedThreads)
            {
                FillEligibleProcessors(input, orderedThread.Thread, topology, workspace, stopAfterFirst: true);
                if (workspace.CandidateProcessors.Count > 0) eligibleCount++;
            }
            if (eligibleCount == 0) continue;

            var share = input.Process.CanonicalCpuScore!.Value / eligibleCount;
            string? preferredCcd = null;
            foreach (var orderedThread in workspace.OrderedThreads)
            {
                var thread = orderedThread.Thread;
                FillEligibleProcessors(input, thread, topology, workspace);
                if (workspace.CandidateProcessors.Count == 0) continue;

                var chosenCoreIndex = ChooseCore(thread, preferredCcd, workspace);
                if (chosenCoreIndex < 0) continue;
                var chosenCore = workspace.Cores[chosenCoreIndex];
                var processorIndex = ChooseLogicalProcessor(thread, chosenCore.Core.Id, workspace);
                if (processorIndex < 0) continue;

                var processor = workspace.CandidateProcessors[processorIndex];
                workspace.Plans.Add(new(input.Process, thread, processor));
                chosenCore.AssignedScore += share;
                workspace.Cores[chosenCoreIndex] = chosenCore;
                workspace.LogicalLoads.TryGetValue(processor.Id, out var logicalLoad);
                workspace.LogicalLoads[processor.Id] = logicalLoad + share;
                preferredCcd ??= chosenCore.Core.CcdId;
            }
        }
    }

    private static bool IsEligibleInput(HostManagerIdealProcessorInput input)
        => input.Process.CanApplyPhysicalPlacement
            && input.Process.CanonicalCpuScore is >= 0
            && input.Process.Policy.CpuManualLockedPositionIds.Count == 0
            && input.Process.Policy.CpuMaximumOccupancyMode.Equals(
                CpuMaximumOccupancyModes.AllCores,
                StringComparison.OrdinalIgnoreCase);

    private static void FillExcludedCoreIds(
        HostManagerCpuReservations? reservations,
        string softwareId,
        string targetId,
        HashSet<string> excludedCoreIds)
    {
        excludedCoreIds.Clear();
        if (reservations is null) return;
        if (reservations.LeaseTargets.Count != 0)
        {
            excludedCoreIds.UnionWith(reservations.ExcludedFor(softwareId, targetId));
            return;
        }
        reservations.BySoftware.TryGetValue(softwareId, out var own);
        if (reservations.BySoftware is Dictionary<string, IReadOnlySet<string>> reservationMap)
        {
            foreach (var pair in reservationMap)
            {
                AddExcludedCoreIds(pair.Value, own, excludedCoreIds);
            }
        }
        else
        {
            foreach (var reservation in reservations.BySoftware.Values)
            {
                AddExcludedCoreIds(reservation, own, excludedCoreIds);
            }
        }
    }

    private static void AddExcludedCoreIds(
        IReadOnlySet<string> reservation,
        IReadOnlySet<string>? own,
        HashSet<string> excludedCoreIds)
    {
        if (reservation is HashSet<string> reservationSet)
        {
            foreach (var coreId in reservationSet)
            {
                if (own?.Contains(coreId) != true) excludedCoreIds.Add(coreId);
            }
            return;
        }

        foreach (var coreId in reservation)
        {
            if (own?.Contains(coreId) != true) excludedCoreIds.Add(coreId);
        }
    }

    private static void FillEligibleProcessors(
        HostManagerIdealProcessorInput input,
        ThreadIdealProcessorPolicySnapshot thread,
        CpuTopologySnapshot topology,
        HostManagerIdealProcessorWorkspace workspace,
        bool stopAfterFirst = false)
    {
        workspace.CandidateProcessors.Clear();
        workspace.CandidateCoreIds.Clear();
        if (thread.ProcessId != input.Process.ProcessId
            || thread.ProcessStartKey != input.Process.ProcessStartKey
            || thread.ThreadStartKey == 0
            || thread.IdealProcessor.Group != thread.AllowedGroup
            || thread.IdealProcessor.Number >= 64
            || (thread.AllowedMask & (1UL << thread.IdealProcessor.Number)) == 0)
        {
            return;
        }

        var cpuSetIds = thread.SelectedCpuSetIds.Count == 0
            ? input.DefaultCpuSetIds
            : thread.SelectedCpuSetIds;
        for (var processorIndex = 0; processorIndex < topology.LogicalProcessors.Count; processorIndex++)
        {
            var processor = topology.LogicalProcessors[processorIndex];
            if (processor.ProcessorGroup != thread.AllowedGroup
                || processor.GroupRelativeIndex is < 0 or >= 64
                || (thread.AllowedMask & (1UL << processor.GroupRelativeIndex)) == 0
                || workspace.ExcludedCoreIds.Contains(processor.PhysicalCoreId)
                || (cpuSetIds.Count != 0
                    && (!processor.CpuSetId.HasValue || !ContainsCpuSet(cpuSetIds, processor.CpuSetId.Value)))
                || !HasCore(workspace.Cores, processor.PhysicalCoreId))
            {
                continue;
            }

            workspace.CandidateProcessors.Add(processor);
            workspace.CandidateCoreIds.Add(processor.PhysicalCoreId);
            if (stopAfterFirst) return;
        }
    }

    private static int ChooseCore(
        ThreadIdealProcessorPolicySnapshot thread,
        string? preferredCcd,
        HostManagerIdealProcessorWorkspace workspace)
    {
        var chosenIndex = -1;
        for (var coreIndex = 0; coreIndex < workspace.Cores.Count; coreIndex++)
        {
            var candidate = workspace.Cores[coreIndex];
            if (!workspace.CandidateCoreIds.Contains(candidate.Core.Id)) continue;
            var candidateIdeal = HasPreferredProcessor(thread, candidate.Core.Id, workspace.CandidateProcessors);
            var candidatePreferredCcd = candidate.Core.CcdId.Equals(preferredCcd, StringComparison.OrdinalIgnoreCase);
            if (chosenIndex < 0 || IsBetterCore(candidate, candidateIdeal, candidatePreferredCcd,
                    workspace.Cores[chosenIndex], HasPreferredProcessor(
                        thread, workspace.Cores[chosenIndex].Core.Id, workspace.CandidateProcessors),
                    workspace.Cores[chosenIndex].Core.CcdId.Equals(preferredCcd, StringComparison.OrdinalIgnoreCase)))
            {
                chosenIndex = coreIndex;
            }
        }
        return chosenIndex;
    }

    private static bool IsBetterCore(
        HostManagerIdealProcessorWorkspace.IdealCoreState candidate,
        bool candidateIdeal,
        bool candidatePreferredCcd,
        HostManagerIdealProcessorWorkspace.IdealCoreState current,
        bool currentIdeal,
        bool currentPreferredCcd)
    {
        var comparison = (candidate.AssignedScore / candidate.Capacity)
            .CompareTo(current.AssignedScore / current.Capacity);
        if (comparison != 0) return comparison < 0;
        if (candidateIdeal != currentIdeal) return candidateIdeal;
        if (candidatePreferredCcd != currentPreferredCcd) return candidatePreferredCcd;
        comparison = candidate.Capacity.CompareTo(current.Capacity);
        if (comparison != 0) return comparison > 0;
        comparison = candidate.Core.Index.CompareTo(current.Core.Index);
        return comparison < 0;
    }

    private static int ChooseLogicalProcessor(
        ThreadIdealProcessorPolicySnapshot thread,
        string physicalCoreId,
        HostManagerIdealProcessorWorkspace workspace)
    {
        var selectedIndex = -1;
        var selectedLoad = 0D;
        var selectedIdeal = 0;
        var selectedGroupRelativeIndex = 0;
        for (var processorIndex = 0; processorIndex < workspace.CandidateProcessors.Count; processorIndex++)
        {
            var candidate = workspace.CandidateProcessors[processorIndex];
            if (!candidate.PhysicalCoreId.Equals(physicalCoreId, StringComparison.OrdinalIgnoreCase)) continue;
            workspace.LogicalLoads.TryGetValue(candidate.Id, out var candidateLoad);
            var loadComparison = candidateLoad.CompareTo(selectedLoad);
            var candidateIdeal = candidate.GroupRelativeIndex == thread.IdealProcessor.Number ? 0 : 1;
            if (selectedIndex < 0
                || loadComparison < 0
                || loadComparison == 0 && candidateIdeal < selectedIdeal
                || loadComparison == 0 && candidateIdeal == selectedIdeal
                    && candidate.GroupRelativeIndex < selectedGroupRelativeIndex)
            {
                selectedIndex = processorIndex;
                selectedLoad = candidateLoad;
                selectedIdeal = candidateIdeal;
                selectedGroupRelativeIndex = candidate.GroupRelativeIndex;
            }
        }
        return selectedIndex;
    }

    private static bool HasPreferredProcessor(
        ThreadIdealProcessorPolicySnapshot thread,
        string physicalCoreId,
        IReadOnlyList<CpuLogicalProcessorModel> processors)
    {
        for (var index = 0; index < processors.Count; index++)
        {
            var processor = processors[index];
            if (processor.PhysicalCoreId.Equals(physicalCoreId, StringComparison.OrdinalIgnoreCase)
                && processor.ProcessorGroup == thread.IdealProcessor.Group
                && processor.GroupRelativeIndex == thread.IdealProcessor.Number)
            {
                return true;
            }
        }
        return false;
    }

    private static bool HasCore(
        IReadOnlyList<HostManagerIdealProcessorWorkspace.IdealCoreState> cores,
        string physicalCoreId)
    {
        for (var index = 0; index < cores.Count; index++)
        {
            var core = cores[index];
            if (core.Core.Id.Equals(physicalCoreId, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static bool ContainsCoreId(IReadOnlyList<string> coreIds, string physicalCoreId)
    {
        for (var index = 0; index < coreIds.Count; index++)
        {
            var coreId = coreIds[index];
            if (coreId.Equals(physicalCoreId, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static bool ContainsCpuSet(IReadOnlyList<uint> cpuSetIds, uint cpuSetId)
    {
        for (var index = 0; index < cpuSetIds.Count; index++)
        {
            if (cpuSetIds[index] == cpuSetId) return true;
        }
        return false;
    }
}
