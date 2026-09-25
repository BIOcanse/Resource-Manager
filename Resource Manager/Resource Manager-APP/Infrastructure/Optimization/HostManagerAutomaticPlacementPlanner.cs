using ResourceManager.App.Application.Optimization.Scheduling;
using ResourceManager.App.Domain.CpuTopology;
using ResourceManager.App.Domain.GpuPlacement;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.Optimization;
using ResourceManager.App.Domain.RuntimeSpecialization;

namespace ResourceManager.App.Infrastructure.Optimization;

internal sealed record HostManagerAutomaticPlacementProcess(
    string TargetId,
    string SoftwareId,
    string DisplayName,
    int ProcessId,
    ulong ProcessStartKey,
    string ProcessName,
    string? ExecutablePath,
    bool CanApplyPhysicalPlacement,
    ResolvedGpuPlacementPolicy Policy,
    double? CanonicalCpuScore,
    IReadOnlyDictionary<ulong, double> CanonicalGpuScores,
    IReadOnlyDictionary<ulong, double> ObservedGpuUsagePercent)
{
    internal string? SoftwareKind { get; init; }
    internal IReadOnlyDictionary<ulong, double> ObservedDedicatedMemoryBytes { get; init; }
        = new Dictionary<ulong, double>();
    internal bool CanMigrateGpu { get; init; } = true;
}

internal sealed record HostManagerAutomaticCpuPlacement(
    HostManagerAutomaticPlacementProcess Process,
    double CanonicalProcessScore,
    IReadOnlyList<string> PhysicalCoreIds,
    CpuAffinityPlan Selector,
    string Source);

internal sealed record HostManagerAutomaticGpuPlacement(
    HostManagerAutomaticPlacementProcess Process,
    double CanonicalProcessScore,
    bool PreferIntegratedGpu,
    ulong TargetAdapterKey,
    string Source)
{
    internal ulong ObservedAdapterKey { get; init; }
}

internal sealed record HostManagerAutomaticPlacementPlan(
    IReadOnlyList<HostManagerAutomaticCpuPlacement> Cpu,
    IReadOnlyList<HostManagerAutomaticGpuPlacement> Gpu);

internal static class HostManagerAutomaticPlacementPlanner
{
    internal static HostManagerAutomaticPlacementPlan Plan(
        CpuTopologySnapshot? topology,
        HardwareMetricSnapshot hardware,
        CompiledHardwareScorePlan hardwareScores,
        IReadOnlyList<HostManagerAutomaticPlacementProcess> processes,
        CompiledHostManagerPlacementCoordinatorRecreatePlan capacity,
        bool runtimeGpuShimEnabled = false,
        CompiledGpuOverflowPolicy? gpuOverflow = null,
        bool cpuPlacementEnabled = true,
        bool gpuPlacementEnabled = true)
    {
        if (cpuPlacementEnabled)
        {
            ArgumentNullException.ThrowIfNull(topology);
        }
        ArgumentNullException.ThrowIfNull(hardware);
        ArgumentNullException.ThrowIfNull(hardwareScores);
        ArgumentNullException.ThrowIfNull(processes);
        ArgumentNullException.ThrowIfNull(capacity);
        ValidateCapacity(topology, processes, capacity);

        return new HostManagerAutomaticPlacementPlan(
            cpuPlacementEnabled ? PlanCpu(topology!, hardwareScores, processes) : [],
            gpuPlacementEnabled ? PlanGpu(hardware, hardwareScores, processes, runtimeGpuShimEnabled, gpuOverflow) : []);
    }

    private static IReadOnlyList<HostManagerAutomaticCpuPlacement> PlanCpu(
        CpuTopologySnapshot topology,
        CompiledHardwareScorePlan hardwareScores,
        IReadOnlyList<HostManagerAutomaticPlacementProcess> processes)
    {
        var result = new List<HostManagerAutomaticCpuPlacement>();
        var ccds = topology.Ccds
            .Select(ccd => new CpuCcdPlacementState(
                ccd,
                ResolveCcdCapacity(topology, hardwareScores, ccd)))
            .Where(static state => state.Capacity > 0)
            .OrderByDescending(static state => state.Capacity)
            .ThenBy(static state => state.Ccd.Index)
            .ThenBy(static state => state.Ccd.Id, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var candidates = processes
            .Where(static process => process.CanApplyPhysicalPlacement)
            .Where(static process => process.CanonicalCpuScore is >= 0)
            .OrderByDescending(static process => process.CanonicalCpuScore!.Value)
            .ThenBy(static process => process.TargetId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static process => process.ProcessId)
            .ToArray();

        foreach (var process in candidates)
        {
            var lockedCoreIds = process.Policy.CpuManualLockedPositionIds;
            if (lockedCoreIds.Count == 0)
            {
                continue;
            }

            var selector = CpuAllowedSetPlanBuilder.CreatePlan(topology, lockedCoreIds);
            if (!selector.Available)
            {
                continue;
            }

            result.Add(new HostManagerAutomaticCpuPlacement(
                process,
                process.CanonicalCpuScore!.Value,
                lockedCoreIds,
                selector,
                "manual-physical-core-lock"));
            AddManualLoad(topology, ccds, lockedCoreIds, process.CanonicalCpuScore.Value);
        }

        if (ccds.Length <= 1)
        {
            return result;
        }

        foreach (var process in candidates)
        {
            if (process.Policy.CpuManualLockedPositionIds.Count != 0
                || !process.Policy.CpuMaximumOccupancyMode.Equals(
                    CpuMaximumOccupancyModes.SingleCcd,
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var selected = ccds
                .OrderBy(static state => state.AssignedScore / state.Capacity)
                .ThenBy(static state => (double)state.AssignedCount / Math.Max(1, state.Ccd.PhysicalCoreIndexes.Count))
                .ThenByDescending(static state => state.Capacity)
                .ThenBy(static state => state.Ccd.Index)
                .First();
            var physicalCoreIds = selected.Ccd.PhysicalCoreIndexes
                .Select(index => topology.PhysicalCores.FirstOrDefault(core => core.Index == index)?.Id)
                .Where(static id => !string.IsNullOrWhiteSpace(id))
                .Select(static id => id!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var selector = CpuAllowedSetPlanBuilder.CreatePlan(topology, physicalCoreIds);
            if (!selector.Available)
            {
                continue;
            }

            result.Add(new HostManagerAutomaticCpuPlacement(
                process,
                process.CanonicalCpuScore!.Value,
                physicalCoreIds,
                selector,
                "automatic-single-ccd"));
            selected.AssignedScore += process.CanonicalCpuScore.Value;
            selected.AssignedCount++;
        }

        return result;
    }

    private static IReadOnlyList<HostManagerAutomaticGpuPlacement> PlanGpu(
        HardwareMetricSnapshot hardware,
        CompiledHardwareScorePlan hardwareScores,
        IReadOnlyList<HostManagerAutomaticPlacementProcess> processes,
        bool runtimeGpuShimEnabled,
        CompiledGpuOverflowPolicy? overflow)
    {
        if (!hardware.GpuInventory.IsCurrentComplete() || overflow is not { IsValid: true })
        {
            return [];
        }

        var adapters = hardware.GpuInventory.Adapters
            .Select(observation =>
            {
                var metrics = hardware.Gpus.FirstOrDefault(gpu => gpu.Index == observation.Index);
                return metrics is null
                    ? null
                    : new GpuPlacementState(
                        observation.AdapterKey,
                        GpuPerformanceScorePresetResolver.IsLikelyIntegratedGpuName(metrics.Name),
                        hardwareScores.ResolveGpuPerformanceScore(metrics),
                        observation);
            })
            .Where(static state => state is not null)
            .Select(static state => state!)
            .ToArray();
        if (adapters.Length < 2)
        {
            return [];
        }

        var candidates = processes
            .Where(static process => process.ExecutablePath is not null)
            .Where(static process => process.CanMigrateGpu)
            .Where(static process => process.CanonicalGpuScores.Count != 0)
            .Where(static process => AllowsPlacementWrite(process.Policy.EnabledMode))
            .Where(process => runtimeGpuShimEnabled && process.Policy.AcceptsExternalRuntimeGpuScheduling())
            .Select(static process => new GpuCandidate(
                process,
                process.CanonicalGpuScores.Values.Max()))
            .OrderByDescending(static candidate => candidate.CanonicalScore)
            .ThenBy(static candidate => candidate.Process.TargetId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static candidate => candidate.Process.ProcessId)
            .ToArray();
        if (candidates.Length == 0)
        {
            return [];
        }

        var integrated = adapters
            .Where(static adapter => adapter.IsIntegrated)
            .OrderByDescending(static adapter => adapter.PerformanceCapacity)
            .ThenBy(static adapter => adapter.AdapterKey)
            .ToArray();
        var highPerformance = adapters
            .Where(static adapter => !adapter.IsIntegrated)
            .OrderByDescending(static adapter => adapter.PerformanceCapacity)
            .ThenBy(static adapter => adapter.AdapterKey)
            .ToArray();
        var ordered = adapters.Where(a => double.IsFinite(a.PerformanceCapacity) && a.PerformanceCapacity > 0)
            .OrderByDescending(a => a.PerformanceCapacity).ThenBy(a => a.AdapterKey).ToArray();
        var selectedByProcess = new Dictionary<int, GpuPlacementState>();
        var observedByProcess = new Dictionary<int, ulong>();
        var automatic = new List<(GpuCandidate Candidate, GpuPlacementState Current)>();
        foreach (var candidate in candidates)
        {
            var target = GpuPlacementTargets.Normalize(candidate.Process.Policy.TargetGpu);
            var current = adapters.Where(a => candidate.Process.ObservedGpuUsagePercent.GetValueOrDefault(a.AdapterKey) > 0
                    || candidate.Process.ObservedDedicatedMemoryBytes.GetValueOrDefault(a.AdapterKey) > 0)
                .OrderByDescending(a => candidate.Process.ObservedGpuUsagePercent.GetValueOrDefault(a.AdapterKey))
                .ThenByDescending(a => candidate.Process.ObservedDedicatedMemoryBytes.GetValueOrDefault(a.AdapterKey))
                .FirstOrDefault();
            if (current is not null) observedByProcess[candidate.Process.ProcessId] = current.AdapterKey;
            if (target == GpuPlacementTargets.AutoIdleGpu)
            {
                if (current is null || !ordered.Contains(current)) continue;
                selectedByProcess[candidate.Process.ProcessId] = current;
                automatic.Add((candidate, current));
                continue;
            }
            var selected = target switch
            {
                GpuPlacementTargets.IntegratedGpu => integrated.FirstOrDefault(),
                GpuPlacementTargets.HighPerformanceGpu => highPerformance.FirstOrDefault(),
                _ => null
            };
            if (selected is not null) selectedByProcess[candidate.Process.ProcessId] = selected;
        }

        // Shared usage is not releasable byte-for-byte. Drain a source, then let the
        // next actual observation establish whether another migration is needed.
        var destinations = new HashSet<ulong>();
        foreach (var source in ordered.Where(a => a.IsOverflowing(overflow)))
        {
            foreach (var item in automatic.Where(x => x.Current == source && x.Candidate.Process.CanMigrateGpu
                    && source.ContributesToOverflow(x.Candidate.Process, overflow))
                .OrderBy(x => x.Candidate.CanonicalScore).ThenBy(x => x.Candidate.Process.ProcessId))
            {
                var destination = ordered.FirstOrDefault(a => a.PerformanceCapacity < source.PerformanceCapacity
                    && !destinations.Contains(a.AdapterKey) && a.CanReceive(item.Candidate.Process, source, overflow));
                if (destination is null) continue;
                selectedByProcess[item.Candidate.Process.ProcessId] = destination;
                destinations.Add(destination.AdapterKey);
                break;
            }
        }
        foreach (var item in automatic.Where(x => x.Candidate.Process.CanMigrateGpu && !x.Current.IsOverflowing(overflow)))
        {
            var destination = ordered.FirstOrDefault(a => a.PerformanceCapacity > item.Current.PerformanceCapacity
                && !destinations.Contains(a.AdapterKey) && a.CanReceive(item.Candidate.Process, item.Current, overflow));
            if (destination is null) continue;
            selectedByProcess[item.Candidate.Process.ProcessId] = destination;
            destinations.Add(destination.AdapterKey);
        }
        return candidates.Where(c => selectedByProcess.ContainsKey(c.Process.ProcessId)).Select(c =>
        {
            var selected = selectedByProcess[c.Process.ProcessId];
            return new HostManagerAutomaticGpuPlacement(c.Process, c.CanonicalScore, selected.IsIntegrated,
                selected.AdapterKey, c.Process.Policy.TargetGpu == GpuPlacementTargets.AutoIdleGpu
                    ? "automatic-gpu-performance-overflow" : "explicit-gpu-class")
            { ObservedAdapterKey = observedByProcess.GetValueOrDefault(c.Process.ProcessId) };
        }).ToArray();
    }

    private static double ResolveCcdCapacity(
        CpuTopologySnapshot topology,
        CompiledHardwareScorePlan hardwareScores,
        CpuCcdModel ccd)
    {
        var scores = ccd.PhysicalCoreIndexes
            .Select(index => topology.PhysicalCores.FirstOrDefault(core => core.Index == index))
            .Where(static core => core is not null)
            .Select(core => hardwareScores.ResolveCpuPerformanceScore(
                core!.Index,
                core.PerformanceScore))
            .ToArray();
        var sum = scores.Sum();
        return sum > 0 ? sum : scores.Length;
    }

    private static void AddManualLoad(
        CpuTopologySnapshot topology,
        IReadOnlyList<CpuCcdPlacementState> ccds,
        IReadOnlyList<string> physicalCoreIds,
        double score)
    {
        var selectedCcdIds = topology.PhysicalCores
            .Where(core => physicalCoreIds.Contains(core.Id, StringComparer.OrdinalIgnoreCase))
            .Select(static core => core.CcdId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (selectedCcdIds.Length == 0)
        {
            return;
        }
        foreach (var state in ccds.Where(state =>
            selectedCcdIds.Contains(state.Ccd.Id, StringComparer.OrdinalIgnoreCase)))
        {
            state.AssignedScore += score / selectedCcdIds.Length;
            state.AssignedCount++;
        }
    }

    private static bool AllowsPlacementWrite(string enabledMode)
        => enabledMode.Equals(GpuPlacementPolicyModes.Auto, StringComparison.OrdinalIgnoreCase)
            || enabledMode.Equals(GpuPlacementPolicyModes.Manual, StringComparison.OrdinalIgnoreCase);

    private static void ValidateCapacity(
        CpuTopologySnapshot? topology,
        IReadOnlyList<HostManagerAutomaticPlacementProcess> processes,
        CompiledHostManagerPlacementCoordinatorRecreatePlan capacity)
    {
        if (!capacity.IsPublished
            || (topology?.PhysicalCores.Count ?? 0) > capacity.CoreCapacity
            || (topology?.Ccds.Count ?? 0) > capacity.CcdCapacity
            || processes.Count > capacity.TargetCapacity)
        {
            throw new InvalidDataException(
                "Automatic placement inputs exceed the compiled placement capacity.");
        }
    }

    private sealed class CpuCcdPlacementState(CpuCcdModel ccd, double capacity)
    {
        internal CpuCcdModel Ccd { get; } = ccd;
        internal double Capacity { get; } = capacity;
        internal double AssignedScore { get; set; }
        internal int AssignedCount { get; set; }
    }

    private sealed class GpuPlacementState(
        ulong adapterKey,
        bool isIntegrated,
        double performanceCapacity,
        SchedulingGpuAdapterObservation observation)
    {
        internal ulong AdapterKey { get; } = adapterKey;
        internal bool IsIntegrated { get; } = isIntegrated;
        internal double PerformanceCapacity { get; } = performanceCapacity;
        internal bool IsOverflowing(CompiledGpuOverflowPolicy policy)
            => observation.UsageStatus == SamplingObservationStatus.Current && observation.UsagePercent >= policy.UsagePercent
                || observation.CapacityStatus == SamplingObservationStatus.Current && observation.TotalDedicatedMemoryBytes > 0
                    && observation.UsedDedicatedMemoryBytes * 100d / observation.TotalDedicatedMemoryBytes >= policy.DedicatedMemoryPercent;

        internal bool ContributesToOverflow(HostManagerAutomaticPlacementProcess process, CompiledGpuOverflowPolicy policy)
            => observation.UsageStatus == SamplingObservationStatus.Current && observation.UsagePercent >= policy.UsagePercent
                && process.ObservedGpuUsagePercent.GetValueOrDefault(AdapterKey) > 0
                || observation.CapacityStatus == SamplingObservationStatus.Current && observation.TotalDedicatedMemoryBytes > 0
                    && observation.UsedDedicatedMemoryBytes * 100d / observation.TotalDedicatedMemoryBytes >= policy.DedicatedMemoryPercent
                    && process.ObservedDedicatedMemoryBytes.GetValueOrDefault(AdapterKey) > 0;

        internal bool CanReceive(HostManagerAutomaticPlacementProcess process, GpuPlacementState source,
            CompiledGpuOverflowPolicy policy)
        {
            if (observation.UsageStatus != SamplingObservationStatus.Current || IsOverflowing(policy)
                || !process.ObservedGpuUsagePercent.TryGetValue(source.AdapterKey, out var usage)
                || observation.UsagePercent + usage * source.PerformanceCapacity / PerformanceCapacity >= policy.UsagePercent)
                return false;
            if (!observation.CapabilityMask.HasFlag(SchedulingGpuCapabilityMask.DedicatedMemory))
                return IsIntegrated;
            // Dedicated bytes on an iGPU do not describe its system-memory backing demand.
            if (source.IsIntegrated && !IsIntegrated) return false;
            return observation.CapacityStatus == SamplingObservationStatus.Current && observation.TotalDedicatedMemoryBytes > 0
                && process.ObservedDedicatedMemoryBytes.TryGetValue(source.AdapterKey, out var bytes)
                && (observation.UsedDedicatedMemoryBytes + bytes) * 100d / observation.TotalDedicatedMemoryBytes < policy.DedicatedMemoryPercent;
        }
    }

    private sealed record GpuCandidate(
        HostManagerAutomaticPlacementProcess Process,
        double CanonicalScore);
}
