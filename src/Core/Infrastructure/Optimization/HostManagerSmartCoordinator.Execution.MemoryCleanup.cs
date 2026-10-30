using System.Diagnostics;
using ResourceManager.App.Application.Optimization;
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
        CancellationToken cancellationToken)
    {
        permit.Require(HostManagerCycleEffectKind.AutomaticMemoryCleanup);
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
            sample.RoundNumber);
        var previousAdmissionBaseline = memoryCleanupHostedAdmissionBaseline;
        if (previousAdmissionBaseline is null
            || !previousAdmissionBaseline.CanAdmit(currentAdmissionBaseline))
        {
            memoryCleanupHostedAdmissionBaseline = currentAdmissionBaseline;
            return HostManagerMemoryCleanupExecutionResult.NotRun(
                HostManagerMemoryCleanupRoundDisposition
                    .AwaitingNewSchedulingRound);
        }

        var candidateProjection = CreateMemoryCleanupCandidates(
            sample.Targets,
            compute,
            sample.RoundNumber);
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
            sample.RoundNumber,
            cycleSequence,
            "ordinary",
            async (currentRequest, finalAdmissionCancellationToken) =>
            {
                var admission = await CaptureMemoryCleanupFinalAdmissionAsync(
                    currentRequest,
                    candidateProjection.Scores,
                    sample.ProcessFacts,
                    sample.Hardware,
                    previousAdmissionBaseline.RoundNumber,
                    sample.RoundNumber,
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
        ulong roundNumber)
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
                factProjection.Facts,
                out var scores))
        {
            return MemoryCleanupCandidateProjectionResult.Invalid;
        }

        var candidates = HostManagerMemoryCleanupCandidateProjection.Create(
            factProjection.Facts,
            scores).Select(candidate => candidate with { RoundNumber = roundNumber }).ToArray();
        return candidates.Length == factProjection.Facts.Count
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

                var cleanupAllowed = target.CanApplyAutomaticPolicy
                    && !target.GpuPlacementPolicy.MemorySchedulingExcluded;
                if (cleanupAllowed && liveIdentityUnknown)
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
                    cleanupAllowed && exactLiveIdentity));
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
        HardwareMetricSnapshot hardware,
        ulong baselineRoundNumber,
        ulong roundNumber,
        CompiledHostManagerMemoryCleanupHotPublishPlan cleanupPlan,
        int initialUnknownCandidateCount,
        CancellationToken cancellationToken)
    {
        var processFacts = initialProcessFacts;
        if (!TryIndexCurrentMemoryCleanupProcessFacts(
                processFacts,
                hardware.Memory.TotalBytes,
                out var currentProcesses,
                out var maximumBaseScores))
        {
            return MemoryCleanupFinalAdmissionResult.Incomplete(
                HostManagerMemoryCleanupRoundDisposition.FinalAdmissionCandidateProjectionIncomplete);
        }
        var capacity = CreateMemoryCleanupCapacitySnapshot(hardware);
        var protectionLevels = await ResolveNativeProtectionLevelsAsync(cancellationToken);
        // Memory scheduling exclusion is resolved again for each candidate's current software, like
        // protection. The cycle's publication lease holds new plans back, so this is the plan the round
        // was planned with; an exclusion published during the round applies from the next round.
        var gpuPlacementPlan = runtimePlanProvider.Current.GpuPlacement;

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
                currentCandidates.Add(baseline with { CanApply = false });
                knownFilteredCount++;
                cycleFactExclusions.Add(new SchedulingFactExclusion(identity.ProcessId, identity.ProcessStartKey,
                    baseline.TargetId, SchedulingFactCode.CpuScore, SchedulingFactExclusionReason.CleanupBaselineScoreMissing, null));
                continue;
            }
            if (!currentProcesses.TryGetValue(identity, out var current))
            {
                knownFilteredCount++;
                cycleFactExclusions.Add(new SchedulingFactExclusion(identity.ProcessId, identity.ProcessStartKey,
                    baseline.TargetId, SchedulingFactCode.MemoryCleanup, SchedulingFactExclusionReason.CleanupCurrentProcessFactsMissing, null));
                continue;
            }

            var softwareKey = NativeStableIdentity.CreateCaseInsensitiveKey(current.SoftwareId);
            if (!maximumBaseScores.TryGetValue(softwareKey, out var maximumBaseScore))
            {
                currentCandidates.Add(baseline with { CanApply = false });
                knownFilteredCount++;
                cycleFactExclusions.Add(new SchedulingFactExclusion(current.ProcessId, current.ProcessStartKey,
                    baseline.TargetId, SchedulingFactCode.BaseScore, SchedulingFactExclusionReason.CleanupSoftwareMaximumBaseScoreMissing, null));
                continue;
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
                protectionLevel,
                gpuPlacementPlan.Resolve(current.SoftwareId, null, null, null).MemorySchedulingExcluded);
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
                    MemoryUsedPercent = Math.Clamp(current.MemoryUsedBytes * 100d / hardware.Memory.TotalBytes, 0, 100)
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
                baselineRoundNumber,
                roundNumber,
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
        SchedulingProcessFactSnapshot current,
        ulong totalMemoryBytes,
        out IReadOnlyDictionary<HostManagerComputeProcessIdentity, SchedulingProcessFact> processes,
        out IReadOnlyDictionary<ulong, double> maximumBaseScores)
    {
        processes = new Dictionary<HostManagerComputeProcessIdentity, SchedulingProcessFact>();
        maximumBaseScores = new Dictionary<ulong, double>();
        var requiredMask = SchedulingProcessMetricMask.MemoryUsage
            | SchedulingProcessMetricMask.RuntimeState;
        if (!current.IsInventoryCurrent()
            || !current.IsMemoryCurrentComplete()
            || totalMemoryBytes == 0
            || !current.IsRuntimeStateCurrentComplete()
            || (current.RequestedMetricMask & requiredMask) != requiredMask
            || (current.CurrentMetricMask & requiredMask) != requiredMask)
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
        var ambiguousAttribution = attributionProcesses.GroupBy(process => (process.ProcessId, process.ProcessStartKey))
            .Where(group => group.Count() > 1).Select(group => group.Key).ToHashSet();
        foreach (var process in attributionProcesses)
        {
            var softwareKey = NativeStableIdentity.CreateCaseInsensitiveKey(process.SoftwareId);
            if (process.ProcessId <= 0
                || process.ProcessStartKey == 0
                || ambiguousAttribution.Contains((process.ProcessId, process.ProcessStartKey))
                || string.IsNullOrWhiteSpace(process.ProcessName)
                || softwareKey == 0
                || !double.IsFinite(process.BaseScore)
                || process.BaseScore < 0)
            {
                continue;
            }
            maxima[softwareKey] = maxima.TryGetValue(softwareKey, out var currentMaximum)
                ? Math.Max(currentMaximum, process.BaseScore)
                : process.BaseScore;
        }
        var ambiguousProcesses = current.Processes.GroupBy(process => (process.ProcessId, process.ProcessStartKey))
            .Where(group => group.Count() > 1).Select(group => group.Key).ToHashSet();
        foreach (var process in current.Processes)
        {
            var identity = new HostManagerComputeProcessIdentity(
                process.ProcessId,
                process.ProcessStartKey);
            var softwareKey = NativeStableIdentity.CreateCaseInsensitiveKey(process.SoftwareId);
            if (process.ProcessId <= 0
                || process.ProcessStartKey == 0
                || ambiguousProcesses.Contains((process.ProcessId, process.ProcessStartKey))
                || string.IsNullOrWhiteSpace(process.ProcessName)
                || softwareKey == 0
                || !double.IsFinite(process.BaseScore)
                || process.BaseScore < 0)
            {
                continue;
            }
            var hasFinalAdmissionFacts = process.ValidMetricMask.HasFlag(
                    SchedulingProcessMetricMask.MemoryUsage)
                && process.ValidMetricMask.HasFlag(
                    SchedulingProcessMetricMask.RuntimeState);
            if (!hasFinalAdmissionFacts || !double.IsFinite(process.MemoryUsedBytes)
                || process.MemoryUsedBytes < 0 || !SchedulingProcessFactSnapshot.IsRuntimeStateValid(process))
            {
                continue;
            }
            indexed.Add(identity, process);
        }

        processes = indexed;
        maximumBaseScores = maxima;
        return true;
    }

    private sealed record MemoryCleanupHostedAdmissionBaseline(
        long RuntimePlanVersion,
        ulong CleanupConfigurationGeneration,
        ulong RoundNumber)
    {
        internal bool CanAdmit(MemoryCleanupHostedAdmissionBaseline current)
            => current.RuntimePlanVersion == RuntimePlanVersion
                && current.CleanupConfigurationGeneration ==
                    CleanupConfigurationGeneration
                && current.RoundNumber > RoundNumber;
    }

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
                    BaselineRoundNumber: 1,
                    RoundNumber: 2,
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
        if (attemptGeneration == 0)
        {
            return HostManagerMemoryCleanupExecutionResult.Incomplete(
                HostManagerMemoryCleanupRoundDisposition.MissingAttemptGeneration,
                originalCandidateCount);
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
                    originalCandidateCount);
            }
            if (!admission.IsComplete)
            {
                return HostManagerMemoryCleanupExecutionResult.Incomplete(
                    admission.Disposition,
                    originalCandidateCount);
            }

            request = admission.Request;
            validationFactContext = admission.ValidationFactContext;
            finalAdmissionApplied = true;
            knownFilteredCandidateCount = admission.KnownFilteredCandidateCount;
            unknownCandidateCount = admission.UnknownCandidateCount;
        }

        var maximumDecisionCount = Math.Min(
            request.MaximumDecisionCount,
            permit.NewPointOfNoReturnRemaining);
        if (maximumDecisionCount == 0)
        {
            return HostManagerMemoryCleanupExecutionResult.Incomplete(
                HostManagerMemoryCleanupRoundDisposition.DeferredByCycleBudget,
                originalCandidateCount);
        }
        request = request with { MaximumDecisionCount = maximumDecisionCount };

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
                originalCandidateCount);
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
                    plan.StateRevision);
            }

            var mismatchFeedback = CreateUnattemptedMemoryCleanupFeedback(plan.Decisions);
            try
            {
                return HostManagerMemoryCleanupExecutionResult.Incomplete(
                    HostManagerMemoryCleanupRoundDisposition.PlannerConfigurationMismatch,
                    originalCandidateCount,
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
            var batch = new HostManagerMemoryCleanupAttemptBatch(
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
                    plannerStateRevision: plan.StateRevision,
                    plannedCount: plan.Decisions.Count,
                    requestedCount: admittedDecisionCount);
            }

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

            if (evidenceOutcomes is not null)
            {
                try
                {
                    evidenceReservation.Complete(
                        evidenceOutcomes,
                        resultShapeIsExact,
                        true,
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
                    return HostManagerMemoryCleanupExecutionResult.Incomplete(
                        disposition: HostManagerMemoryCleanupRoundDisposition
                            .ValidationEvidenceSettlementFailed,
                        candidateCount: originalCandidateCount,
                        plannerStateRevision: plan.StateRevision,
                        plannedCount: plan.Decisions.Count,
                        requestedCount: admittedDecisionCount,
                        terminalCount: terminalCount,
                        succeededCount: succeededCount);
                }
            }
        }
        finally
        {
            automaticMemoryCleanupPlanner.Complete(feedback);
        }

        if (terminalCount != admittedDecisionCount)
        {
            return HostManagerMemoryCleanupExecutionResult.Incomplete(
                disposition: HostManagerMemoryCleanupRoundDisposition.BatchOutcomeUnknown,
                candidateCount: originalCandidateCount,
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
                plannerStateRevision: plan.StateRevision,
                plannedCount: plan.Decisions.Count,
                requestedCount: admittedDecisionCount,
                terminalCount: terminalCount,
                succeededCount: succeededCount);
    }

    private static HostManagerComputeProcessIdentity CreateProcessIdentity(
        AutomaticMemoryCleanupCandidate candidate)
        => new(
            candidate.ProcessId,
            checked((ulong)candidate.ProcessStartedAt.ToFileTime()));

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
