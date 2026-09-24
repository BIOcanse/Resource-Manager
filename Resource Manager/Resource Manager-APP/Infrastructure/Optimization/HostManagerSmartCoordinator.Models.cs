using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using ResourceManager.Adapter;
using ResourceManager.App.Application.Adaptation;
using ResourceManager.App.Application.GpuPlacement;
using ResourceManager.App.Application.Metrics;
using ResourceManager.App.Application.Optimization;
using ResourceManager.App.Application.Optimization.Scoring;
using ResourceManager.App.Application.Optimization.Scheduling;
using ResourceManager.App.Application.ResourceBreakdown;
using ResourceManager.App.Application.Settings;
using ResourceManager.App.Domain.Adaptation;
using ResourceManager.App.Domain.Adaptation.Scheduling;
using ResourceManager.App.Domain.GpuPlacement;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.Optimization;
using ResourceManager.App.Domain.Optimization.Scoring;
using ResourceManager.App.Domain.ProcessAttribution;
using ResourceManager.App.Domain.ResourceBreakdown;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Domain.Settings;
using ResourceManager.App.Domain.Software;
using ResourceManager.App.Infrastructure.Windows;

namespace ResourceManager.App.Infrastructure.Optimization;

public sealed partial class HostManagerSmartCoordinator
{
    private sealed class MutableHostManagerTargetInfo(
        string targetId,
        string softwareId,
        string displayName,
        string softwareKind,
        string displayKind,
        double baseScore,
        ResolvedGpuPlacementPolicy gpuPlacementPolicy,
        bool canApplyAutomaticPolicy,
        bool canApplyAdaptedPolicy,
        bool canApplyPhysicalCorePlacement,
        CompiledAdapterDispatchRoute adapterDispatchRoute,
        IReadOnlyList<AdapterCpuSchedulingGrade> supportedCpuGrades,
        IReadOnlyList<AdapterGpuSchedulingGrade> supportedGpuGrades)
    {
        private readonly HashSet<string> processNames = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> executablePaths = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<int> processIds = [];
        private readonly Dictionary<int, ulong> processStartKeys = [];
        private readonly Dictionary<ulong, MutableHostManagerGpuTargetInfo> gpus = new();
        private string resourceKind = OptimizationResourceKinds.Cpu;
        private double evidenceValue;
        private double evidenceSystemPercent;

        public bool HasAnyUsage { get; private set; }
        public string RuntimeState { get; private set; } = HostManagerRuntimeStates.BackgroundProcess;
        public double CpuUsagePercent { get; private set; }
        public double GpuUsagePercent { get; private set; }
        public double? VramUsedPercent { get; private set; }
        public bool HasCpuMetric { get; private set; }
        public bool GpuIdentityConsistent { get; private set; } = true;
        public bool ForegroundFocused { get; private set; }
        public bool HasVisibleWindow { get; private set; }
        public bool HasBackgroundWindow { get; private set; }
        public bool HasHiddenWindow { get; private set; }
        public bool CanApplyAutomaticPolicy { get; } = canApplyAutomaticPolicy;
        public bool CanApplyAdaptedPolicy { get; } = canApplyAdaptedPolicy;
        public bool CanApplyPhysicalCorePlacement { get; } = canApplyPhysicalCorePlacement;
        public string SoftwareKind { get; } = softwareKind;
        public CompiledAdapterDispatchRoute AdapterDispatchRoute { get; } = adapterDispatchRoute;
        public IReadOnlyList<AdapterCpuSchedulingGrade> SupportedCpuGrades { get; } = supportedCpuGrades;
        public IReadOnlyList<AdapterGpuSchedulingGrade> SupportedGpuGrades { get; } = supportedGpuGrades;
        public ResolvedGpuPlacementPolicy GpuPlacementPolicy { get; } = gpuPlacementPolicy;

        public void AddFact(SchedulingProcessFact process)
        {
            HasAnyUsage = true;

            if (process.ProcessId > 0)
            {
                processIds.Add(process.ProcessId);
                processStartKeys.Add(process.ProcessId, process.ProcessStartKey);
                SetRuntimeState(process.RuntimeState);
                ForegroundFocused |= process.ForegroundFocused;
                HasVisibleWindow |= process.HasVisibleWindow;
                HasBackgroundWindow |= process.HasBackgroundWindow;
                HasHiddenWindow |= process.HasHiddenWindow;
            }

            if (!string.IsNullOrWhiteSpace(process.ExecutablePath))
            {
                executablePaths.Add(process.ExecutablePath);
            }

            if (!string.IsNullOrWhiteSpace(process.ProcessName))
            {
                processNames.Add(process.ProcessName);
            }

            if (process.ValidMetricMask.HasFlag(SchedulingProcessMetricMask.CpuUsage))
            {
                HasCpuMetric = true;
                CpuUsagePercent = process.CpuUsagePercent;
                SelectEvidence(OptimizationResourceKinds.Cpu, process.CpuUsagePercent);
            }
            foreach (var gpuFact in process.Gpus)
            {
                if (gpuFact.GpuIndex < 0
                    || gpuFact.AdapterKey == 0
                    || gpuFact.ValidMetricMask.HasFlag(
                            SchedulingProcessMetricMask.GpuUsage)
                        && (gpuFact.UsageSourceGeneration == 0
                            || gpuFact.UsageTopologyGeneration == 0)
                    || gpuFact.HasMetric(
                            SchedulingProcessMetricMask.GpuDedicatedMemory)
                        && (gpuFact.DedicatedMemorySourceGeneration == 0
                            || gpuFact.DedicatedMemoryTopologyGeneration == 0))
                {
                    GpuIdentityConsistent = false;
                    continue;
                }

                if (!gpus.TryGetValue(gpuFact.AdapterKey, out var gpu))
                {
                    gpu = new MutableHostManagerGpuTargetInfo(gpuFact);
                    gpus.Add(gpuFact.AdapterKey, gpu);
                }
                else if (!gpu.Matches(gpuFact))
                {
                    GpuIdentityConsistent = false;
                    continue;
                }

                gpu.ResidentMemoryBytes = gpuFact.ResidentMemoryBytes;

                if (gpuFact.ValidMetricMask.HasFlag(SchedulingProcessMetricMask.GpuUsage))
                {
                    gpu.AddUsage(gpuFact);
                    GpuUsagePercent = Math.Max(GpuUsagePercent, gpuFact.UsagePercent);
                    SelectEvidence($"{OptimizationResourceKinds.Gpu}{gpuFact.GpuIndex}", gpuFact.UsagePercent);
                }
                if (gpuFact.ValidMetricMask.HasFlag(SchedulingProcessMetricMask.GpuDedicatedMemory))
                {
                    gpu.AddVram(gpuFact);
                    VramUsedPercent = Math.Max(VramUsedPercent ?? 0, gpuFact.DedicatedMemoryUsedPercent);
                    SelectEvidence($"{OptimizationResourceKinds.Vram}{gpuFact.GpuIndex}", gpuFact.DedicatedMemoryUsedPercent);
                }
            }
        }

        private void SelectEvidence(string kind, double percent)
        {
            if (percent <= evidenceSystemPercent)
            {
                return;
            }

            evidenceSystemPercent = percent;
            evidenceValue = percent;
            resourceKind = kind;
        }

        public HostManagerTargetInfo ToImmutable()
        {
            return new HostManagerTargetInfo(
                targetId,
                softwareId,
                displayName,
                SoftwareKind,
                displayKind,
                baseScore,
                GpuPlacementPolicy,
                CanApplyAutomaticPolicy,
                CanApplyAdaptedPolicy,
                CanApplyPhysicalCorePlacement,
                AdapterDispatchRoute,
                SupportedCpuGrades,
                SupportedGpuGrades,
                RuntimeState,
                ForegroundFocused,
                HasVisibleWindow,
                HasBackgroundWindow,
                HasHiddenWindow,
                HasCpuMetric,
                CpuUsagePercent,
                GpuUsagePercent,
                VramUsedPercent,
                resourceKind,
                evidenceValue,
                GpuIdentityConsistent,
                gpus.Values
                    .OrderBy(static gpu => gpu.AdapterKey)
                    .ThenBy(static gpu => gpu.GpuIndex)
                    .Select(static gpu => gpu.ToImmutable())
                    .ToArray(),
                processIds.Order().ToArray(),
                new Dictionary<int, ulong>(processStartKeys),
                processNames.Order(StringComparer.OrdinalIgnoreCase).ToArray(),
                executablePaths.Order(StringComparer.OrdinalIgnoreCase).ToArray());
        }

        private void SetRuntimeState(string runtimeState)
        {
            if (RuntimeStateRank(runtimeState) > RuntimeStateRank(RuntimeState))
            {
                RuntimeState = runtimeState;
            }
        }

        private static int RuntimeStateRank(string runtimeState)
            => runtimeState switch
            {
                HostManagerRuntimeStates.ForegroundFocused => 5,
                HostManagerRuntimeStates.ForegroundUnfocused => 4,
                HostManagerRuntimeStates.BackgroundWindow => 3,
                HostManagerRuntimeStates.TrayOnly => 2,
                HostManagerRuntimeStates.BackgroundProcess => 1,
                _ => 0
            };
    }

    private sealed class MutableHostManagerGpuTargetInfo(SchedulingProcessGpuFact fact)
    {
        public int GpuIndex { get; } = fact.GpuIndex;
        public ulong AdapterKey { get; } = fact.AdapterKey;
        public ulong UsageSourceGeneration { get; private set; } =
            fact.ValidMetricMask.HasFlag(SchedulingProcessMetricMask.GpuUsage)
                ? fact.UsageSourceGeneration
                : 0;
        public ulong DedicatedMemorySourceGeneration { get; private set; } =
            fact.HasMetric(
                SchedulingProcessMetricMask.GpuDedicatedMemory)
                ? fact.DedicatedMemorySourceGeneration
                : 0;
        public ulong UsageTopologyGeneration { get; private set; } =
            fact.ValidMetricMask.HasFlag(SchedulingProcessMetricMask.GpuUsage)
                ? fact.UsageTopologyGeneration
                : 0;
        public ulong DedicatedMemoryTopologyGeneration { get; private set; } =
            fact.HasMetric(
                SchedulingProcessMetricMask.GpuDedicatedMemory)
                ? fact.DedicatedMemoryTopologyGeneration
                : 0;
        public double GpuUsagePercent { get; private set; }
        public double VramUsedPercent { get; private set; }
        public bool HasUsageMetric { get; private set; }
        public bool HasVramMetric { get; private set; }
        public double? ResidentMemoryBytes { get; set; }

        public bool Matches(SchedulingProcessGpuFact candidate)
            => candidate.GpuIndex == GpuIndex
                && candidate.AdapterKey == AdapterKey
                && (!candidate.ValidMetricMask.HasFlag(
                        SchedulingProcessMetricMask.GpuUsage)
                    || UsageSourceGeneration == 0
                    || candidate.UsageSourceGeneration == UsageSourceGeneration
                        && candidate.UsageTopologyGeneration ==
                            UsageTopologyGeneration)
                && (!candidate.HasMetric(
                        SchedulingProcessMetricMask.GpuDedicatedMemory)
                    || DedicatedMemorySourceGeneration == 0
                    || candidate.DedicatedMemorySourceGeneration ==
                            DedicatedMemorySourceGeneration
                        && candidate.DedicatedMemoryTopologyGeneration ==
                            DedicatedMemoryTopologyGeneration);

        public void AddUsage(SchedulingProcessGpuFact candidate)
        {
            HasUsageMetric = true;
            UsageSourceGeneration = candidate.UsageSourceGeneration;
            UsageTopologyGeneration = candidate.UsageTopologyGeneration;
            GpuUsagePercent = Math.Max(
                GpuUsagePercent,
                candidate.UsagePercent);
        }

        public void AddVram(SchedulingProcessGpuFact candidate)
        {
            HasVramMetric = true;
            DedicatedMemorySourceGeneration =
                candidate.DedicatedMemorySourceGeneration;
            DedicatedMemoryTopologyGeneration =
                candidate.DedicatedMemoryTopologyGeneration;
            VramUsedPercent = Math.Max(
                VramUsedPercent,
                candidate.DedicatedMemoryUsedPercent);
        }

        public HostManagerGpuTargetInfo ToImmutable()
        {
            return new HostManagerGpuTargetInfo(
                GpuIndex,
                AdapterKey,
                UsageSourceGeneration,
                DedicatedMemorySourceGeneration,
                UsageTopologyGeneration,
                DedicatedMemoryTopologyGeneration,
                GpuUsagePercent,
                VramUsedPercent,
                HasUsageMetric,
                HasVramMetric,
                ResidentMemoryBytes);
        }
    }

    private sealed record HostManagerTargetInfo(
        string TargetId,
        string SoftwareId,
        string DisplayName,
        string SoftwareKind,
        string DisplayKind,
        double BaseScore,
        ResolvedGpuPlacementPolicy GpuPlacementPolicy,
        bool CanApplyAutomaticPolicy,
        bool CanApplyAdaptedPolicy,
        bool CanApplyPhysicalCorePlacement,
        CompiledAdapterDispatchRoute AdapterDispatchRoute,
        IReadOnlyList<AdapterCpuSchedulingGrade> SupportedCpuGrades,
        IReadOnlyList<AdapterGpuSchedulingGrade> SupportedGpuGrades,
        string RuntimeState,
        bool ForegroundFocused,
        bool HasVisibleWindow,
        bool HasBackgroundWindow,
        bool HasHiddenWindow,
        bool HasCpuMetric,
        double CpuUsagePercent,
        double GpuUsagePercent,
        double? VramUsedPercent,
        string ResourceKind,
        double EvidenceValue,
        bool GpuIdentityConsistent,
        IReadOnlyList<HostManagerGpuTargetInfo> Gpus,
        IReadOnlyList<int> ProcessIds,
        IReadOnlyDictionary<int, ulong> ProcessStartKeys,
        IReadOnlyList<string> ProcessNames,
        IReadOnlyList<string> ExecutablePaths);

    private sealed record HostManagerGpuTargetInfo(
        int GpuIndex,
        ulong AdapterKey,
        ulong UsageSourceGeneration,
        ulong DedicatedMemorySourceGeneration,
        ulong UsageTopologyGeneration,
        ulong DedicatedMemoryTopologyGeneration,
        double GpuUsagePercent,
        double VramUsedPercent,
        bool HasUsageMetric,
        bool HasVramMetric,
        double? ResidentMemoryBytes);

    private sealed record HostManagerSample(
        HardwareMetricSnapshot Hardware,
        HostManagerIdleCapacitySnapshot IdleCapacity,
        SchedulingProcessFactSnapshot ProcessFacts,
        IReadOnlyList<HostManagerTargetInfo> Targets,
        ResourceManager.App.Domain.CpuTopology.CpuCoreResidencySnapshot? CpuResidency);
}
