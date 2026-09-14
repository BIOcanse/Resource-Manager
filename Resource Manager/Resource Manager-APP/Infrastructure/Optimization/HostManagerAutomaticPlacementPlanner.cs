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
    IReadOnlyDictionary<ulong, double> ObservedGpuUsagePercent);

internal sealed record HostManagerAutomaticCpuPlacement(
    HostManagerAutomaticPlacementProcess Process,
    double CanonicalProcessScore,
    IReadOnlyList<string> PhysicalCoreIds,
    CpuAffinityPlan Selector,
    string Source);

internal sealed record HostManagerAutomaticGpuPreferencePlacement(
    HostManagerAutomaticPlacementProcess Process,
    double CanonicalProcessScore,
    bool PreferIntegratedGpu,
    ulong TargetAdapterKey,
    string Source);

internal sealed record HostManagerAutomaticPlacementPlan(
    IReadOnlyList<HostManagerAutomaticCpuPlacement> Cpu,
    IReadOnlyList<HostManagerAutomaticGpuPreferencePlacement> GpuPreferences);

internal static class HostManagerAutomaticPlacementPlanner
{
    internal static HostManagerAutomaticPlacementPlan Plan(
        CpuTopologySnapshot topology,
        HardwareMetricSnapshot hardware,
        CompiledHardwareScorePlan hardwareScores,
        IReadOnlyList<HostManagerAutomaticPlacementProcess> processes,
        CompiledHostManagerPlacementCoordinatorRecreatePlan capacity,
        bool runtimeGpuShimEnabled = false)
    {
        ArgumentNullException.ThrowIfNull(topology);
        ArgumentNullException.ThrowIfNull(hardware);
        ArgumentNullException.ThrowIfNull(hardwareScores);
        ArgumentNullException.ThrowIfNull(processes);
        ArgumentNullException.ThrowIfNull(capacity);
        ValidateCapacity(topology, processes, capacity);

        return new HostManagerAutomaticPlacementPlan(
            PlanCpu(topology, hardwareScores, processes),
            PlanGpuPreferences(hardware, hardwareScores, processes, runtimeGpuShimEnabled));
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
            .Where(static process => AllowsPlacementWrite(process.Policy.EnabledMode))
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
                || !process.Policy.EnabledMode.Equals(
                    GpuPlacementPolicyModes.Auto,
                    StringComparison.OrdinalIgnoreCase)
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

    private static IReadOnlyList<HostManagerAutomaticGpuPreferencePlacement> PlanGpuPreferences(
        HardwareMetricSnapshot hardware,
        CompiledHardwareScorePlan hardwareScores,
        IReadOnlyList<HostManagerAutomaticPlacementProcess> processes,
        bool runtimeGpuShimEnabled)
    {
        if (!hardware.GpuInventory.IsCurrentComplete())
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
                        Math.Max(1, hardwareScores.ResolveGpuPerformanceScore(metrics)),
                        observation.UsageStatus == SamplingObservationStatus.Current
                            ? observation.UsagePercent
                                * Math.Max(1, hardwareScores.ResolveGpuPerformanceScore(metrics))
                                / 100d
                            : 0);
            })
            .Where(static state => state is not null)
            .Select(static state => state!)
            .ToArray();
        if (adapters.Length == 0)
        {
            return [];
        }

        var candidates = processes
            .Where(static process => process.CanApplyPhysicalPlacement)
            .Where(static process => process.ExecutablePath is not null)
            .Where(static process => process.CanonicalGpuScores.Count != 0)
            .Where(static process => AllowsPlacementWrite(process.Policy.EnabledMode))
            .Where(process => process.Policy.AllowsProvider(GpuPlacementProviderIds.WindowsGraphicsPreference)
                || runtimeGpuShimEnabled && process.Policy.AcceptsRuntimeGpuScheduling())
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

        foreach (var adapter in adapters)
        {
            var candidateUsage = candidates.Sum(candidate =>
                candidate.Process.ObservedGpuUsagePercent.GetValueOrDefault(adapter.AdapterKey));
            adapter.ProjectedDemand = Math.Max(
                0,
                adapter.ProjectedDemand
                    - candidateUsage * adapter.PerformanceCapacity / 100d);
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
        var result = new List<HostManagerAutomaticGpuPreferencePlacement>();

        foreach (var candidate in candidates)
        {
            var target = GpuPlacementTargets.Normalize(candidate.Process.Policy.TargetGpu);
            bool? preferIntegrated = target switch
            {
                GpuPlacementTargets.IntegratedGpu when integrated.Length != 0 => true,
                GpuPlacementTargets.HighPerformanceGpu when highPerformance.Length != 0 => false,
                GpuPlacementTargets.AutoIdleGpu when integrated.Length != 0 && highPerformance.Length != 0 =>
                    SelectAutomaticGpuClass(
                        integrated,
                        highPerformance,
                        candidate.CanonicalScore),
                _ => null
            };
            if (!preferIntegrated.HasValue)
            {
                continue;
            }

            var selectedClass = preferIntegrated.Value ? integrated : highPerformance;
            var selected = selectedClass
                .OrderBy(adapter =>
                    (adapter.ProjectedDemand + candidate.CanonicalScore)
                    / adapter.PerformanceCapacity)
                .ThenByDescending(static adapter => adapter.PerformanceCapacity)
                .ThenBy(static adapter => adapter.AdapterKey)
                .First();
            selected.ProjectedDemand += candidate.CanonicalScore;
            selected.AssignedCount++;
            result.Add(new HostManagerAutomaticGpuPreferencePlacement(
                candidate.Process,
                candidate.CanonicalScore,
                preferIntegrated.Value,
                selected.AdapterKey,
                target.Equals(GpuPlacementTargets.AutoIdleGpu, StringComparison.OrdinalIgnoreCase)
                    ? "automatic-gpu-class"
                    : "explicit-gpu-class"));
        }

        return result;
    }

    private static bool SelectAutomaticGpuClass(
        IReadOnlyList<GpuPlacementState> integrated,
        IReadOnlyList<GpuPlacementState> highPerformance,
        double canonicalScore)
    {
        var integratedBest = integrated
            .OrderBy(static adapter => adapter.ProjectedDemand / adapter.PerformanceCapacity)
            .ThenByDescending(static adapter => adapter.PerformanceCapacity)
            .First();
        var highBest = highPerformance
            .OrderBy(static adapter => adapter.ProjectedDemand / adapter.PerformanceCapacity)
            .ThenByDescending(static adapter => adapter.PerformanceCapacity)
            .First();
        var integratedLoad = (integratedBest.ProjectedDemand + canonicalScore)
            / integratedBest.PerformanceCapacity;
        var highLoad = (highBest.ProjectedDemand + canonicalScore)
            / highBest.PerformanceCapacity;
        if (Math.Abs(integratedLoad - highLoad) < 0.000001)
        {
            return false;
        }
        return integratedLoad < highLoad;
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
        CpuTopologySnapshot topology,
        IReadOnlyList<HostManagerAutomaticPlacementProcess> processes,
        CompiledHostManagerPlacementCoordinatorRecreatePlan capacity)
    {
        if (!capacity.IsPublished
            || topology.PhysicalCores.Count > capacity.CoreCapacity
            || topology.Ccds.Count > capacity.CcdCapacity
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
        double projectedDemand)
    {
        internal ulong AdapterKey { get; } = adapterKey;
        internal bool IsIntegrated { get; } = isIntegrated;
        internal double PerformanceCapacity { get; } = performanceCapacity;
        internal double ProjectedDemand { get; set; } = projectedDemand;
        internal int AssignedCount { get; set; }
    }

    private sealed record GpuCandidate(
        HostManagerAutomaticPlacementProcess Process,
        double CanonicalScore);
}
