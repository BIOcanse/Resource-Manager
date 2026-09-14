using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.Monitoring;
using ResourceManager.App.Domain.Optimization;
using ResourceManager.App.Domain.RuntimeSpecialization;
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
        HostManagerSchedulingPlanBinding planBinding,
        HostManagerSample sample)
    {
        var attemptGeneration = NextComputeScoringGeneration();
        HostManagerComputeScoringCycleResult? result;
        try
        {
            EnsureComputeScoringWorkspace(plan, cpuScoring);
            var workspace = computeScoringWorkspace
                ?? throw new InvalidOperationException(
                    "The compute scoring authority workspace was not created.");
            var runtimeFacts = CreateComputeRuntimeFacts(sample.Targets);
            var welfareCapacity = CreateWelfareCapacityInput(
                sample.Hardware,
                runtimeFacts);
            result = workspace.Score(
                attemptGeneration,
                sample.ProcessFacts,
                sample.Hardware.GpuInventory,
                runtimeFacts,
                sample.CpuResidency,
                welfareCapacity);
        }
        catch (Exception exception)
        {
            PublishSchedulingAuthorityUnavailable(
                attemptGeneration,
                planBinding,
                sample.Hardware.CapturedAt,
                $"compute-scoring-{exception.GetType().Name}:{exception.Message}",
                exception);
            return null;
        }

        if (result is null)
        {
            PublishSchedulingAuthorityUnavailable(
                attemptGeneration,
                planBinding,
                sample.Hardware.CapturedAt,
                "compute-sample-incomplete");
            return null;
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
                    out var memoryDataset)
                || sample.Hardware.Memory.ObservationStatus
                    != SamplingObservationStatus.Current
                || !sample.Hardware.Memory.IsUsageAvailable
                || sample.Hardware.Memory.TotalBytes == 0
                || sample.Hardware.Memory.UsedBytes
                    > sample.Hardware.Memory.TotalBytes
                || !IsPercent(sample.Hardware.Memory.UsagePercent)
                || memoryDataset.WorkspaceIdentity == 0
                || memoryDataset.CommittedGeneration == 0)
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
            var memorySource = new HostManagerMemorySourceStamp(
                memoryDataset.WorkspaceIdentity,
                memoryDataset.CommittedGeneration);
            var desired = workspace.Plan(
                result,
                sample.ProcessFacts,
                memorySource,
                ComputeMemoryFreeRatioUnits(
                    sample.Hardware.Memory.UsedBytes,
                    sample.Hardware.Memory.TotalBytes,
                    policy.Configuration.RatioUnitsMaximum),
                attemptGeneration,
                policy.AllowUnrestricted);
            var nonAdaptedProjection = ProjectNonAdaptedMemoryModes(
                result,
                desired,
                sample,
                policy);
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
        ulong attemptGeneration,
        HostManagerSchedulingPlanBinding planBinding,
        DateTimeOffset observedAt,
        string reason,
        Exception? exception = null)
    {
        ClearNonAdaptedMemoryModeProjection();
        schedulingAuthorityOwner.PublishUnavailable(
            attemptGeneration,
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
        var replacement = new HostManagerComputeScoringWorkspace(in configuration, cpuScoring);
        computeScoringWorkspace?.Dispose();
        computeScoringWorkspace = replacement;
    }

    private static IReadOnlyDictionary<HostManagerComputeProcessIdentity, HostManagerComputeRuntimeFact>
        CreateComputeRuntimeFacts(IReadOnlyList<HostManagerTargetInfo> targets)
    {
        var result = new Dictionary<HostManagerComputeProcessIdentity, HostManagerComputeRuntimeFact>(targets.Count);
        foreach (var target in targets)
        {
            if (target.ProcessIds.Count != 1)
            {
                throw new InvalidDataException(
                    $"Compute scoring target {target.TargetId} does not represent exactly one process instance.");
            }
            var processId = target.ProcessIds[0];
            if (!target.ProcessStartKeys.TryGetValue(processId, out var processStartKey)
                || processStartKey == 0)
            {
                throw new InvalidDataException(
                    $"Compute scoring target {target.TargetId} has no exact process start identity.");
            }

            var expectedTargetId = HostManagerTargetIdentity.CreateProcessTargetId(
                processId,
                processStartKey);
            if (!string.Equals(expectedTargetId, target.TargetId, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Compute scoring target {target.TargetId} does not match {processId}/{processStartKey}.");
            }

            var identity = new HostManagerComputeProcessIdentity(processId, processStartKey);
            if (!result.TryAdd(
                identity,
                new HostManagerComputeRuntimeFact(
                    EncodeComputeRuntimeState(target.RuntimeState),
                    NeutralComputePolicyMultiplier,
                    NeutralComputePolicyMultiplier)))
            {
                throw new InvalidDataException(
                    $"Compute scoring received duplicate process identity {processId}/{processStartKey}.");
            }
        }
        return result;
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
            eligibleProcessCount,
            CreateWelfarePublicationFingerprint(hardware));
    }

    private static ulong CreateWelfarePublicationFingerprint(
        HardwareMetricSnapshot hardware)
    {
        const ulong offsetBasis = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;
        var hash = offsetBasis;
        AddDataset(SamplingDatasetIds.SystemCpuUsage);
        AddDataset(SamplingDatasetIds.SystemMemoryUsage);
        AddDataset(SamplingDatasetIds.SystemGpuInventory);
        Add(hardware.GpuInventory.TopologyFingerprint);
        Add(hardware.GpuInventory.ObservedCount);
        Add(hardware.GpuInventory.SkippedCount);
        Add(hardware.GpuInventory.OverflowCount);
        var adapters = hardware.GpuInventory.Adapters
            .OrderBy(static adapter => adapter.Index)
            .ThenBy(static adapter => adapter.AdapterKey);
        foreach (var adapter in adapters)
        {
            Add(checked((ulong)adapter.Index));
            Add(adapter.AdapterKey);
            Add((ulong)adapter.CapabilityMask);
            if (adapter.CapabilityMask.HasFlag(SchedulingGpuCapabilityMask.Usage))
            {
                AddDataset($"gpu.{adapter.Index}.usage");
            }
            if (adapter.CapabilityMask.HasFlag(SchedulingGpuCapabilityMask.DedicatedMemory))
            {
                AddDataset($"gpu.{adapter.Index}.vram");
            }
        }
        return hash == 0 ? 1 : hash;

        void AddDataset(string id)
        {
            if (!hardware.Datasets.TryGetValue(id, out var dataset)
                || !string.Equals(dataset.DatasetId, id, StringComparison.OrdinalIgnoreCase))
            {
                for (var field = 0; field < 9; field++)
                {
                    Add(0);
                }
                return;
            }

            Add(1);
            Add((ulong)dataset.Status);
            Add(dataset.SourceGeneration);
            Add(checked((ulong)Math.Max(0, dataset.ObservedAtUtcTicks)));
            Add(dataset.WorkspaceIdentity);
            Add(dataset.ConfigurationGeneration);
            Add(dataset.CatalogGeneration);
            Add(dataset.CommittedGeneration);
            Add(checked((ulong)Math.Max(0, dataset.LastAttemptAtUtcTicks)));
        }

        void Add(ulong value)
        {
            for (var byteIndex = 0; byteIndex < sizeof(ulong); byteIndex++)
            {
                hash ^= (byte)value;
                hash = unchecked(hash * prime);
                value >>= 8;
            }
        }
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

    private ulong NextComputeScoringGeneration()
    {
        computeScoringSequence = Math.Max(
            computeScoringSequence,
            schedulingAuthorityOwner.Capture().AttemptGeneration);
        computeScoringSequence = unchecked(computeScoringSequence + 1);
        if (computeScoringSequence == 0)
        {
            throw new InvalidOperationException(
                "The Host Manager compute scoring sequence wrapped to zero.");
        }
        return computeScoringSequence;
    }
}
