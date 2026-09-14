using System.Diagnostics;
using ResourceManager.App.Application.Optimization;
using ResourceManager.App.Application.Optimization.SmartControl;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.Monitoring;
using ResourceManager.App.Domain.Optimization;
using ResourceManager.App.Domain.Optimization.MemoryCleanup;
using ResourceManager.App.Domain.ResourceBreakdown;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.NativeCore;

namespace ResourceManager.App.Infrastructure.Optimization;

public sealed partial class HostManagerSmartCoordinator
{
    private MemoryCleanupHostedAdmissionBaseline? memoryCleanupHostedAdmissionBaseline;

    private async ValueTask<HostManagerMemoryCleanupExecutionResult>
        ApplyAutomaticMemoryCleanupAsync(
        HostManagerCycleEffectPermit permit,
        HostManagerProcessEffectValidationCycleSnapshot processEffectValidationScope,
        HostManagerSample sample,
        HostManagerComputeScoringCycleResult? compute,
        CompiledRuntimePlan runtimePlan,
        ulong cycleSequence,
        bool capacitySampleFresh,
        CancellationToken cancellationToken)
    {
        permit.Require(HostManagerCycleEffectKind.AutomaticMemoryCleanup);
        if (!capacitySampleFresh)
        {
            return HostManagerMemoryCleanupExecutionResult.NotRun(
                HostManagerMemoryCleanupRoundDisposition.InvalidCapacitySample);
        }
        var cleanupPlan = runtimePlan.HostManager.HotPublish.MemoryCleanup;
        var initialCapacity = CreateMemoryCleanupCapacitySnapshot(sample.Hardware);
        if (!HasDeterministicMemoryCleanupModeInputs(initialCapacity, cleanupPlan))
        {
            memoryCleanupHostedAdmissionBaseline = null;
            return HostManagerMemoryCleanupExecutionResult.NotRun(
                HostManagerMemoryCleanupRoundDisposition.InvalidCapacitySample);
        }
        if (!IsMemoryCleanupPressureActionable(initialCapacity, cleanupPlan))
        {
            memoryCleanupHostedAdmissionBaseline = null;
            return HostManagerMemoryCleanupExecutionResult.NotRun(
                HostManagerMemoryCleanupRoundDisposition.DeferredByCurrentCapacity);
        }

        var currentAdmissionBaseline = new MemoryCleanupHostedAdmissionBaseline(
            runtimePlan.Version,
            cleanupPlan.ConfigurationGeneration,
            sample.Hardware);
        var previousAdmissionBaseline = memoryCleanupHostedAdmissionBaseline;
        if (previousAdmissionBaseline is null
            || !previousAdmissionBaseline.CanAdmit(currentAdmissionBaseline))
        {
            memoryCleanupHostedAdmissionBaseline = currentAdmissionBaseline;
            return HostManagerMemoryCleanupExecutionResult.NotRun(
                HostManagerMemoryCleanupRoundDisposition
                    .AwaitingNewHostedCapacityObservation);
        }

        var candidateProjection = CreateMemoryCleanupCandidates(
            sample.Targets,
            compute,
            sample.ProcessFacts.Generation);
        if (!candidateProjection.IsComplete)
        {
            return HostManagerMemoryCleanupExecutionResult.NotRun(
                HostManagerMemoryCleanupRoundDisposition.IncompleteCandidateProjection);
        }
        var request = new AutomaticMemoryCleanupPlanRequest(
            AutomaticMemoryCleanupRequestKind.Normal
                | AutomaticMemoryCleanupRequestKind.EvaluateEmergency,
            initialCapacity.PhysicalMemoryFreeRatio,
            initialCapacity.PhysicalMemoryFreeRatio,
            1,
            candidateProjection.Candidates);
        var executionPhysicalMemoryFreeRatio =
            initialCapacity.PhysicalMemoryFreeRatio;
        var result = (await ExecuteMemoryCleanupCoreAsync(
            permit,
            processEffectValidationScope,
            request,
            validationFactContext: default,
            compute?.SchedulingGeneration ?? 0,
            cycleSequence,
            "ordinary",
            async (currentRequest, finalAdmissionCancellationToken) =>
            {
                var admission = await CaptureMemoryCleanupFinalAdmissionAsync(
                    currentRequest,
                    candidateProjection.Scores,
                    sample.ProcessFacts,
                    previousAdmissionBaseline.Hardware,
                    currentAdmissionBaseline.Hardware,
                    cleanupPlan,
                    candidateProjection.UnknownCandidateCount,
                    finalAdmissionCancellationToken);
                if (admission.IsComplete)
                {
                    executionPhysicalMemoryFreeRatio =
                        admission.Request.PhysicalMemoryFreeRatio;
                }
                return admission;
            },
            cancellationToken))
            .StampCompletion(timeProvider.GetUtcNow());
        memoryCleanupHostedAdmissionBaseline = currentAdmissionBaseline;
        if (result.RequestedCount > 0)
        {
            logger.LogInformation(
                "Automatic memory cleanup requested {RequestedCount} process trims at {PhysicalMemoryFreePercent:0.0}% physical free memory; {SucceededCount} succeeded.",
                result.RequestedCount,
                Math.Clamp(executionPhysicalMemoryFreeRatio, 0, 1) * 100,
                result.SucceededCount);
        }
        return result;
    }

    private MemoryCleanupCandidateProjectionResult CreateMemoryCleanupCandidates(
        IReadOnlyList<HostManagerTargetInfo> targets,
        HostManagerComputeScoringCycleResult? compute,
        ulong processInventoryGeneration)
    {
        if (compute?.Cpu is not { } cpu)
        {
            return MemoryCleanupCandidateProjectionResult.Invalid;
        }
        var scoredProcesses = cpu.Scores
            .Where(static score => score.Kind == NativeComputeScoringOutputKind.ProcessCpu)
            .Select(static score => new HostManagerComputeProcessIdentity(score.ProcessId, score.ProcessStartKey))
            .ToHashSet();
        var factProjection = CreateMemoryCleanupTargetFacts(targets, scoredProcesses);
        if (!factProjection.IsComplete)
        {
            return MemoryCleanupCandidateProjectionResult.Invalid;
        }
        if (!HostManagerMemoryCleanupScoreIndex.TryCreate(
                compute,
                processInventoryGeneration,
                factProjection.Facts,
                out var scores))
        {
            return MemoryCleanupCandidateProjectionResult.Invalid;
        }

        var candidates = HostManagerMemoryCleanupCandidateProjection.Create(
            factProjection.Facts,
            scores);
        return candidates.Count == factProjection.Facts.Count
            ? new MemoryCleanupCandidateProjectionResult(
                true,
                candidates,
                scores,
                factProjection.UnknownCandidateCount)
            : MemoryCleanupCandidateProjectionResult.Invalid;
    }

    private MemoryCleanupTargetFactProjectionResult CreateMemoryCleanupTargetFacts(
        IReadOnlyList<HostManagerTargetInfo> targets,
        IReadOnlySet<HostManagerComputeProcessIdentity> scoredProcesses)
    {
        var candidateFacts = new List<HostManagerMemoryCleanupTargetFact>(targets.Count);
        var unknownCandidateCount = 0;
        for (var targetIndex = 0; targetIndex < targets.Count; targetIndex++)
        {
            var target = targets[targetIndex];
            if (!target.ProcessIds.Any(processId =>
                    target.ProcessStartKeys.TryGetValue(processId, out var startKey)
                    && scoredProcesses.Contains(new(processId, startKey))))
            {
                continue;
            }
            if (target.ProcessIds.Count != 1
                || target.ProcessStartKeys.Count != 1
                || !double.IsFinite(target.BaseScore)
                || target.BaseScore < 0)
            {
                return MemoryCleanupTargetFactProjectionResult.Invalid;
            }
            for (var processIndex = 0; processIndex < target.ProcessIds.Count; processIndex++)
            {
                var processId = target.ProcessIds[processIndex];
                if (!target.ProcessStartKeys.TryGetValue(processId, out var expectedStartKey)
                    || expectedStartKey == 0
                    || expectedStartKey > long.MaxValue)
                {
                    return MemoryCleanupTargetFactProjectionResult.Invalid;
                }
                DateTimeOffset expectedStartedAt;
                try
                {
                    expectedStartedAt = DateTimeOffset.FromFileTime(
                        checked((long)expectedStartKey));
                }
                catch (Exception exception) when (
                    exception is ArgumentOutOfRangeException or OverflowException)
                {
                    return MemoryCleanupTargetFactProjectionResult.Invalid;
                }

                var processRead = processPolicyWriter.ReadProcessInstanceForRecovery(processId);
                var exactLiveIdentity = false;
                var liveIdentityUnknown = false;
                switch (processRead.Status)
                {
                    case RecoveryReadStatus.Found:
                        if (processRead.Value is null
                            || processRead.Value.ProcessId != processId)
                        {
                            return MemoryCleanupTargetFactProjectionResult.Invalid;
                        }

                        ulong observedStartKey;
                        try
                        {
                            observedStartKey = checked(
                                (ulong)processRead.Value.StartedAt.ToFileTime());
                        }
                        catch (Exception exception) when (
                            exception is ArgumentOutOfRangeException or OverflowException)
                        {
                            return MemoryCleanupTargetFactProjectionResult.Invalid;
                        }

                        if (observedStartKey == 0)
                        {
                            return MemoryCleanupTargetFactProjectionResult.Invalid;
                        }
                        exactLiveIdentity = observedStartKey == expectedStartKey;
                        break;

                    case RecoveryReadStatus.NotFoundOrExited:
                        if (processRead.Value is not null)
                        {
                            return MemoryCleanupTargetFactProjectionResult.Invalid;
                        }
                        break;

                    case RecoveryReadStatus.Unavailable:
                        if (processRead.Value is not null)
                        {
                            return MemoryCleanupTargetFactProjectionResult.Invalid;
                        }
                        liveIdentityUnknown = true;
                        break;

                    default:
                        return MemoryCleanupTargetFactProjectionResult.Invalid;
                }

                if (target.CanApplyAutomaticPolicy && liveIdentityUnknown)
                {
                    unknownCandidateCount++;
                }
                candidateFacts.Add(new HostManagerMemoryCleanupTargetFact(
                    target.TargetId,
                    target.SoftwareId,
                    target.DisplayName,
                    processId,
                    expectedStartKey,
                    expectedStartedAt,
                    target.RuntimeState,
                    target.BaseScore,
                    target.CanApplyAutomaticPolicy
                        && exactLiveIdentity));
            }
        }
        return new MemoryCleanupTargetFactProjectionResult(
            true,
            candidateFacts,
            unknownCandidateCount);
    }

    private async Task<MemoryCleanupFinalAdmissionResult>
        CaptureMemoryCleanupFinalAdmissionAsync(
        AutomaticMemoryCleanupPlanRequest request,
        IReadOnlyDictionary<HostManagerComputeProcessIdentity, HostManagerComputeScore>
            baselineScores,
        SchedulingProcessFactSnapshot initialProcessFacts,
        HardwareMetricSnapshot initialHardware,
        HardwareMetricSnapshot hardware,
        CompiledHostManagerMemoryCleanupHotPublishPlan cleanupPlan,
        int initialUnknownCandidateCount,
        CancellationToken cancellationToken)
    {
        if (!IsStrictlyNewerMemoryCleanupCapacity(
                initialHardware,
                hardware))
        {
            return MemoryCleanupFinalAdmissionResult.Incomplete(
                HostManagerMemoryCleanupRoundDisposition.FinalAdmissionCapacityUnavailable);
        }
        if (!SystemMemoryUsageDependency.TryCreate(
                hardware,
                timeProvider.GetUtcNow(),
                out var finalMemoryDependency))
        {
            return MemoryCleanupFinalAdmissionResult.Incomplete(
                HostManagerMemoryCleanupRoundDisposition.FinalAdmissionCapacityUnavailable);
        }

        var processFacts = schedulingProcessFactSource.ReadLatest(
            new SchedulingProcessFactRequest(
                SchedulingProcessMetricMask.MemoryUsage
                    | SchedulingProcessMetricMask.RuntimeState,
                finalMemoryDependency,
                hardware.GpuInventory));
        if (processFacts is null)
        {
            return MemoryCleanupFinalAdmissionResult.Incomplete(
                HostManagerMemoryCleanupRoundDisposition.FinalAdmissionCandidateProjectionIncomplete);
        }
        if (!TryIndexCurrentMemoryCleanupProcessFacts(
                initialProcessFacts,
                processFacts,
                finalMemoryDependency,
                out var currentProcesses,
                out var maximumBaseScores))
        {
            return MemoryCleanupFinalAdmissionResult.Incomplete(
                HostManagerMemoryCleanupRoundDisposition.FinalAdmissionCandidateProjectionIncomplete);
        }
        var capacity = CreateMemoryCleanupCapacitySnapshot(hardware);
        if (!IsMemoryCleanupPressureActionable(capacity, cleanupPlan))
        {
            return MemoryCleanupFinalAdmissionResult.Incomplete(
                HasDeterministicMemoryCleanupModeInputs(capacity, cleanupPlan)
                    ? HostManagerMemoryCleanupRoundDisposition.DeferredByCurrentCapacity
                    : HostManagerMemoryCleanupRoundDisposition.FinalAdmissionCapacityUnavailable);
        }

        var protectionLevels = await ResolveNativeProtectionLevelsAsync(cancellationToken);

        var currentCandidates = new List<AutomaticMemoryCleanupCandidate>(
            request.Candidates.Count);
        var knownFilteredCount = 0;
        var unknownCount = initialUnknownCandidateCount;
        for (var index = 0; index < request.Candidates.Count; index++)
        {
            var baseline = request.Candidates[index];
            if (!baseline.CanApply)
            {
                currentCandidates.Add(baseline);
                continue;
            }

            var identity = CreateProcessIdentity(baseline);
            if (!baselineScores.TryGetValue(identity, out var baselineScore))
            {
                return MemoryCleanupFinalAdmissionResult.Incomplete(
                    HostManagerMemoryCleanupRoundDisposition.FinalAdmissionCandidateProjectionIncomplete);
            }
            if (!currentProcesses.TryGetValue(identity, out var current))
            {
                knownFilteredCount++;
                continue;
            }

            var softwareKey = NativeStableIdentity.CreateCaseInsensitiveKey(current.SoftwareId);
            if (!maximumBaseScores.TryGetValue(softwareKey, out var maximumBaseScore))
            {
                return MemoryCleanupFinalAdmissionResult.Incomplete(
                    HostManagerMemoryCleanupRoundDisposition.FinalAdmissionCandidateProjectionIncomplete);
            }
            var protectionLevel = protectionLevels.GetValueOrDefault(
                baseline.TargetId,
                protectionLevels.GetValueOrDefault(current.SoftwareId));
            var admission = HostManagerMemoryCleanupPrePonrAdmission.EvaluateCurrentFact(
                baseline,
                baselineScore,
                current,
                maximumBaseScore,
                current.RuntimeState,
                protectionLevel);
            if (admission == HostManagerMemoryCleanupCandidateAdmissionStatus.Eligible)
            {
                admission = HostManagerMemoryCleanupPrePonrAdmission.EvaluateLiveIdentity(
                    baseline,
                    processPolicyWriter.ReadProcessInstanceForRecovery(current.ProcessId));
            }
            if (admission == HostManagerMemoryCleanupCandidateAdmissionStatus.Eligible)
            {
                currentCandidates.Add(baseline with
                {
                    MemoryUsedPercent = current.MemoryUsagePercent
                });
                continue;
            }

            currentCandidates.Add(baseline with { CanApply = false });
            if (admission == HostManagerMemoryCleanupCandidateAdmissionStatus.Unknown)
            {
                unknownCount++;
            }
            else
            {
                knownFilteredCount++;
            }
        }

        return MemoryCleanupFinalAdmissionResult.Complete(
            request with
            {
                OrdinaryMemoryFreeRatio = capacity.PhysicalMemoryFreeRatio,
                PhysicalMemoryFreeRatio = capacity.PhysicalMemoryFreeRatio,
                VirtualMemoryFreeRatio = capacity.VirtualMemoryFreeRatio,
                Candidates = currentCandidates.ToArray()
            },
            new HostManagerMemoryCleanupValidationFactContext(
                cleanupPlan.ConfigurationGeneration,
                cleanupPlan.GuardedFreeRatio,
                cleanupPlan.PhysicalEmergencyFreeRatio,
                cleanupPlan.VirtualEmergencyFreeRatio,
                hardware.WorkspaceIdentity,
                hardware.ConfigurationGeneration,
                hardware.CatalogGeneration,
                initialHardware.CommittedGeneration,
                hardware.CommittedGeneration,
                initialHardware.CapturedAt.UtcTicks,
                hardware.CapturedAt.UtcTicks,
                OrdinaryMemoryFreeRatioCurrent:
                    capacity.PhysicalMemoryFreeRatioCurrent,
                PhysicalMemoryFreeRatioCurrent:
                    capacity.PhysicalMemoryFreeRatioCurrent,
                VirtualMemoryFreeRatioCurrent:
                    capacity.VirtualMemoryFreeRatioCurrent),
            knownFilteredCount,
            unknownCount);
    }

    private static bool TryIndexCurrentMemoryCleanupProcessFacts(
        SchedulingProcessFactSnapshot initial,
        SchedulingProcessFactSnapshot current,
        SystemMemoryUsageDependency expectedMemoryDependency,
        out IReadOnlyDictionary<HostManagerComputeProcessIdentity, SchedulingProcessFact> processes,
        out IReadOnlyDictionary<ulong, double> maximumBaseScores)
    {
        processes = new Dictionary<HostManagerComputeProcessIdentity, SchedulingProcessFact>();
        maximumBaseScores = new Dictionary<ulong, double>();
        var requiredMask = SchedulingProcessMetricMask.MemoryUsage
            | SchedulingProcessMetricMask.RuntimeState;
        if (!current.IsInventoryCurrentComplete()
            || !current.IsMemoryCurrentComplete(expectedMemoryDependency)
            || !current.IsRuntimeStateCurrentComplete()
            || current.Generation < initial.Generation
            || current.ObservedAtUtcTicks < initial.ObservedAtUtcTicks
            || current.RequestedMetricMask != requiredMask
            || current.CurrentMetricMask != requiredMask)
        {
            return false;
        }

        var indexed = new Dictionary<HostManagerComputeProcessIdentity, SchedulingProcessFact>(
            current.Processes.Count);
        var maxima = new Dictionary<ulong, double>();
        var attributionProcesses = current.HasSeparateFoundationPayloads
            ? current.AttributionProcesses
            : current.Processes;
        if (current.HasSeparateFoundationPayloads
            && !current.TryGetCurrentFoundationDataset(
                SamplingDatasetIds.ProcessAttribution,
                out _))
        {
            return false;
        }
        foreach (var process in attributionProcesses)
        {
            var softwareKey = NativeStableIdentity.CreateCaseInsensitiveKey(process.SoftwareId);
            if (process.ProcessId <= 0
                || process.ProcessStartKey == 0
                || string.IsNullOrWhiteSpace(process.ProcessName)
                || softwareKey == 0
                || !double.IsFinite(process.BaseScore)
                || process.BaseScore < 0)
            {
                return false;
            }
            maxima[softwareKey] = maxima.TryGetValue(softwareKey, out var currentMaximum)
                ? Math.Max(currentMaximum, process.BaseScore)
                : process.BaseScore;
        }
        foreach (var process in current.Processes)
        {
            var identity = new HostManagerComputeProcessIdentity(
                process.ProcessId,
                process.ProcessStartKey);
            var softwareKey = NativeStableIdentity.CreateCaseInsensitiveKey(process.SoftwareId);
            if (process.ProcessId <= 0
                || process.ProcessStartKey == 0
                || process.SourceGeneration != current.Generation
                || string.IsNullOrWhiteSpace(process.ProcessName)
                || softwareKey == 0
                || !double.IsFinite(process.BaseScore)
                || process.BaseScore < 0)
            {
                return false;
            }
            var hasFinalAdmissionFacts = process.ValidMetricMask.HasFlag(
                    SchedulingProcessMetricMask.MemoryUsage)
                && process.ValidMetricMask.HasFlag(
                    SchedulingProcessMetricMask.RuntimeState);
            if (hasFinalAdmissionFacts && !indexed.TryAdd(identity, process))
            {
                return false;
            }
        }

        processes = indexed;
        maximumBaseScores = maxima;
        return true;
    }

    private sealed record MemoryCleanupHostedAdmissionBaseline(
        long RuntimePlanVersion,
        ulong CleanupConfigurationGeneration,
        HardwareMetricSnapshot Hardware)
    {
        internal bool CanAdmit(MemoryCleanupHostedAdmissionBaseline current)
            => current.RuntimePlanVersion == RuntimePlanVersion
                && current.CleanupConfigurationGeneration ==
                    CleanupConfigurationGeneration
                && IsStrictlyNewerMemoryCleanupCapacity(Hardware, current.Hardware);
    }

    private static bool IsStrictlyNewerMemoryCleanupCapacity(
        HardwareMetricSnapshot initial,
        HardwareMetricSnapshot current)
        => initial.TryGetCurrentDataset(
                SamplingDatasetIds.SystemMemoryUsage,
                out var initialMemory)
            && current.TryGetCurrentDataset(
                SamplingDatasetIds.SystemMemoryUsage,
                out var currentMemory)
            && currentMemory.WorkspaceIdentity == initialMemory.WorkspaceIdentity
            && currentMemory.ConfigurationGeneration ==
                initialMemory.ConfigurationGeneration
            && currentMemory.CatalogGeneration == initialMemory.CatalogGeneration
            && currentMemory.SourceGeneration > initialMemory.SourceGeneration
            && currentMemory.CommittedGeneration >
                initialMemory.CommittedGeneration
            && currentMemory.ObservedAtUtcTicks > initialMemory.ObservedAtUtcTicks;

    private static MemoryCleanupCapacitySnapshot CreateMemoryCleanupCapacitySnapshot(
        HardwareMetricSnapshot hardware)
    {
        var physicalCurrent = hardware.TryGetCurrentDataset(
                SamplingDatasetIds.SystemMemoryUsage,
                out _)
            && hardware.Memory.IsUsageAvailable
            && hardware.Memory.ObservationStatus == SamplingObservationStatus.Current
            && hardware.Memory.TotalBytes > 0
            && hardware.Memory.UsedBytes <= hardware.Memory.TotalBytes
            && IsPercent(hardware.Memory.UsagePercent);
        var virtualCurrent = hardware.TryGetCurrentDataset(
                SamplingDatasetIds.SystemVirtualMemoryUsage,
                out _)
            && hardware.VirtualMemory.IsSelectable
            && hardware.VirtualMemory.ObservationStatus ==
                SamplingObservationStatus.Current
            && hardware.VirtualMemory.TotalBytes > 0
            && hardware.VirtualMemory.UsedBytes <= hardware.VirtualMemory.TotalBytes
            && IsPercent(hardware.VirtualMemory.UsagePercent);
        return new MemoryCleanupCapacitySnapshot(
            physicalCurrent
                ? ClampRatio(1 - hardware.Memory.UsagePercent / 100d)
                : 1,
            physicalCurrent,
            virtualCurrent
                ? ClampRatio(1 - hardware.VirtualMemory.UsagePercent / 100d)
                : 1,
            virtualCurrent);
    }

    private static bool HasDeterministicMemoryCleanupModeInputs(
        MemoryCleanupCapacitySnapshot capacity,
        CompiledHostManagerMemoryCleanupHotPublishPlan cleanupPlan)
        => capacity.PhysicalMemoryFreeRatioCurrent;

    private static bool IsMemoryCleanupPressureActionable(
        MemoryCleanupCapacitySnapshot capacity,
        CompiledHostManagerMemoryCleanupHotPublishPlan cleanupPlan)
        => IsEmergencyMemoryCleanupPressure(capacity, cleanupPlan)
            || (capacity.PhysicalMemoryFreeRatioCurrent
                && capacity.PhysicalMemoryFreeRatio
                    > cleanupPlan.PhysicalEmergencyFreeRatio
                && capacity.PhysicalMemoryFreeRatio
                    < cleanupPlan.GuardedFreeRatio);

    private static bool IsEmergencyMemoryCleanupPressure(
        MemoryCleanupCapacitySnapshot capacity,
        CompiledHostManagerMemoryCleanupHotPublishPlan cleanupPlan)
        => (capacity.PhysicalMemoryFreeRatioCurrent
                && capacity.PhysicalMemoryFreeRatio
                    <= cleanupPlan.PhysicalEmergencyFreeRatio)
            || (capacity.VirtualMemoryFreeRatioCurrent
                && capacity.VirtualMemoryFreeRatio
                    <= cleanupPlan.VirtualEmergencyFreeRatio);

    private HostManagerMemoryCleanupExecutionResult ExecuteMemoryCleanup(
        HostManagerCycleEffectPermit permit,
        AutomaticMemoryCleanupPlanRequest request,
        ulong attemptGeneration,
        string reason,
        CancellationToken cancellationToken)
    {
        var cleanupPlan = runtimePlanProvider.Current.HostManager.RequirePublished()
            .HotPublish.MemoryCleanup;
        return ExecuteMemoryCleanupCoreAsync(
                permit,
                processEffectValidationScopeAuthorityOwner.Capture(),
                request,
                new HostManagerMemoryCleanupValidationFactContext(
                    cleanupPlan.ConfigurationGeneration,
                    cleanupPlan.GuardedFreeRatio,
                    cleanupPlan.PhysicalEmergencyFreeRatio,
                    cleanupPlan.VirtualEmergencyFreeRatio,
                    CapacityWorkspaceIdentity: 1,
                    CapacityConfigurationGeneration: 1,
                    CapacityCatalogGeneration: 1,
                    BaselineCapacityCommittedGeneration: 1,
                    FinalCapacityCommittedGeneration: 2,
                    BaselineCapacityCapturedAtUtcTicks: 1,
                    FinalCapacityCapturedAtUtcTicks: 2,
                    OrdinaryMemoryFreeRatioCurrent: true,
                    PhysicalMemoryFreeRatioCurrent: true,
                    VirtualMemoryFreeRatioCurrent: true),
                attemptGeneration,
                attemptGeneration,
                reason,
                finalAdmission: null,
                cancellationToken)
            .GetAwaiter()
            .GetResult();
    }

    private async Task<HostManagerMemoryCleanupExecutionResult> ExecuteMemoryCleanupCoreAsync(
        HostManagerCycleEffectPermit permit,
        HostManagerProcessEffectValidationCycleSnapshot processEffectValidationScope,
        AutomaticMemoryCleanupPlanRequest request,
        HostManagerMemoryCleanupValidationFactContext validationFactContext,
        ulong attemptGeneration,
        ulong cycleSequence,
        string reason,
        Func<
            AutomaticMemoryCleanupPlanRequest,
            CancellationToken,
            Task<MemoryCleanupFinalAdmissionResult>>? finalAdmission,
        CancellationToken cancellationToken)
    {
        permit.Require(HostManagerCycleEffectKind.AutomaticMemoryCleanup);
        var originalCandidateCount = request.Candidates.Count;
        if (!processEffectValidationScope.IsProductionUnscoped)
        {
            request = request with
            {
                Candidates = request.Candidates
                    .Where(candidate => processEffectValidationScope.Allows(
                        CreateProcessIdentity(candidate)))
                    .ToArray()
            };
        }
        var blockedCandidateCount = 0;
        IReadOnlySet<HostManagerComputeProcessIdentity> blocked;
        try
        {
            blocked = memoryCleanupAttemptJournal.ReconcileAndCaptureBlocked(
                processPolicyWriter.ReadProcessInstanceForRecovery);
            if (!processEffectValidationScopeAuthorityOwner
                    .ReconcileDeclaredMemoryCleanupHandoff(
                        memoryCleanupAttemptJournal.CapturePendingBatches()))
            {
                throw new InvalidDataException(
                    "The validation scope could not reconcile its declared memory-cleanup handoff.");
            }
            if (blocked.Count > 0)
            {
                blockedCandidateCount = request.Candidates.Count(candidate =>
                    blocked.Contains(CreateProcessIdentity(candidate)));
                request = request with
                {
                    Candidates = request.Candidates
                        .Where(candidate => !blocked.Contains(CreateProcessIdentity(candidate)))
                        .ToArray()
                };
            }
        }
        catch (Exception exception) when (IsExpectedMemoryCleanupAttemptJournalFailure(exception))
        {
            logger.LogError(
                exception,
                "Automatic memory cleanup attempt-journal reconciliation failed closed. Reason: {Reason}",
                reason);
            return HostManagerMemoryCleanupExecutionResult.Incomplete(
                HostManagerMemoryCleanupRoundDisposition.JournalReconcileFailed,
                originalCandidateCount);
        }

        if (attemptGeneration == 0)
        {
            return HostManagerMemoryCleanupExecutionResult.Incomplete(
                HostManagerMemoryCleanupRoundDisposition.MissingSchedulingGeneration,
                originalCandidateCount,
                blockedCandidateCount);
        }

        var finalAdmissionApplied = false;
        var knownFilteredCandidateCount = 0;
        var unknownCandidateCount = 0;
        if (finalAdmission is not null)
        {
            MemoryCleanupFinalAdmissionResult admission = default;
            Exception? captureFailure = null;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                admission = await finalAdmission(request, cancellationToken);
            }
            catch (Exception exception) when (IsExpectedMemoryCleanupFinalAdmissionFailure(exception))
            {
                captureFailure = exception;
            }

            if (captureFailure is not null)
            {
                logger.LogError(
                    captureFailure,
                    "Automatic memory cleanup final admission failed closed. Reason: {Reason}",
                    reason);
                return HostManagerMemoryCleanupExecutionResult.Incomplete(
                    HostManagerMemoryCleanupRoundDisposition.FinalAdmissionCaptureFailed,
                    originalCandidateCount,
                    blockedCandidateCount);
            }
            if (!admission.IsComplete)
            {
                return HostManagerMemoryCleanupExecutionResult.Incomplete(
                    admission.Disposition,
                    originalCandidateCount,
                    blockedCandidateCount);
            }

            request = admission.Request;
            validationFactContext = admission.ValidationFactContext;
            finalAdmissionApplied = true;
            knownFilteredCandidateCount = admission.KnownFilteredCandidateCount;
            unknownCandidateCount = admission.UnknownCandidateCount;
            if (!hostManagerSmartControlZones.CanRun(
                    HostManagerSmartControlZoneIds.PolicyExecution))
            {
                return HostManagerMemoryCleanupExecutionResult.Incomplete(
                    HostManagerMemoryCleanupRoundDisposition.DeferredByCurrentPolicy,
                    originalCandidateCount,
                    blockedCandidateCount);
            }
        }

        AutomaticMemoryCleanupPlanResult plan;
        try
        {
            plan = automaticMemoryCleanupPlanner.Plan(request);
        }
        catch (Exception exception) when (exception is InvalidOperationException
            or InvalidDataException
            or DllNotFoundException
            or EntryPointNotFoundException
            or BadImageFormatException
            or TypeInitializationException)
        {
            logger.LogError(exception, "Native automatic memory cleanup plan failed closed. Reason: {Reason}", reason);
            return HostManagerMemoryCleanupExecutionResult.Incomplete(
                HostManagerMemoryCleanupRoundDisposition.PlannerFailed,
                originalCandidateCount,
                blockedCandidateCount);
        }

        if (plan.ConfigurationGeneration == 0
            || plan.ConfigurationGeneration
                != validationFactContext.PlannerConfigurationGeneration)
        {
            logger.LogError(
                "Automatic memory cleanup planner configuration generation {PlannerConfigurationGeneration} did not match final-admission generation {AdmissionConfigurationGeneration}. Reason: {Reason}",
                plan.ConfigurationGeneration,
                validationFactContext.PlannerConfigurationGeneration,
                reason);
            if (plan.Decisions.Count == 0)
            {
                return HostManagerMemoryCleanupExecutionResult.Incomplete(
                    HostManagerMemoryCleanupRoundDisposition.PlannerConfigurationMismatch,
                    originalCandidateCount,
                    blockedCandidateCount,
                    plan.StateRevision);
            }

            var mismatchFeedback = CreateUnattemptedMemoryCleanupFeedback(plan.Decisions);
            try
            {
                return HostManagerMemoryCleanupExecutionResult.Incomplete(
                    HostManagerMemoryCleanupRoundDisposition.PlannerConfigurationMismatch,
                    originalCandidateCount,
                    blockedCandidateCount,
                    plan.StateRevision,
                    plan.Decisions.Count);
            }
            finally
            {
                automaticMemoryCleanupPlanner.Complete(mismatchFeedback);
            }
        }

        if (plan.Decisions.Count == 0)
        {
            if (blockedCandidateCount != 0)
            {
                return HostManagerMemoryCleanupExecutionResult.Incomplete(
                    HostManagerMemoryCleanupRoundDisposition.BlockedByPriorUnknownOutcome,
                    originalCandidateCount,
                    blockedCandidateCount,
                    plan.StateRevision);
            }
            if (unknownCandidateCount != 0)
            {
                return HostManagerMemoryCleanupExecutionResult.Incomplete(
                    HostManagerMemoryCleanupRoundDisposition.FinalAdmissionCandidatesUnknown,
                    originalCandidateCount,
                    plannerStateRevision: plan.StateRevision);
            }
            if (finalAdmissionApplied && knownFilteredCandidateCount != 0)
            {
                return HostManagerMemoryCleanupExecutionResult.Incomplete(
                    HostManagerMemoryCleanupRoundDisposition.DeferredByCurrentFacts,
                    originalCandidateCount,
                    plannerStateRevision: plan.StateRevision);
            }
            return HostManagerMemoryCleanupExecutionResult.CompleteNoDecision(
                originalCandidateCount,
                plan.StateRevision);
        }

        var feedback = CreateUnattemptedMemoryCleanupFeedback(plan.Decisions);

        var succeededCount = 0;
        var admittedDecisionCount = 0;
        var terminalCount = 0;
        var settlementCompleted = false;
        HostManagerMemoryCleanupAttemptBatch batch = default;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!permit.TryReserveNewPointOfNoReturn(
                    checked((uint)plan.Decisions.Count),
                    out var effectReservation)
                || effectReservation is null)
            {
                return HostManagerMemoryCleanupExecutionResult.Incomplete(
                    HostManagerMemoryCleanupRoundDisposition.DeferredByCycleBudget,
                    originalCandidateCount,
                    blockedCandidateCount,
                    plan.StateRevision,
                    plan.Decisions.Count);
            }
            using var effectReservationScope = effectReservation;
            admittedDecisionCount = checked((int)effectReservation.Count);
            var admittedDecisions = plan.Decisions
                .Take(admittedDecisionCount)
                .ToArray();
            var validationIdentities = new HostManagerComputeProcessIdentity[
                admittedDecisions.Length];
            for (var index = 0; index < validationIdentities.Length; index++)
            {
                var candidate = admittedDecisions[index].Candidate;
                validationIdentities[index] = new(
                    candidate.ProcessId,
                    checked((ulong)candidate.ProcessStartedAt.ToFileTime()));
            }
            batch = new HostManagerMemoryCleanupAttemptBatch(
                Guid.NewGuid(),
                validationIdentities.ToHashSet(),
                attemptGeneration);
            if (!memoryCleanupValidationEvidence.TryReserve(
                    processEffectValidationScope,
                    cycleSequence,
                    batch.BatchId,
                    attemptGeneration,
                    plan.StateRevision,
                    validationFactContext,
                    request,
                    admittedDecisions,
                    out var evidenceReservation)
                || evidenceReservation is null)
            {
                return HostManagerMemoryCleanupExecutionResult.Incomplete(
                    disposition: HostManagerMemoryCleanupRoundDisposition
                        .ValidationEvidenceCapacityUnavailable,
                    candidateCount: originalCandidateCount,
                    blockedCandidateCount: blockedCandidateCount,
                    plannerStateRevision: plan.StateRevision,
                    plannedCount: plan.Decisions.Count,
                    requestedCount: admittedDecisionCount);
            }
            using var evidenceReservationScope = evidenceReservation;
            HostManagerProcessEffectValidationAdmissionPermit? validationPermit = null;
            if (!TryBeginProcessEffectBatchAdmission(
                    processEffectValidationScope,
                    HostManagerProcessEffectValidationFamily.AutomaticMemoryCleanup,
                    "decision-batch-pre-ponr",
                    validationIdentities,
                    out validationPermit))
            {
                return HostManagerMemoryCleanupExecutionResult.Incomplete(
                    HostManagerMemoryCleanupRoundDisposition.BlockedByValidationScope,
                    originalCandidateCount,
                    blockedCandidateCount,
                    plan.StateRevision,
                    plan.Decisions.Count);
            }
            if (!TryDeclareProcessEffectAdmission(
                    validationPermit
                        ?? throw new InvalidOperationException(
                            "A scoped memory-cleanup batch lost its validation permit."),
                    batch,
                    attemptGeneration))
            {
                return HostManagerMemoryCleanupExecutionResult.Incomplete(
                    disposition: HostManagerMemoryCleanupRoundDisposition
                        .BlockedByValidationScope,
                    candidateCount: originalCandidateCount,
                    blockedCandidateCount: blockedCandidateCount,
                    plannerStateRevision: plan.StateRevision,
                    plannedCount: plan.Decisions.Count,
                    requestedCount: admittedDecisionCount);
            }
            var requests = new ProcessResourcePolicyBatchRequest[admittedDecisionCount];
            for (var index = 0; index < requests.Length; index++)
            {
                var decision = admittedDecisions[index];
                requests[index] = new ProcessResourcePolicyBatchRequest(
                    decision.Candidate.ProcessId,
                    decision.Candidate.ProcessStartedAt,
                    TrimWorkingSet: true);
            }

            try
            {
                var preparedBatch = memoryCleanupAttemptJournal.PrepareBeforeWriter(
                    batch.BatchId,
                    attemptGeneration,
                    admittedDecisions);
                if (preparedBatch.BatchId != batch.BatchId
                    || preparedBatch.AttemptGeneration != attemptGeneration
                    || !preparedBatch.Processes.SetEquals(batch.Processes))
                {
                    throw new InvalidDataException(
                        "The memory-cleanup attempt journal returned a different declared batch.");
                }
            }
            catch (Exception exception) when (IsExpectedMemoryCleanupAttemptJournalFailure(exception))
            {
                MarkProcessEffectAdmissionUnsettled(
                    validationPermit,
                    "memory-cleanup-attempt-prepare-failed");
                logger.LogError(
                    exception,
                    "Automatic memory cleanup attempt-journal prepare failed closed. Reason: {Reason}",
                    reason);
                return HostManagerMemoryCleanupExecutionResult.Incomplete(
                    disposition: HostManagerMemoryCleanupRoundDisposition.JournalPrepareFailed,
                    candidateCount: originalCandidateCount,
                    blockedCandidateCount: blockedCandidateCount,
                    plannerStateRevision: plan.StateRevision,
                    plannedCount: plan.Decisions.Count);
            }
            if (!TryCommitProcessEffectAdmission(
                    validationPermit
                        ?? throw new InvalidOperationException(
                            "A scoped memory-cleanup batch lost its validation permit."),
                    batch,
                    attemptGeneration))
            {
                return HostManagerMemoryCleanupExecutionResult.Incomplete(
                    disposition: HostManagerMemoryCleanupRoundDisposition
                        .BlockedByValidationScope,
                    candidateCount: originalCandidateCount,
                    blockedCandidateCount: blockedCandidateCount,
                    plannerStateRevision: plan.StateRevision,
                    plannedCount: plan.Decisions.Count,
                    requestedCount: admittedDecisionCount);
            }

            try
            {
                memoryCleanupAttemptJournal.ArmForWriter(batch);
            }
            catch (Exception exception) when (
                IsExpectedMemoryCleanupAttemptJournalFailure(exception))
            {
                logger.LogError(
                    exception,
                    "Automatic memory cleanup writer arm failed closed. Reason: {Reason}",
                    reason);
                return HostManagerMemoryCleanupExecutionResult.Incomplete(
                    disposition: HostManagerMemoryCleanupRoundDisposition.JournalWriterArmFailed,
                    candidateCount: originalCandidateCount,
                    blockedCandidateCount: blockedCandidateCount,
                    plannerStateRevision: plan.StateRevision,
                    plannedCount: plan.Decisions.Count,
                    requestedCount: admittedDecisionCount);
            }

            IReadOnlyList<ProcessResourcePolicyBatchWriteResult> results;
            var writerCompletedAt = default(DateTimeOffset);
            var writerCompletedAtQpcTicks = 0L;
            try
            {
                if (!evidenceReservation.IsProductionUnscoped)
                {
                    evidenceReservation.MarkWriterStarted(
                        timeProvider.GetUtcNow(),
                        Stopwatch.GetTimestamp());
                }
                effectReservation.EnterPointOfNoReturn(effectReservation.Count);
                results = processPolicyWriter.TryApplyBatch(requests);
                if (!evidenceReservation.IsProductionUnscoped)
                {
                    writerCompletedAtQpcTicks = Stopwatch.GetTimestamp();
                    writerCompletedAt = timeProvider.GetUtcNow();
                }
            }
            catch (Exception exception)
            {
                if (!evidenceReservation.IsProductionUnscoped)
                {
                    writerCompletedAtQpcTicks = Stopwatch.GetTimestamp();
                    writerCompletedAt = timeProvider.GetUtcNow();
                }
                TryMarkMemoryCleanupBatchUnknown(batch, reason, exception);
                if (!evidenceReservation.IsProductionUnscoped)
                {
                    try
                    {
                        evidenceReservation.CompleteUnknown(
                            writerCompletedAt,
                            writerCompletedAtQpcTicks);
                    }
                    catch (Exception evidenceException) when (
                        evidenceException is InvalidDataException
                            or InvalidOperationException
                            or OverflowException)
                    {
                        logger.LogCritical(
                            evidenceException,
                            "Automatic memory cleanup validation evidence could not record the unknown native outcome. Reason: {Reason}",
                            reason);
                    }
                }
                for (var index = 0; index < admittedDecisionCount; index++)
                {
                    feedback[index] = feedback[index] with { Attempted = true };
                }
                logger.LogError(
                    exception,
                    "Automatic memory cleanup batch outcome is unknown. Reason: {Reason}",
                    reason);
                return HostManagerMemoryCleanupExecutionResult.Incomplete(
                    disposition: HostManagerMemoryCleanupRoundDisposition.BatchOutcomeUnknown,
                    candidateCount: originalCandidateCount,
                    blockedCandidateCount: blockedCandidateCount,
                    plannerStateRevision: plan.StateRevision,
                    plannedCount: plan.Decisions.Count,
                    requestedCount: admittedDecisionCount);
            }

            var terminalProcesses = new HashSet<HostManagerComputeProcessIdentity>();
            var resultCountIsExact = results.Count == admittedDecisionCount;
            var resultShapeIsExact = resultCountIsExact;
            var evidenceOutcomes = evidenceReservation.IsProductionUnscoped
                ? null
                : new HostManagerMemoryCleanupValidationActionOutcome[
                    admittedDecisionCount];
            for (var index = 0; index < admittedDecisionCount; index++)
            {
                feedback[index] = feedback[index] with { Attempted = true };
                var decision = admittedDecisions[index];
                var identity = CreateProcessIdentity(decision.Candidate);
                var batchResult = resultCountIsExact
                    ? results[index]
                    : null;
                var processIdentityMatches =
                    batchResult?.ProcessId == decision.Candidate.ProcessId;
                ProcessResourcePolicyBatchFieldResult? result = null;
                if (processIdentityMatches
                    && batchResult!.Fields is { Count: 1 })
                {
                    var candidateResult = batchResult.Fields[0];
                    if (candidateResult.Field ==
                            ProcessResourcePolicyBatchFields.TrimWorkingSet
                        && (candidateResult.Succeeded
                            ? candidateResult.ErrorCode == 0
                            : candidateResult.ErrorCode > 0))
                    {
                        result = candidateResult;
                    }
                }
                if (result is null)
                {
                    resultShapeIsExact = false;
                }
                if (result is not null)
                {
                    terminalProcesses.Add(identity);
                    terminalCount++;
                }
                var succeeded = result?.Succeeded == true;
                if (evidenceOutcomes is not null)
                {
                    evidenceOutcomes[index] = new(
                        ResultReturned: result is not null,
                        Succeeded: succeeded,
                        Win32Error: result?.ErrorCode);
                }
                feedback[index] = feedback[index] with { Succeeded = succeeded };
                if (succeeded) succeededCount++;
                logger.LogDebug(
                    "Automatic memory cleanup {Mode} trim {Target} pid {ProcessId}: {Message}",
                    admittedDecisions[index].Mode,
                    admittedDecisions[index].Candidate.DisplayName,
                    admittedDecisions[index].Candidate.ProcessId,
                    result?.Message ?? "No batch result.");
            }

            try
            {
                memoryCleanupAttemptJournal.Settle(batch, terminalProcesses);
                settlementCompleted = true;
            }
            catch (Exception exception) when (IsExpectedMemoryCleanupAttemptJournalFailure(exception))
            {
                logger.LogError(
                    exception,
                    "Automatic memory cleanup settlement could not be persisted; the durable attempt remains blocked. Reason: {Reason}",
                    reason);
            }
            if (evidenceOutcomes is not null)
            {
                try
                {
                    evidenceReservation.Complete(
                        evidenceOutcomes,
                        resultShapeIsExact,
                        settlementCompleted,
                        writerCompletedAt,
                        writerCompletedAtQpcTicks);
                }
                catch (Exception exception) when (exception is InvalidDataException
                    or InvalidOperationException
                    or OverflowException)
                {
                    logger.LogCritical(
                        exception,
                        "Automatic memory cleanup validation evidence could not be settled. Reason: {Reason}",
                        reason);
                    if (settlementCompleted)
                    {
                        return HostManagerMemoryCleanupExecutionResult.Incomplete(
                            disposition: HostManagerMemoryCleanupRoundDisposition
                                .ValidationEvidenceSettlementFailed,
                            candidateCount: originalCandidateCount,
                            blockedCandidateCount: blockedCandidateCount,
                            plannerStateRevision: plan.StateRevision,
                            plannedCount: plan.Decisions.Count,
                            requestedCount: admittedDecisionCount,
                            terminalCount: terminalCount,
                            succeededCount: succeededCount);
                    }
                }
            }
        }
        finally
        {
            automaticMemoryCleanupPlanner.Complete(feedback);
        }

        if (!settlementCompleted)
        {
            return HostManagerMemoryCleanupExecutionResult.Incomplete(
                disposition: HostManagerMemoryCleanupRoundDisposition.JournalSettlementFailed,
                candidateCount: originalCandidateCount,
                blockedCandidateCount: blockedCandidateCount,
                plannerStateRevision: plan.StateRevision,
                plannedCount: plan.Decisions.Count,
                requestedCount: admittedDecisionCount,
                terminalCount: terminalCount,
                succeededCount: succeededCount);
        }
        if (terminalCount != admittedDecisionCount)
        {
            return HostManagerMemoryCleanupExecutionResult.Incomplete(
                disposition: HostManagerMemoryCleanupRoundDisposition.BatchOutcomeUnknown,
                candidateCount: originalCandidateCount,
                blockedCandidateCount: blockedCandidateCount,
                plannerStateRevision: plan.StateRevision,
                plannedCount: plan.Decisions.Count,
                requestedCount: admittedDecisionCount,
                terminalCount: terminalCount,
                succeededCount: succeededCount);
        }
        if (blockedCandidateCount != 0)
        {
            return HostManagerMemoryCleanupExecutionResult.Incomplete(
                disposition: HostManagerMemoryCleanupRoundDisposition.BlockedByPriorUnknownOutcome,
                candidateCount: originalCandidateCount,
                blockedCandidateCount: blockedCandidateCount,
                plannerStateRevision: plan.StateRevision,
                plannedCount: plan.Decisions.Count,
                requestedCount: admittedDecisionCount,
                terminalCount: terminalCount,
                succeededCount: succeededCount);
        }
        if (unknownCandidateCount != 0)
        {
            return HostManagerMemoryCleanupExecutionResult.Incomplete(
                disposition: HostManagerMemoryCleanupRoundDisposition.FinalAdmissionCandidatesUnknown,
                candidateCount: originalCandidateCount,
                plannerStateRevision: plan.StateRevision,
                plannedCount: plan.Decisions.Count,
                requestedCount: admittedDecisionCount,
                terminalCount: terminalCount,
                succeededCount: succeededCount);
        }
        return admittedDecisionCount == plan.Decisions.Count
            ? HostManagerMemoryCleanupExecutionResult.CompleteKnownFull(
                originalCandidateCount,
                plan.StateRevision,
                admittedDecisionCount,
                succeededCount)
            : HostManagerMemoryCleanupExecutionResult.Incomplete(
                disposition: HostManagerMemoryCleanupRoundDisposition.DeferredByCycleBudget,
                candidateCount: originalCandidateCount,
                blockedCandidateCount: blockedCandidateCount,
                plannerStateRevision: plan.StateRevision,
                plannedCount: plan.Decisions.Count,
                requestedCount: admittedDecisionCount,
                terminalCount: terminalCount,
                succeededCount: succeededCount);
    }

    private void TryMarkMemoryCleanupBatchUnknown(
        HostManagerMemoryCleanupAttemptBatch batch,
        string reason,
        Exception batchException)
    {
        try
        {
            memoryCleanupAttemptJournal.MarkUnknown(batch);
        }
        catch (Exception journalException) when (
            IsExpectedMemoryCleanupAttemptJournalFailure(journalException))
        {
            logger.LogCritical(
                journalException,
                "Automatic memory cleanup could not persist an explicit unknown outcome after {BatchFailureType}. The pre-call Applying record remains authoritative. Reason: {Reason}",
                batchException.GetType().Name,
                reason);
        }
    }

    private static HostManagerComputeProcessIdentity CreateProcessIdentity(
        AutomaticMemoryCleanupCandidate candidate)
        => new(
            candidate.ProcessId,
            checked((ulong)candidate.ProcessStartedAt.ToFileTime()));

    private static bool IsExpectedMemoryCleanupAttemptJournalFailure(Exception exception)
        => exception is InvalidDataException
            or InvalidOperationException
            or IOException
            or UnauthorizedAccessException
            or System.Text.Json.JsonException;

    private static bool IsExpectedMemoryCleanupFinalAdmissionFailure(Exception exception)
        => exception is InvalidDataException
            or InvalidOperationException
            or IOException
            or UnauthorizedAccessException
            or DllNotFoundException
            or EntryPointNotFoundException
            or BadImageFormatException
            or TypeInitializationException
            or System.ComponentModel.Win32Exception;

    private static AutomaticMemoryCleanupFeedback[] CreateUnattemptedMemoryCleanupFeedback(
        IReadOnlyList<AutomaticMemoryCleanupDecision> decisions)
    {
        var feedback = new AutomaticMemoryCleanupFeedback[decisions.Count];
        for (var index = 0; index < feedback.Length; index++)
        {
            feedback[index] = new AutomaticMemoryCleanupFeedback(
                decisions[index].Reservation,
                Attempted: false,
                Succeeded: false);
        }
        return feedback;
    }

    private readonly record struct MemoryCleanupCandidateProjectionResult(
        bool IsComplete,
        IReadOnlyList<AutomaticMemoryCleanupCandidate> Candidates,
        IReadOnlyDictionary<HostManagerComputeProcessIdentity, HostManagerComputeScore> Scores,
        int UnknownCandidateCount)
    {
        internal static MemoryCleanupCandidateProjectionResult Invalid { get; } =
            new(
                false,
                [],
                new Dictionary<HostManagerComputeProcessIdentity, HostManagerComputeScore>(),
                UnknownCandidateCount: 0);
    }

    private readonly record struct MemoryCleanupTargetFactProjectionResult(
        bool IsComplete,
        IReadOnlyList<HostManagerMemoryCleanupTargetFact> Facts,
        int UnknownCandidateCount)
    {
        internal static MemoryCleanupTargetFactProjectionResult Invalid { get; } =
            new(false, [], UnknownCandidateCount: 0);
    }

    private readonly record struct MemoryCleanupCapacitySnapshot(
        double PhysicalMemoryFreeRatio,
        bool PhysicalMemoryFreeRatioCurrent,
        double VirtualMemoryFreeRatio,
        bool VirtualMemoryFreeRatioCurrent);

    private readonly record struct MemoryCleanupFinalAdmissionResult(
        bool IsComplete,
        HostManagerMemoryCleanupRoundDisposition Disposition,
        AutomaticMemoryCleanupPlanRequest Request,
        HostManagerMemoryCleanupValidationFactContext ValidationFactContext,
        int KnownFilteredCandidateCount,
        int UnknownCandidateCount)
    {
        internal static MemoryCleanupFinalAdmissionResult Incomplete(
            HostManagerMemoryCleanupRoundDisposition disposition)
            => new(
                false,
                disposition,
                Request: null!,
                ValidationFactContext: default,
                KnownFilteredCandidateCount: 0,
                UnknownCandidateCount: 0);

        internal static MemoryCleanupFinalAdmissionResult Complete(
            AutomaticMemoryCleanupPlanRequest request,
            HostManagerMemoryCleanupValidationFactContext validationFactContext,
            int knownFilteredCandidateCount,
            int unknownCandidateCount)
            => new(
                true,
                HostManagerMemoryCleanupRoundDisposition.Incomplete,
                request,
                validationFactContext,
                knownFilteredCandidateCount,
                unknownCandidateCount);
    }
}
