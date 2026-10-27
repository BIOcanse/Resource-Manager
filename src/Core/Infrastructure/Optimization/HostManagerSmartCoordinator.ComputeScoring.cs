using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.Monitoring;
using ResourceManager.App.Domain.Optimization;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Domain.ResourceBreakdown;
using ResourceManager.App.Infrastructure.NativeCore;

namespace ResourceManager.App.Infrastructure.Optimization;

public sealed partial class HostManagerSmartCoordinator
{
    private const double NeutralComputePolicyMultiplier = 1;

    internal HostManagerSchedulingAuthoritySnapshot SchedulingAuthority
        => schedulingAuthorityOwner.Capture();

    private HostManagerComputeScoringCycleResult? RunSchedulingAuthority(
        CompiledHostManagerSmartCoordinatorPlan plan,
        CompiledCpuScoringPlan cpuScoring,
        CompiledOptimizationModePlan mode,
        HostManagerSchedulingPlanBinding planBinding,
        HostManagerSample sample,
        IReadOnlyDictionary<string, int> protectionLevels)
    {
        var capturedRound = sample.RoundNumber;
        HostManagerComputeScoringCycleResult? result;
        try
        {
            EnsureComputeScoringWorkspace(plan, cpuScoring);
            var workspace = computeScoringWorkspace
                ?? throw new InvalidOperationException(
                    "The compute scoring authority workspace was not created.");
            var runtimeFacts = CreateComputeRuntimeFacts(sample.Targets, cycleFactExclusions);
            var welfareCapacity = CreateWelfareCapacityInput(
                sample.Hardware,
                runtimeFacts);
            result = workspace.Score(
                capturedRound,
                sample.ProcessFacts,
                sample.Hardware.GpuInventory,
                runtimeFacts,
                sample.CpuResidency,
                welfareCapacity,
                scoreCpu: mode.MemorySchedulingEnabled || mode.CpuSchedulingEnabled,
                scoreGpu: mode.GpuSchedulingEnabled,
                exclusions: cycleFactExclusions);
        }
        catch (Exception exception)
        {
            PublishSchedulingAuthorityUnavailable(
                capturedRound,
                planBinding,
                sample.Hardware.CapturedAt,
                $"compute-scoring-{exception.GetType().Name}:{exception.Message}",
                exception);
            return null;
        }

        if (result is null)
        {
            PublishSchedulingAuthorityUnavailable(
                capturedRound,
                planBinding,
                sample.Hardware.CapturedAt,
                "compute-sample-incomplete");
            return null;
        }

        if (!mode.MemorySchedulingEnabled)
        {
            ClearNonAdaptedMemoryModeProjection();
            schedulingAuthorityOwner.PublishComputeOnly(
                result,
                planBinding,
                sample.Hardware.CapturedAt,
                "memory-domain-disabled");
            RecoverSchedulingAuthorityLogging();
            return result;
        }

        if (result.Cpu is null)
        {
            PublishSchedulingAuthorityComputeOnly(
                result,
                planBinding,
                sample.Hardware.CapturedAt,
                "cpu-compute-domain-incomplete");
            return result;
        }

        HostManagerMemoryModePolicyCapture policyCapture;
        try
        {
            policyCapture = memoryModePolicyAuthorityOwner.Capture(planBinding, plan);
        }
        catch (Exception exception)
        {
            PublishSchedulingAuthorityComputeOnly(
                result,
                planBinding,
                sample.Hardware.CapturedAt,
                $"memory-mode-policy-{exception.GetType().Name}:{exception.Message}",
                exception);
            return result;
        }

        if (policyCapture.Policy is null)
        {
            PublishSchedulingAuthorityComputeOnly(
                result,
                planBinding,
                sample.Hardware.CapturedAt,
                policyCapture.UnavailableReason ?? "memory-mode-policy-unavailable");
            return result;
        }

        var policy = policyCapture.Policy;
        try
        {
            ValidateMemoryModePolicy(planBinding, plan, policy);
            if (!sample.Hardware.TryGetCurrentDataset(
                    SamplingDatasetIds.SystemMemoryUsage,
                    out _)
                || sample.Hardware.Memory.ObservationStatus
                    != SamplingObservationStatus.Current
                || !sample.Hardware.Memory.IsUsageAvailable
                || sample.Hardware.Memory.TotalBytes == 0
                || sample.Hardware.Memory.UsedBytes
                    > sample.Hardware.Memory.TotalBytes
                || !IsPercent(sample.Hardware.Memory.UsagePercent))
            {
                PublishSchedulingAuthorityComputeOnly(
                    result,
                    planBinding,
                    sample.Hardware.CapturedAt,
                    "memory-mode-ram-source-incomplete");
                return result;
            }

            var memoryModeConfiguration = policy.Configuration;
            EnsureMemoryModeWorkspace(in memoryModeConfiguration);
            var workspace = memoryModeWorkspace
                ?? throw new InvalidOperationException(
                    "The memory-mode authority workspace was not created.");
            var desired = workspace.Plan(
                result,
                sample.ProcessFacts,
                ComputeMemoryFreeRatioUnits(
                    sample.Hardware.Memory.UsedBytes,
                    sample.Hardware.Memory.TotalBytes,
                    policy.Configuration.RatioUnitsMaximum),
                policy.AllowUnrestricted);
            var nonAdaptedProjection = ProjectNonAdaptedMemoryModes(
                result,
                desired,
                sample,
                policy,
                protectionLevels);
            schedulingAuthorityOwner.PublishReady(
                result,
                desired,
                sample.Hardware.CapturedAt,
                planBinding,
                policy.PolicyEvidence);
            PublishNonAdaptedMemoryModeProjection(nonAdaptedProjection);
            RecoverSchedulingAuthorityLogging();
            return result;
        }
        catch (Exception exception)
        {
            PublishSchedulingAuthorityComputeOnly(
                result,
                planBinding,
                sample.Hardware.CapturedAt,
                $"memory-mode-plan-{exception.GetType().Name}:{exception.Message}",
                exception);
            return result;
        }
    }

    private void EnsureMemoryModeWorkspace(
        in NativeMemoryModeConfiguration configuration)
    {
        if (memoryModeWorkspace?.MatchesConfiguration(in configuration) == true)
        {
            return;
        }

        var replacement = new HostManagerMemoryModeWorkspace(in configuration);
        memoryModeWorkspace?.Dispose();
        memoryModeWorkspace = replacement;
    }

    private static void ValidateMemoryModePolicy(
        HostManagerSchedulingPlanBinding planBinding,
        CompiledHostManagerSmartCoordinatorPlan plan,
        HostManagerMemoryModePolicy policy)
    {
        var configured = plan.HotPublish.MemoryModePolicy;
        if (policy.PlanBinding != planBinding
            || !string.Equals(
                policy.SourceKind,
                configured.SourceKind,
                StringComparison.Ordinal)
            || policy.Configuration.Generation != configured.ConfigurationGeneration
            || policy.Configuration.MaximumSoftwareCount
                != checked((uint)plan.Recreate.MaximumSoftwareGroups)
            || policy.Configuration.RatioUnitsMaximum
                != configured.RatioUnitsMaximum
            || policy.Configuration.UnrestrictedMinimumFreeRatioUnits
                != configured.UnrestrictedMinimumFreeRatioUnits
            || policy.Configuration.NormalMinimumFreeRatioUnits
                != configured.NormalMinimumFreeRatioUnits
            || policy.Configuration.StrongBeginFreeRatioUnits
                != configured.StrongBeginFreeRatioUnits
            || policy.Configuration.MiddleTierMinimumBaseScore
                != plan.HotPublish.BaseScoreTiers.MiddleMinimumBaseScore
            || policy.Configuration.HighTierMinimumBaseScore
                != plan.HotPublish.BaseScoreTiers.HighMinimumBaseScore
            || policy.OptimizeMemoryPriority != configured.OptimizeMemoryPriority
            || policy.PagedFrozenMemoryPriority != configured.PagedFrozenMemoryPriority
            || !string.Equals(
                policy.ForeignMemoryPriorityDisposition,
                configured.ForeignMemoryPriorityDisposition,
                StringComparison.Ordinal)
            || policy.OwnedStateVerificationIntervalCycles
                != configured.OwnedStateVerificationIntervalCycles
            || !policy.PolicyEvidence.IsBoundTo(
                planBinding,
                policy.AllowUnrestricted))
        {
            throw new InvalidDataException(
                "The memory-mode policy is not bound to the current Host configuration and policy evidence.");
        }
    }

    private static uint ComputeMemoryFreeRatioUnits(
        ulong usedBytes,
        ulong totalBytes,
        uint ratioUnitsMaximum)
    {
        if (totalBytes == 0 || ratioUnitsMaximum == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(totalBytes));
        }
        if (usedBytes > totalBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(usedBytes));
        }
        var freeBytes = totalBytes - usedBytes;
        return checked((uint)(((UInt128)freeBytes * ratioUnitsMaximum) / totalBytes));
    }

    private void PublishSchedulingAuthorityUnavailable(
        ulong capturedRound,
        HostManagerSchedulingPlanBinding planBinding,
        DateTimeOffset observedAt,
        string reason,
        Exception? exception = null)
    {
        ClearNonAdaptedMemoryModeProjection();
        schedulingAuthorityOwner.PublishUnavailable(
            capturedRound,
            planBinding,
            observedAt,
            BoundAuthorityReason(reason));
        LogSchedulingAuthorityFailure(reason, exception);
    }

    private void PublishSchedulingAuthorityComputeOnly(
        HostManagerComputeScoringCycleResult result,
        HostManagerSchedulingPlanBinding planBinding,
        DateTimeOffset observedAt,
        string reason,
        Exception? exception = null)
    {
        ClearNonAdaptedMemoryModeProjection();
        schedulingAuthorityOwner.PublishComputeOnly(
            result,
            planBinding,
            observedAt,
            BoundAuthorityReason(reason));
        LogSchedulingAuthorityFailure(reason, exception);
    }

    private void LogSchedulingAuthorityFailure(
        string reason,
        Exception? exception)
    {
        if (string.Equals(reason, lastSchedulingAuthorityFailure, StringComparison.Ordinal))
        {
            logger.LogDebug(
                "Host Manager scheduling authority remains non-ready: {Reason}",
                reason);
            return;
        }

        if (exception is null)
        {
            logger.LogInformation(
                "Host Manager scheduling authority is non-ready: {Reason}",
                reason);
        }
        else
        {
            logger.LogError(
                exception,
                "Host Manager scheduling authority failed closed: {Reason}",
                reason);
        }
        lastSchedulingAuthorityFailure = reason;
    }

    private void RecoverSchedulingAuthorityLogging()
    {
        if (lastSchedulingAuthorityFailure is null)
        {
            return;
        }
        logger.LogInformation(
            "Host Manager scheduling authority recovered after {Failure}.",
            lastSchedulingAuthorityFailure);
        lastSchedulingAuthorityFailure = null;
    }

    private static string BoundAuthorityReason(string reason)
        => reason.Length <= 512 ? reason : reason[..512];

    private void EnsureComputeScoringWorkspace(
        CompiledHostManagerSmartCoordinatorPlan plan,
        CompiledCpuScoringPlan cpuScoring)
    {
        if (computeScoringWorkspace?.ConfigurationGeneration == plan.ConfigurationGeneration
            && ReferenceEquals(computeScoringWorkspace.CpuScoring, cpuScoring))
        {
            return;
        }

        var configuration = HostManagerComputeScoringConfigurationFactory.Create(plan, cpuScoring);
        var replacement = new HostManagerComputeScoringWorkspace(in configuration, cpuScoring, selfLocalResourceManagerOwner.CreateScratch);
        try { computeScoringWorkspace?.Dispose(); }
        catch (Exception original)
        {
            try { replacement.Dispose(); }
            catch (Exception cleanup) { throw new AggregateException(original, cleanup); }
            throw;
        }
        computeScoringWorkspace = replacement;
    }

    private static IReadOnlyDictionary<HostManagerComputeProcessIdentity, HostManagerComputeRuntimeFact>
        CreateComputeRuntimeFacts(
            IReadOnlyList<HostManagerTargetInfo> targets,
            ICollection<SchedulingFactExclusion>? exclusions = null)
    {
        var result = new Dictionary<HostManagerComputeProcessIdentity, HostManagerComputeRuntimeFact>(targets.Count);
        var observedIdentities = new HashSet<HostManagerComputeProcessIdentity>();
        foreach (var target in targets)
        {
            if (target.ProcessIds.Count != 1)
            {
                Exclude(null, null, target.TargetId, SchedulingFactExclusionReason.TargetProcessCountNotOne);
                continue;
            }
            var processId = target.ProcessIds[0];
            if (processId <= 0
                || !target.ProcessStartKeys.TryGetValue(processId, out var processStartKey)
                || processStartKey == 0)
            {
                Exclude(processId, null, target.TargetId, SchedulingFactExclusionReason.TargetProcessIdentityMissing);
                continue;
            }

            var expectedTargetId = HostManagerTargetIdentity.CreateProcessTargetId(processId, processStartKey);
            if (!string.Equals(expectedTargetId, target.TargetId, StringComparison.Ordinal))
            {
                Exclude(processId, processStartKey, target.TargetId, SchedulingFactExclusionReason.TargetProcessIdentityMismatch);
                continue;
            }

            var identity = new HostManagerComputeProcessIdentity(processId, processStartKey);
            if (!observedIdentities.Add(identity))
            {
                result.Remove(identity);
                Exclude(processId, processStartKey, target.TargetId, SchedulingFactExclusionReason.DuplicateTargetProcessIdentity);
                continue;
            }
            if (target.RuntimeState is not (HostManagerRuntimeStates.ForegroundFocused or HostManagerRuntimeStates.ForegroundUnfocused
                or HostManagerRuntimeStates.BackgroundWindow or HostManagerRuntimeStates.TrayOnly
                or HostManagerRuntimeStates.BackgroundProcess or HostManagerRuntimeStates.NotRunning))
            {
                Exclude(processId, processStartKey, target.TargetId, SchedulingFactExclusionReason.TargetRuntimeStateInvalid);
                continue;
            }
            result.Add(identity, new HostManagerComputeRuntimeFact(
                EncodeComputeRuntimeState(target.RuntimeState),
                NeutralComputePolicyMultiplier,
                NeutralComputePolicyMultiplier));
        }
        return result;

        void Exclude(int? processId, ulong? startKey, string targetId, SchedulingFactExclusionReason reason)
            => exclusions?.Add(new SchedulingFactExclusion(processId, startKey, targetId, SchedulingFactCode.RuntimeState, reason));
    }

    internal static HostManagerWelfareCapacityInput CreateWelfareCapacityInput(
        HardwareMetricSnapshot hardware,
        IReadOnlyDictionary<HostManagerComputeProcessIdentity, HostManagerComputeRuntimeFact> runtimeFacts)
    {
        ArgumentNullException.ThrowIfNull(hardware);
        ArgumentNullException.ThrowIfNull(runtimeFacts);

        var cpuFreeRatio = hardware.TryGetCurrentDataset(
                SamplingDatasetIds.SystemCpuUsage,
                out _)
            && hardware.Cpu.ObservationStatus == CpuMetricObservationStatus.Complete
            && hardware.Cpu.IsUsageAvailable
            && IsPercent(hardware.Cpu.UsagePercent)
                ? ClampRatio(1 - hardware.Cpu.UsagePercent / 100d)
                : 0;
        var memoryFreeRatio = hardware.TryGetCurrentDataset(
                SamplingDatasetIds.SystemMemoryUsage,
                out _)
            && hardware.Memory.ObservationStatus == SamplingObservationStatus.Current
            && hardware.Memory.IsUsageAvailable
            && hardware.Memory.TotalBytes > 0
            && hardware.Memory.UsedBytes <= hardware.Memory.TotalBytes
            && IsPercent(hardware.Memory.UsagePercent)
                ? ClampRatio(1 - hardware.Memory.UsagePercent / 100d)
                : 0;
        var (gpuFreeRatio, vramFreeRatio) = CreateGpuWelfareFreeRatios(hardware);
        var eligibleProcessCount = checked((uint)runtimeFacts.Values.Count(static runtime =>
            runtime.RuntimeState != NativeComputeScoringRuntimeState.NotRunning));
        return new HostManagerWelfareCapacityInput(
            cpuFreeRatio,
            gpuFreeRatio,
            vramFreeRatio,
            memoryFreeRatio,
            eligibleProcessCount);
    }

    private static (double GpuFreeRatio, double VramFreeRatio)
        CreateGpuWelfareFreeRatios(HardwareMetricSnapshot hardware)
    {
        var inventory = hardware.GpuInventory;
        if (!hardware.TryGetCurrentDataset(
                SamplingDatasetIds.SystemGpuInventory,
                out _)
            || !inventory.IsCurrentComplete())
        {
            return (0, 0);
        }

        var gpuFreeRatio = 1d;
        var hasGpuUsage = false;
        var gpuUsageComplete = true;
        UInt128 usedVramBytes = 0;
        UInt128 totalVramBytes = 0;
        var hasVramCapacity = false;
        var vramCapacityComplete = true;
        foreach (var adapter in inventory.Adapters)
        {
            if (adapter.CapabilityMask.HasFlag(SchedulingGpuCapabilityMask.Usage))
            {
                hasGpuUsage = true;
                if (!hardware.TryGetCurrentDataset(
                        $"gpu.{adapter.Index}.usage",
                        out _)
                    || adapter.UsageStatus != SamplingObservationStatus.Current
                    || !adapter.ValidMetricMask.HasFlag(SchedulingGpuMetricMask.Usage)
                    || !IsPercent(adapter.UsagePercent))
                {
                    gpuUsageComplete = false;
                }
                else
                {
                    gpuFreeRatio = Math.Min(
                        gpuFreeRatio,
                        ClampRatio(1 - adapter.UsagePercent / 100d));
                }
            }

            if (adapter.CapabilityMask.HasFlag(
                    SchedulingGpuCapabilityMask.DedicatedMemory))
            {
                hasVramCapacity = true;
                var capacityMask = SchedulingGpuMetricMask.UsedDedicatedMemory
                    | SchedulingGpuMetricMask.TotalDedicatedMemory;
                if (!hardware.TryGetCurrentDataset(
                        $"gpu.{adapter.Index}.vram",
                        out _)
                    || adapter.CapacityStatus != SamplingObservationStatus.Current
                    || (adapter.ValidMetricMask & capacityMask) != capacityMask
                    || adapter.TotalDedicatedMemoryBytes == 0
                    || adapter.UsedDedicatedMemoryBytes
                        > adapter.TotalDedicatedMemoryBytes)
                {
                    vramCapacityComplete = false;
                }
                else
                {
                    usedVramBytes += adapter.UsedDedicatedMemoryBytes;
                    totalVramBytes += adapter.TotalDedicatedMemoryBytes;
                }
            }
        }

        if (!hasGpuUsage || !gpuUsageComplete)
        {
            gpuFreeRatio = 0;
        }
        var vramFreeRatio = hasVramCapacity
            && vramCapacityComplete
            && totalVramBytes > 0
                ? ClampRatio(1 - (double)usedVramBytes / (double)totalVramBytes)
                : 0;
        return (gpuFreeRatio, vramFreeRatio);
    }

    private static NativeComputeScoringRuntimeState EncodeComputeRuntimeState(string value)
        => value switch
        {
            HostManagerRuntimeStates.ForegroundFocused => NativeComputeScoringRuntimeState.ForegroundFocused,
            HostManagerRuntimeStates.ForegroundUnfocused => NativeComputeScoringRuntimeState.ForegroundUnfocused,
            HostManagerRuntimeStates.BackgroundWindow => NativeComputeScoringRuntimeState.BackgroundWindow,
            HostManagerRuntimeStates.TrayOnly => NativeComputeScoringRuntimeState.TrayOnly,
            HostManagerRuntimeStates.BackgroundProcess => NativeComputeScoringRuntimeState.BackgroundProcess,
            HostManagerRuntimeStates.NotRunning => NativeComputeScoringRuntimeState.NotRunning,
            HostManagerRuntimeStates.Unknown => NativeComputeScoringRuntimeState.Unknown,
            _ => throw new InvalidDataException(
                $"Unknown compute scoring runtime state '{value}'.")
        };

}
