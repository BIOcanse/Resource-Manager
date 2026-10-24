using ResourceManager.App.Application.Optimization;
using ResourceManager.App.Domain.Adaptation.Scheduling;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.Monitoring;
using ResourceManager.App.Domain.Optimization;
using ResourceManager.App.Domain.Optimization.Scoring;
using ResourceManager.App.Domain.ResourceBreakdown;
using ResourceManager.App.Domain.Software;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.NativeCore;

namespace ResourceManager.App.Infrastructure.Optimization;

public sealed partial class HostManagerSmartCoordinator
{
    private async Task<IReadOnlyDictionary<string, int>> ResolveNativeProtectionLevelsAsync(
        CancellationToken cancellationToken)
    {
        var levels = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var protectedTargets =
            await protectionService.GetProtectedTargetsAsync(cancellationToken);
        foreach (var target in protectedTargets)
        {
            if (target.State != OptimizationProtectionStates.Active)
            {
                continue;
            }

            var level = OptimizationProtectionLevels.Normalize(target.ProtectionLevel);
            Add(target.TargetKey);
            Add(target.SoftwareId);

            void Add(string? key)
            {
                if (!string.IsNullOrWhiteSpace(key))
                {
                    levels[key] = Math.Max(level, levels.GetValueOrDefault(key));
                }
            }
        }

        return levels;
    }

    private static uint CountNativeFactRows(HostManagerSample sample, NativeAppliedOwnershipFactIndex ownership)
    {
        // Ownership records may need a standalone restore row. Counting all current
        // records also covers ones represented by a target without a second index.
        ulong rows = checked((ulong)ownership.ProcessRecords.Count + (ulong)ownership.AdapterRecords.Count);
        for (var targetIndex = 0; targetIndex < sample.Targets.Count; targetIndex++)
        {
            var target = sample.Targets[targetIndex];
            if (target.ProcessIds.Count != 1) continue;
            var processId = target.ProcessIds[0];
            if (processId <= 0 || !target.ProcessStartKeys.TryGetValue(processId, out var startKey) || startKey == 0) continue;
            // Identity plus a CPU metric/score row. Either raw CPU or canonical
            // CPU scoring can supply that row; the projection applies exclusions.
            rows = checked(rows + 2);
            for (var gpuIndex = 0; gpuIndex < target.Gpus.Count; gpuIndex++)
            {
                var gpu = target.Gpus[gpuIndex];
                if (gpu.HasUsageMetric && IsPercent(gpu.GpuUsagePercent) &&
                    TryResolveCurrentGpuIndex(sample, gpu, SchedulingProcessMetricMask.GpuUsage, out _)) rows++;
                if (gpu.HasVramMetric && IsPercent(gpu.VramUsedPercent) &&
                    TryResolveCurrentGpuIndex(sample, gpu, SchedulingProcessMetricMask.GpuDedicatedMemory, out _)) rows++;
            }
        }
        return checked((uint)rows);
    }

    private NativeFactProjectionResult ProjectNativeFacts(
        NativeSmartCoordinatorWorkspace workspace,
        HostManagerSample sample,
        NativeAppliedOwnershipFactIndex ownership,
        IReadOnlyDictionary<string, int> protectionLevels,
        CompiledOptimizationModePlan modePlan,
        bool policyExecutionEnabled,
        bool cpuPlacementEnabled,
        HostManagerComputeScoringCycleResult? computeScoring)
    {
        using var view = workspace.CreateScratchView();
        var inputRows = view.Inputs;
        var cursor = 0;
        var sourceIndex = 0U;
        var scoreProjection = CreateNativeComputeScoreProjection(computeScoring);
        var seenCpuProcessScores = new HashSet<NativeProcessScoreKey>();
        var seenGpuProcessScores = new HashSet<NativeGpuScoreKey>();
        var observedCpuSoftwareMembers = new Dictionary<ulong, uint>();
        var observedGpuSoftwareMembers = new Dictionary<NativeSoftwareGpuScoreKey, uint>();
        var projectedScoreRows = new List<(int Index, HostManagerComputeScore SoftwareScore)>();
        var reattributedSoftwareKeys = new HashSet<ulong>();
        foreach (var process in sample.ProcessFacts.Processes)
        {
            // Missing attribution says nothing about whether an owned process changed software.
            if (string.IsNullOrWhiteSpace(process.SoftwareId)) continue;
            var incarnation = new NativeAppliedOwnershipProcessIncarnation(
                checked((uint)process.ProcessId), process.ProcessStartKey);
            var currentKey = NativeStableIdentity.CreateCaseInsensitiveKey(process.SoftwareId);
            if (ownership.ProcessAnchors.TryGetValue(incarnation, out var anchor)
                && anchor.SoftwareKey != currentKey)
            {
                reattributedSoftwareKeys.Add(currentKey);
                reattributedSoftwareKeys.Add(anchor.SoftwareKey);
            }
        }
        // Missing decision facts can omit a still-live process; its absence here is not exit evidence.
        var representedProcesses = new HashSet<(int ProcessId, ulong ProcessStartKey)>();
        var seenProcessOwnership = new HashSet<NativeAppliedOwnershipProcessIdentity>();
        var seenAdapterOwnership = new HashSet<ulong>();
        foreach (var target in sample.Targets)
        {
            if (target.ProcessIds.Count != 1)
            {
                Exclude(null, null, target.TargetId, "identity", "target-process-count-not-one");
                continue;
            }
            var targetProcessId = target.ProcessIds[0];
            if (targetProcessId <= 0
                || !target.ProcessStartKeys.TryGetValue(targetProcessId, out var targetStartKey)
                || targetStartKey == 0)
            {
                Exclude(targetProcessId, null, target.TargetId, "identity", "target-process-identity-missing");
                continue;
            }
            if (!string.Equals(target.TargetId,
                HostManagerTargetIdentity.CreateProcessTargetId(targetProcessId, targetStartKey), StringComparison.Ordinal))
            {
                Exclude(targetProcessId, targetStartKey, target.TargetId, "identity", "target-process-identity-mismatch");
                continue;
            }
            var targetKey = NativeStableIdentity.CreateCaseInsensitiveKey(target.TargetId);
            var attributedSoftwareKey =
                NativeStableIdentity.CreateCaseInsensitiveKey(target.SoftwareId);
            var protectionLevel = protectionLevels.GetValueOrDefault(
                target.TargetId,
                protectionLevels.GetValueOrDefault(target.SoftwareId));

            foreach (var processId in target.ProcessIds)
            {
                var startKey = targetStartKey;
                representedProcesses.Add((processId, startKey));

                var incarnation = new NativeAppliedOwnershipProcessIncarnation(
                    checked((uint)processId),
                    startKey);
                var hasAnchor = ownership.ProcessAnchors.TryGetValue(
                    incarnation,
                    out var anchor);
                var softwareKey = hasAnchor
                    ? anchor.SoftwareKey
                    : attributedSoftwareKey;
                var scoreMembershipConsistent = !reattributedSoftwareKeys.Contains(softwareKey)
                    && !reattributedSoftwareKeys.Contains(attributedSoftwareKey);
                if (!scoreMembershipConsistent)
                {
                    Exclude(processId, startKey, target.TargetId, "compute-score", "ownership-software-attribution-mismatch");
                }
                var hasAdapterRecord = ownership.AdapterRecords.TryGetValue(
                    softwareKey,
                    out var adapterRecord);
                var adapterGrades = adapterRecord.CurrentGrades;
                var cpuOwned = hasAdapterRecord
                    && (adapterGrades.ValidMask &
                        (uint)NativeAppliedOwnershipGradeValidity.Cpu) != 0;
                var gpuOwned = hasAdapterRecord
                    && (adapterGrades.ValidMask &
                        (uint)NativeAppliedOwnershipGradeValidity.Gpu) != 0;
                var cpuGrade = cpuOwned
                    ? ToNativeAdapterGrade(adapterGrades.CpuGrade)
                    : NativeSmartCoordinatorAdapterGrade.Normal;
                var gpuGrade = gpuOwned
                    ? ToNativeAdapterGrade(adapterGrades.GpuGrade)
                    : NativeSmartCoordinatorAdapterGrade.Normal;
                var processIdentity = new NativeAppliedOwnershipProcessIdentity(
                    targetKey,
                    softwareKey,
                    checked((uint)processId),
                    startKey);
                var hasProcessRecord = ownership.ProcessRecords.TryGetValue(
                    processIdentity,
                    out var processRecord);
                if (hasProcessRecord)
                {
                    seenProcessOwnership.Add(processIdentity);
                }
                var processGrade = hasProcessRecord
                    ? ToNativeProcessGrade(processRecord.CurrentGrades.ProcessGrade)
                    : NativeSmartCoordinatorProcessGrade.Normal;
                var processOwned = hasProcessRecord;
                var processScoreKey = new NativeProcessScoreKey(targetKey, processId, startKey);
                var processCpuMetricsComplete = scoreProjection.ProcessCpu.TryGetValue(processScoreKey, out var cpuScore);
                HostManagerComputeScore? cpuSoftwareScore = null;
                if (processCpuMetricsComplete
                    && (cpuScore!.SoftwareKey != attributedSoftwareKey
                        || !seenCpuProcessScores.Add(processScoreKey)))
                {
                    Exclude(processId, startKey, target.TargetId, "cpu-score", "canonical-process-membership-mismatch");
                    processCpuMetricsComplete = false;
                }
                if (processCpuMetricsComplete
                    && !scoreProjection.SoftwareCpu.TryGetValue(attributedSoftwareKey, out cpuSoftwareScore))
                {
                    Exclude(processId, startKey, target.TargetId, "cpu-score", "canonical-software-score-missing");
                }
                var rawCpuAvailable = target.HasCpuMetric && IsPercent(target.CpuUsagePercent);
                var processGpuMetricsComplete = IsProcessGpuMetricsComplete(
                    sample.ProcessFacts,
                    sample.Hardware.GpuInventory,
                    target.GpuIdentityConsistent,
                    target.Gpus);

                var identity = CreateNativeIdentityFact(
                    target,
                    softwareKey,
                    processId,
                    startKey,
                    sourceIndex++,
                    checked((byte)protectionLevel),
                    processGrade,
                    cpuGrade,
                    gpuGrade,
                    processOwned,
                    cpuOwned,
                    gpuOwned,
                    policyExecutionEnabled
                        && scoreMembershipConsistent
                        && CanExecuteAutomaticProcessPolicyForMode(modePlan, target),
                    policyExecutionEnabled
                        && scoreMembershipConsistent
                        && CanExecuteSoftwareLevelAdapterModesForMode(modePlan, target),
                    cpuPlacementEnabled
                        && scoreMembershipConsistent
                        && target.CanApplyPhysicalCorePlacement,
                    processCpuMetricsComplete && scoreMembershipConsistent,
                    processGpuMetricsComplete && scoreMembershipConsistent,
                    modePlan.CpuSchedulingEnabled && !target.GpuPlacementPolicy.CpuSchedulingExcluded,
                    modePlan.GpuSchedulingEnabled && !target.GpuPlacementPolicy.GpuSchedulingExcluded);
                Append(inputRows, identity);
                if (hasAdapterRecord)
                {
                    seenAdapterOwnership.Add(softwareKey);
                }
                if (processCpuMetricsComplete)
                {
                    IncrementMemberCount(observedCpuSoftwareMembers, attributedSoftwareKey);
                    // Historical ownership must not receive another software's current score.
                    if (scoreMembershipConsistent || rawCpuAvailable)
                    {
                        AppendMetric(inputRows, identity, NativeSmartCoordinatorMetricKind.CpuUsagePercent,
                            rawCpuAvailable ? target.CpuUsagePercent : null, 0,
                            scoreMembershipConsistent ? cpuScore : null,
                            scoreMembershipConsistent ? cpuSoftwareScore : null);
                    }
                }
                else if (rawCpuAvailable)
                {
                    AppendMetric(inputRows, identity, NativeSmartCoordinatorMetricKind.CpuUsagePercent, target.CpuUsagePercent, 0);
                }
                foreach (var gpu in target.Gpus)
                {
                    if (gpu.HasUsageMetric
                        && IsPercent(gpu.GpuUsagePercent)
                        && TryResolveCurrentGpuIndex(
                            sample,
                            gpu,
                            SchedulingProcessMetricMask.GpuUsage,
                            out var usageGpuIndex))
                    {
                        if (scoreProjection.HasGpu)
                        {
                            var gpuProcessScoreKey = new NativeGpuScoreKey(
                                targetKey,
                                processId,
                                startKey,
                                gpu.AdapterKey);
                            var softwareScoreKey = new NativeSoftwareGpuScoreKey(
                                attributedSoftwareKey,
                                gpu.AdapterKey);
                            scoreProjection.SoftwareGpu.TryGetValue(softwareScoreKey, out var softwareScore);
                            if (!scoreProjection.ProcessGpu.TryGetValue(
                                    gpuProcessScoreKey,
                                    out var processScore)
                                || processScore.SoftwareKey != attributedSoftwareKey
                                || !seenGpuProcessScores.Add(gpuProcessScoreKey))
                            {
                                Exclude(processId, startKey, target.TargetId, "gpu-score", "canonical-process-membership-mismatch");
                                // Raw usage and VRAM remain independent of the score filter.
                                AppendMetric(inputRows, identity, NativeSmartCoordinatorMetricKind.GpuUsagePercent,
                                    gpu.GpuUsagePercent, usageGpuIndex);
                            }
                            else
                            {
                                if (softwareScore is null)
                                {
                                    Exclude(processId, startKey, target.TargetId, "gpu-score", "canonical-software-score-missing");
                                }
                                IncrementMemberCount(observedGpuSoftwareMembers, softwareScoreKey);
                                AppendMetric(inputRows,
                                    identity,
                                    NativeSmartCoordinatorMetricKind.GpuUsagePercent,
                                    gpu.GpuUsagePercent,
                                    usageGpuIndex,
                                    scoreMembershipConsistent ? processScore : null,
                                    scoreMembershipConsistent ? softwareScore : null);
                            }
                        }
                        else
                        {
                            AppendMetric(inputRows, 
                                identity,
                                NativeSmartCoordinatorMetricKind.GpuUsagePercent,
                                gpu.GpuUsagePercent,
                                usageGpuIndex);
                        }
                    }
                    if (gpu.HasVramMetric
                        && IsPercent(gpu.VramUsedPercent)
                        && TryResolveCurrentGpuIndex(
                            sample,
                            gpu,
                            SchedulingProcessMetricMask.GpuDedicatedMemory,
                            out var vramGpuIndex))
                    {
                        AppendMetric(inputRows, identity, NativeSmartCoordinatorMetricKind.VramUsagePercent, gpu.VramUsedPercent, vramGpuIndex);
                    }
                }
            }
        }

        ExcludeUnprojectedComputeScores(
            inputRows,
            projectedScoreRows,
            scoreProjection,
            seenCpuProcessScores,
            seenGpuProcessScores,
            observedCpuSoftwareMembers,
            observedGpuSoftwareMembers,
            cycleFactExclusions);

        foreach (var pair in ownership.ProcessRecords
            .Where(pair => !seenProcessOwnership.Contains(pair.Key))
            .OrderBy(static pair => pair.Key.ProcessId)
            .ThenBy(static pair => pair.Key.ProcessStartKey)
            .ThenBy(static pair => pair.Key.TargetKey)
            .ThenBy(static pair => pair.Key.SoftwareKey))
        {
            var record = pair.Value;
            Append(inputRows, CreateNativeStandaloneProcessRestoreFact(
                pair.Key,
                in record,
                sourceIndex++));
            seenProcessOwnership.Add(pair.Key);
        }

        foreach (var pair in ownership.AdapterRecords
            .Where(pair => !seenAdapterOwnership.Contains(pair.Key))
            .OrderBy(static pair => pair.Key))
        {
            var record = pair.Value;
            Append(inputRows, CreateNativeStandaloneAdapterFact(
                pair.Key,
                in record,
                sourceIndex++));
            seenAdapterOwnership.Add(pair.Key);
        }

        var allNativeOwnershipRepresented = seenProcessOwnership.Count == ownership.ProcessRecords.Count
            && seenAdapterOwnership.Count == ownership.AdapterRecords.Count;

        Volatile.Read(ref transitionProbe)?.ObserveNativeInputs(inputRows[..cursor]);
        return new NativeFactProjectionResult(
            checked((uint)cursor),
            sample.ProcessFacts.IsInventoryCurrentComplete()
                && representedProcesses.SetEquals(sample.ProcessFacts.CurrentInventoryProcesses
                    .Select(static process => (process.ProcessId, process.ProcessStartKey))),
            allNativeOwnershipRepresented);

        void AppendMetric(
            Span<NativeSmartCoordinatorInputRow> outputRows,
            NativeSmartCoordinatorInputRow identity,
            NativeSmartCoordinatorMetricKind kind,
            double? value,
            uint deviceIndex,
            HostManagerComputeScore? processScore = null,
            HostManagerComputeScore? softwareScore = null)
        {
            identity.MetricKind = kind;
            identity.MetricValue = value.GetValueOrDefault();
            identity.DeviceIndex = deviceIndex;
            identity.SourceIndex = sourceIndex++;
            if (value.HasValue) identity.ValidMask |= NativeSmartCoordinatorInputValidity.Metric;
            if (processScore is not null)
            {
                if (processScore.MemberCount != 1
                    || !double.IsFinite(processScore.Score)
                    || processScore.Score < 0)
                {
                    throw new InvalidDataException(
                        "The canonical score row cannot be projected into the native ABI.");
                }
                identity.ValidMask |= NativeSmartCoordinatorInputValidity.ProcessScore;
                identity.ProcessScore = processScore.Score;
            }
            if (softwareScore is not null)
            {
                if (softwareScore.MemberCount == 0
                    || !double.IsFinite(softwareScore.Score)
                    || softwareScore.Score < 0)
                {
                    throw new InvalidDataException(
                        "The canonical score row cannot be projected into the native ABI.");
                }
                identity.ValidMask |= NativeSmartCoordinatorInputValidity.SoftwareScore
                    | NativeSmartCoordinatorInputValidity.ScoreMemberCount;
                identity.SoftwareScore = softwareScore.Score;
                identity.ScoreMemberCount = softwareScore.MemberCount;
                projectedScoreRows.Add((cursor, softwareScore));
            }
            Append(outputRows, identity);
        }

        void Append(Span<NativeSmartCoordinatorInputRow> outputRows, NativeSmartCoordinatorInputRow row)
        {
            if (cursor >= outputRows.Length)
            {
                throw new InvalidDataException(
                    $"The Host Manager smart coordinator input capacity {outputRows.Length} is smaller than the projected fact count.");
            }

            outputRows[cursor++] = row;
        }

        void Exclude(int? processId, ulong? startKey, string targetId, string fact, string reason)
            => cycleFactExclusions.Add(new SchedulingFactExclusion(processId, startKey, targetId, fact, reason));
    }

    private static NativeSmartCoordinatorInputRow CreateNativeIdentityFact(
        HostManagerTargetInfo target,
        ulong softwareKey,
        int processId,
        ulong processStartKey,
        uint sourceIndex,
        byte protectionLevel,
        NativeSmartCoordinatorProcessGrade processGrade,
        NativeSmartCoordinatorAdapterGrade cpuGrade,
        NativeSmartCoordinatorAdapterGrade gpuGrade,
        bool processOwned,
        bool cpuOwned,
        bool gpuOwned,
        bool canApplyProcessPolicy,
        bool canApplyAdapterPolicy,
        bool hardwareSchedulingEligible,
        bool processCpuMetricsComplete,
        bool processGpuMetricsComplete,
        bool cpuSchedulingEnabled,
        bool gpuSchedulingEnabled)
    {
        var flags = NativeSmartCoordinatorInputFlags.Running;
        if (target.ForegroundFocused)
        {
            flags |= NativeSmartCoordinatorInputFlags.ForegroundFocused;
        }
        if (target.HasVisibleWindow)
        {
            flags |= NativeSmartCoordinatorInputFlags.HasVisibleWindow;
        }
        if (target.HasBackgroundWindow)
        {
            flags |= NativeSmartCoordinatorInputFlags.HasBackgroundWindow;
        }
        if (target.HasHiddenWindow)
        {
            flags |= NativeSmartCoordinatorInputFlags.HasHiddenWindow;
        }
        if (processCpuMetricsComplete)
        {
            flags |= NativeSmartCoordinatorInputFlags.ProcessCpuMetricsComplete;
        }
        if (processGpuMetricsComplete)
        {
            flags |= NativeSmartCoordinatorInputFlags.ProcessGpuMetricsComplete;
        }
        if (canApplyProcessPolicy)
        {
            flags |= NativeSmartCoordinatorInputFlags.CanApplyProcessPolicy;
        }
        if (canApplyAdapterPolicy)
        {
            flags |= NativeSmartCoordinatorInputFlags.CanApplyAdapterPolicy;
        }
        if (hardwareSchedulingEligible)
        {
            flags |= NativeSmartCoordinatorInputFlags.HardwareSchedulingEligible;
        }
        if (processOwned)
        {
            flags |= NativeSmartCoordinatorInputFlags.OwnsProcessGrade;
        }
        if (cpuOwned)
        {
            flags |= NativeSmartCoordinatorInputFlags.OwnsCpuGrade;
        }
        if (gpuOwned)
        {
            flags |= NativeSmartCoordinatorInputFlags.OwnsGpuGrade;
        }

        return new NativeSmartCoordinatorInputRow
        {
            StructSize = checked((uint)NativeSmartCoordinatorSession.SizeOf<NativeSmartCoordinatorInputRow>()),
            MetricKind = NativeSmartCoordinatorMetricKind.None,
            SoftwareKind = ToNativeSoftwareKind(target.SoftwareKind),
            ProtectionLevel = protectionLevel,
            ValidMask = NativeSmartCoordinatorInputValidity.ProcessIdentity
                | NativeSmartCoordinatorInputValidity.SoftwareIdentity
                | NativeSmartCoordinatorInputValidity.SoftwareKind
                | NativeSmartCoordinatorInputValidity.BaseScore
                | NativeSmartCoordinatorInputValidity.SurfaceFacts
                | NativeSmartCoordinatorInputValidity.Eligibility
                | NativeSmartCoordinatorInputValidity.Protection
                | NativeSmartCoordinatorInputValidity.CpuCapabilities
                | NativeSmartCoordinatorInputValidity.GpuCapabilities
                | NativeSmartCoordinatorInputValidity.AppliedProcessGrade
                | NativeSmartCoordinatorInputValidity.AppliedCpuGrade
                | NativeSmartCoordinatorInputValidity.AppliedGpuGrade,
            Flags = flags,
            TargetKey = NativeStableIdentity.CreateCaseInsensitiveKey(target.TargetId),
            SoftwareKey = softwareKey,
            ProcessStartKey = processStartKey,
            BaseScore = target.BaseScore,
            SourceIndex = sourceIndex,
            ProcessId = checked((uint)processId),
            CpuCapabilityMask = cpuSchedulingEnabled ? CreateCapabilityMask(target.SupportedCpuGrades) : (byte)0,
            GpuCapabilityMask = gpuSchedulingEnabled ? CreateCapabilityMask(target.SupportedGpuGrades) : (byte)0,
            AppliedProcessGrade = processGrade,
            AppliedCpuGrade = cpuGrade,
            AppliedGpuGrade = gpuGrade
        };
    }

    internal static NativeSmartCoordinatorInputRow CreateNativeStandaloneAdapterFact(
        ulong softwareKey,
        in NativeAppliedOwnershipRecord record,
        uint sourceIndex)
    {
        if (softwareKey == 0
            || record.Primary.Scope != (uint)NativeAppliedOwnershipScope.Adapter
            || record.Primary.SoftwareId != softwareKey)
        {
            throw new InvalidDataException(
                "The Host Manager applied ownership record cannot be projected as an authoritative software fact.");
        }

        var grades = record.CurrentGrades;
        var cpuOwned = (grades.ValidMask & (uint)NativeAppliedOwnershipGradeValidity.Cpu) != 0;
        var gpuOwned = (grades.ValidMask & (uint)NativeAppliedOwnershipGradeValidity.Gpu) != 0;
        var flags = NativeSmartCoordinatorInputFlags.None;
        if (cpuOwned)
        {
            flags |= NativeSmartCoordinatorInputFlags.OwnsCpuGrade;
        }
        if (gpuOwned)
        {
            flags |= NativeSmartCoordinatorInputFlags.OwnsGpuGrade;
        }

        return new NativeSmartCoordinatorInputRow
        {
            StructSize = checked((uint)NativeSmartCoordinatorSession.SizeOf<NativeSmartCoordinatorInputRow>()),
            ValidMask = NativeSmartCoordinatorInputValidity.SoftwareIdentity
                | NativeSmartCoordinatorInputValidity.SurfaceFacts
                | NativeSmartCoordinatorInputValidity.AppliedCpuGrade
                | NativeSmartCoordinatorInputValidity.AppliedGpuGrade,
            Flags = flags,
            SoftwareKey = softwareKey,
            SourceIndex = sourceIndex,
            AppliedProcessGrade = NativeSmartCoordinatorProcessGrade.Normal,
            AppliedCpuGrade = cpuOwned
                ? ToNativeAdapterGrade(grades.CpuGrade)
                : NativeSmartCoordinatorAdapterGrade.Normal,
            AppliedGpuGrade = gpuOwned
                ? ToNativeAdapterGrade(grades.GpuGrade)
                : NativeSmartCoordinatorAdapterGrade.Normal
        };
    }

    internal static NativeSmartCoordinatorInputRow CreateNativeStandaloneProcessRestoreFact(
        NativeAppliedOwnershipProcessIdentity identity,
        in NativeAppliedOwnershipRecord record,
        uint sourceIndex)
    {
        if (identity.TargetKey == 0
            || identity.ProcessId == 0
            || identity.ProcessStartKey == 0
            || record.Primary.Scope != (uint)NativeAppliedOwnershipScope.Process
            || record.Primary.TargetId != identity.TargetKey
            || record.Primary.SoftwareId != identity.SoftwareKey
            || record.Primary.ProcessId != identity.ProcessId
            || record.Primary.ProcessStartKey != identity.ProcessStartKey
            || record.CurrentGrades.ValidMask !=
                (uint)NativeAppliedOwnershipGradeValidity.Process)
        {
            throw new InvalidDataException(
                "The Host Manager applied ownership record cannot be projected as an absent-process restore fact.");
        }

        return new NativeSmartCoordinatorInputRow
        {
            StructSize = checked((uint)NativeSmartCoordinatorSession.SizeOf<NativeSmartCoordinatorInputRow>()),
            ValidMask = NativeSmartCoordinatorInputValidity.ProcessIdentity
                | NativeSmartCoordinatorInputValidity.SoftwareIdentity
                | NativeSmartCoordinatorInputValidity.SurfaceFacts
                | NativeSmartCoordinatorInputValidity.AppliedProcessGrade
                | NativeSmartCoordinatorInputValidity.AppliedCpuGrade
                | NativeSmartCoordinatorInputValidity.AppliedGpuGrade,
            Flags = NativeSmartCoordinatorInputFlags.OwnsProcessGrade,
            TargetKey = identity.TargetKey,
            SoftwareKey = identity.SoftwareKey,
            ProcessStartKey = identity.ProcessStartKey,
            SourceIndex = sourceIndex,
            ProcessId = identity.ProcessId,
            AppliedProcessGrade = ToNativeProcessGrade(
                record.CurrentGrades.ProcessGrade),
            AppliedCpuGrade = NativeSmartCoordinatorAdapterGrade.Normal,
            AppliedGpuGrade = NativeSmartCoordinatorAdapterGrade.Normal
        };
    }

    internal static NativeAppliedOwnershipFactIndex CreateAppliedOwnershipFactIndex(
        NativeAppliedOwnershipSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ValidateAppliedOwnershipSnapshotHeader(snapshot.Header, snapshot.Records.Count);

        var processes = new Dictionary<NativeAppliedOwnershipProcessIdentity, NativeAppliedOwnershipRecord>();
        var memoryProcesses = new Dictionary<NativeAppliedOwnershipProcessIdentity, NativeAppliedOwnershipRecord>();
        var adapters = new Dictionary<ulong, NativeAppliedOwnershipRecord>();
        var processAnchors = new Dictionary<
            NativeAppliedOwnershipProcessIncarnation,
            NativeAppliedOwnershipProcessAnchor>();
        foreach (var record in snapshot.Records)
        {
            ValidateAppliedOwnershipRecord(in record);
            switch ((NativeAppliedOwnershipScope)record.Primary.Scope)
            {
                case NativeAppliedOwnershipScope.Process:
                    {
                        var identity = new NativeAppliedOwnershipProcessIdentity(
                            record.Primary.TargetId,
                            record.Primary.SoftwareId,
                            record.Primary.ProcessId,
                            record.Primary.ProcessStartKey);
                        var memoryPolicy = IsMemoryPolicyOwnershipIdentity(identity);
                        var destination = memoryPolicy
                            ? memoryProcesses
                            : processes;
                        if (!destination.TryAdd(identity, record))
                        {
                            throw new InvalidDataException(
                                "The Host Manager applied ownership snapshot contains a duplicate process identity.");
                        }
                        AddProcessAnchor(
                            processAnchors,
                            identity,
                            memoryPolicy);
                        break;
                    }
                case NativeAppliedOwnershipScope.Adapter:
                    if (!adapters.TryAdd(record.Primary.SoftwareId, record))
                    {
                        throw new InvalidDataException(
                            "The Host Manager applied ownership snapshot contains a duplicate adapter identity.");
                    }
                    break;
                default:
                    throw new InvalidDataException(
                        $"Unknown Host Manager applied ownership scope {record.Primary.Scope}.");
            }
        }

        return new NativeAppliedOwnershipFactIndex(
            snapshot.Header.LedgerRevision,
            processes,
            memoryProcesses,
            adapters,
            processAnchors);
    }

    private static void AddProcessAnchor(
        IDictionary<NativeAppliedOwnershipProcessIncarnation, NativeAppliedOwnershipProcessAnchor>
            anchors,
        NativeAppliedOwnershipProcessIdentity identity,
        bool memoryPolicy)
    {
        var incarnation = new NativeAppliedOwnershipProcessIncarnation(
            identity.ProcessId,
            identity.ProcessStartKey);
        if (!anchors.TryGetValue(incarnation, out var current))
        {
            anchors.Add(
                incarnation,
                new NativeAppliedOwnershipProcessAnchor(
                    identity.SoftwareKey,
                    HasProcessOwnership: !memoryPolicy,
                    HasMemoryOwnership: memoryPolicy));
            return;
        }

        if (current.SoftwareKey != identity.SoftwareKey)
        {
            throw new InvalidDataException(
                "The Host Manager applied process and memory ownership records disagree on their process-incarnation software key.");
        }
        if (memoryPolicy ? current.HasMemoryOwnership : current.HasProcessOwnership)
        {
            throw new InvalidDataException(
                "The Host Manager applied ownership snapshot contains multiple records for one process-incarnation domain.");
        }

        anchors[incarnation] = current with
        {
            HasProcessOwnership = current.HasProcessOwnership || !memoryPolicy,
            HasMemoryOwnership = current.HasMemoryOwnership || memoryPolicy
        };
    }

    private static NativeAppliedOwnershipProcessIdentity CreateMemoryPolicyOwnershipIdentity(
        ulong softwareKey,
        uint processId,
        ulong processStartKey)
        => new(
            NativeStableIdentity.CreateCaseInsensitiveKey(
                HostManagerTargetIdentity.CreateProcessMemoryPolicyTargetId(
                    checked((int)processId),
                    processStartKey)),
            softwareKey,
            processId,
            processStartKey);

    private static bool IsMemoryPolicyOwnershipIdentity(
        NativeAppliedOwnershipProcessIdentity identity)
        => identity == CreateMemoryPolicyOwnershipIdentity(
            identity.SoftwareKey,
            identity.ProcessId,
            identity.ProcessStartKey);

    private static NativeSmartCoordinatorProcessGrade ToNativeProcessGrade(int grade)
        => grade switch
        {
            (int)NativeAppliedOwnershipProcessGrade.A1 => NativeSmartCoordinatorProcessGrade.A1,
            (int)NativeAppliedOwnershipProcessGrade.Level1 => NativeSmartCoordinatorProcessGrade.Level1,
            (int)NativeAppliedOwnershipProcessGrade.Level2 => NativeSmartCoordinatorProcessGrade.Level2,
            (int)NativeAppliedOwnershipProcessGrade.Level3 => NativeSmartCoordinatorProcessGrade.Level3,
            (int)NativeAppliedOwnershipProcessGrade.Level4 => NativeSmartCoordinatorProcessGrade.Level4,
            _ => throw new InvalidDataException($"Unknown Host Manager applied process grade {grade}.")
        };

    private static NativeSmartCoordinatorAdapterGrade ToNativeAdapterGrade(int grade)
        => grade switch
        {
            (int)NativeAppliedOwnershipAdapterGrade.Freeze => NativeSmartCoordinatorAdapterGrade.Freeze,
            (int)NativeAppliedOwnershipAdapterGrade.Optimize => NativeSmartCoordinatorAdapterGrade.Optimize,
            (int)NativeAppliedOwnershipAdapterGrade.Normal => NativeSmartCoordinatorAdapterGrade.Normal,
            (int)NativeAppliedOwnershipAdapterGrade.Extreme => NativeSmartCoordinatorAdapterGrade.Extreme,
            _ => throw new InvalidDataException($"Unknown Host Manager applied adapter grade {grade}.")
        };

    private static unsafe void ValidateAppliedOwnershipSnapshotHeader(
        in NativeAppliedOwnershipSnapshotHeader header,
        int recordCount)
    {
        var headerCopy = header;
        var reservedIsZero = true;
        for (var index = 0; index < 4; index++)
        {
            reservedIsZero &= headerCopy.Reserved[index] == 0;
        }

        if (header.AbiVersion != NativeAppliedOwnershipAbi.Version
            || header.StructSize != NativeAppliedOwnershipAbi.SnapshotHeaderSize
            || header.LedgerRevision == 0
            || (header.LedgerInstanceLow == 0 && header.LedgerInstanceHigh == 0)
            || header.EntryCount != checked((uint)recordCount)
            || header.EntryCount > header.RecordCapacity
            || header.RecordCapacity == 0
            || header.PrimaryIndexCapacity == 0
            || header.PayloadIndexCapacity == 0
            || header.MaximumImageBytes == 0
            || !reservedIsZero)
        {
            throw new InvalidDataException(
                "The Host Manager applied ownership snapshot header is not canonical.");
        }
    }

    private static unsafe void ValidateAppliedOwnershipRecord(
        in NativeAppliedOwnershipRecord record)
    {
        var recordCopy = record;
        var reservedIsZero = true;
        for (var index = 0; index < 4; index++)
        {
            reservedIsZero &= recordCopy.Reserved[index] == 0;
        }

        var primary = record.Primary;
        var binding = record.OriginalBinding;
        var action = binding.ActionIdentity;
        var grades = record.CurrentGrades;
        if (primary.ReservedUInt32 != 0
            || primary.ProcessReserved != 0
            || action.Reserved != 0
            || binding.RecoveryReserved != 0
            || grades.ReservedUInt32 != 0
            || grades.ReservedInt32 != 0
            || record.Flags != 0
            || record.ReservedUInt32 != 0
            || !reservedIsZero)
        {
            throw new InvalidDataException(
                "The Host Manager applied ownership record contains non-zero reserved data.");
        }

        if ((binding.JournalInstanceLow == 0 && binding.JournalInstanceHigh == 0)
            || binding.Disposition != (uint)NativeAppliedOwnershipDisposition.Apply
            || binding.MaximumRecoveryAttempts == 0
            || binding.RecoveryDeadlineUtcMilliseconds == 0
            || !IsCanonicalAtomicGroup(
                binding.AtomicGroupId,
                binding.GroupMemberIndex,
                binding.GroupMemberCount)
            || action.ConfigurationGeneration == 0
            || action.PlanEpoch == 0
            || action.ActionId == 0
            || action.HostSessionIncarnation == 0
            || action.TargetId == 0
            || record.Payload.Slot == 0
            || record.Payload.Generation == 0
            || record.Payload.Length == 0
            || record.Payload.DigestLow == 0
            || record.Payload.DigestHigh == 0
            || record.RecordRevision == 0
            || record.PromotedAtUtcMilliseconds == 0
            || record.UpdatedAtUtcMilliseconds < record.PromotedAtUtcMilliseconds)
        {
            throw new InvalidDataException(
                "The Host Manager applied ownership record is not canonical.");
        }

        switch ((NativeAppliedOwnershipScope)primary.Scope)
        {
            case NativeAppliedOwnershipScope.Process:
                var processIdentity = new NativeAppliedOwnershipProcessIdentity(
                    primary.TargetId,
                    primary.SoftwareId,
                    primary.ProcessId,
                    primary.ProcessStartKey);
                var memoryPolicy = IsMemoryPolicyOwnershipIdentity(processIdentity);
                if (primary.TargetId == 0
                    || primary.ProcessId == 0
                    || primary.ProcessStartKey == 0
                    || binding.Scope != (uint)NativeAppliedOwnershipJournalScope.Process
                    || memoryPolicy && primary.SoftwareId == 0
                    || (memoryPolicy
                        ? binding.DomainMask != (uint)NativeAppliedOwnershipDomain.PhysicalMemory
                            || binding.GradeValidMask !=
                                (uint)NativeAppliedOwnershipGradeValidity.Memory
                            || grades.ValidMask !=
                                (uint)NativeAppliedOwnershipGradeValidity.Memory
                            || !IsMemoryPriorityState(binding.ProcessFromGrade)
                            || !IsMemoryPriorityTarget(binding.ProcessToGrade)
                            || !IsMemoryPriorityTarget(grades.ProcessGrade)
                        : binding.DomainMask != (uint)NativeAppliedOwnershipDomain.Process
                            || binding.GradeValidMask !=
                                (uint)NativeAppliedOwnershipGradeValidity.Process
                            || grades.ValidMask !=
                                (uint)NativeAppliedOwnershipGradeValidity.Process
                            || !IsKnownProcessGrade(binding.ProcessFromGrade)
                            || !IsKnownProcessGrade(binding.ProcessToGrade)
                            || !IsKnownProcessGrade(grades.ProcessGrade))
                    || binding.ProcessFromGrade == binding.ProcessToGrade
                    || binding.CpuFromGrade != 0
                    || binding.CpuToGrade != 0
                    || binding.GpuFromGrade != 0
                    || binding.GpuToGrade != 0
                    || grades.CpuGrade != 0
                    || grades.GpuGrade != 0
                    || action.TargetId != primary.TargetId
                    || action.SoftwareId != primary.SoftwareId
                    || action.ProcessId != primary.ProcessId
                    || action.ProcessStartKey != primary.ProcessStartKey)
                {
                    throw new InvalidDataException(
                        "The Host Manager applied process ownership record is not canonical.");
                }
                if (!memoryPolicy)
                {
                    _ = ToNativeProcessGrade(grades.ProcessGrade);
                }
                break;
            case NativeAppliedOwnershipScope.Adapter:
                {
                    var domain = (NativeAppliedOwnershipDomain)binding.DomainMask;
                    var originalValidity = (uint)(domain &
                        (NativeAppliedOwnershipDomain.Cpu | NativeAppliedOwnershipDomain.Gpu));
                    var currentValidity = (NativeAppliedOwnershipGradeValidity)grades.ValidMask;
                    if (primary.TargetId != 0
                        || primary.SoftwareId == 0
                        || primary.ProcessId != 0
                        || primary.ProcessStartKey != 0
                        || binding.Scope != (uint)NativeAppliedOwnershipJournalScope.Software
                        || domain == NativeAppliedOwnershipDomain.None
                        || (domain & ~(NativeAppliedOwnershipDomain.Cpu | NativeAppliedOwnershipDomain.Gpu)) != 0
                        || binding.GradeValidMask != originalValidity
                        || currentValidity == NativeAppliedOwnershipGradeValidity.None
                        || (currentValidity & ~(NativeAppliedOwnershipGradeValidity.Cpu |
                            NativeAppliedOwnershipGradeValidity.Gpu)) != 0
                        || grades.ProcessGrade != 0
                        || binding.ProcessFromGrade != 0
                        || binding.ProcessToGrade != 0
                        || action.SoftwareId != primary.SoftwareId
                        || action.ProcessId != 0
                        || action.ProcessStartKey != 0)
                    {
                        throw new InvalidDataException(
                            "The Host Manager applied adapter ownership record is not canonical.");
                    }
                    if ((originalValidity & (uint)NativeAppliedOwnershipGradeValidity.Cpu) != 0)
                    {
                        if (!IsKnownAdapterGrade(binding.CpuFromGrade)
                            || !IsKnownAdapterGrade(binding.CpuToGrade)
                            || binding.CpuFromGrade == binding.CpuToGrade)
                        {
                            throw new InvalidDataException(
                                "The Host Manager applied adapter ownership record has an invalid CPU transition.");
                        }
                    }
                    else if (binding.CpuFromGrade != 0
                        || binding.CpuToGrade != 0)
                    {
                        throw new InvalidDataException(
                            "The Host Manager applied adapter ownership record has invalid original CPU data.");
                    }
                    if ((originalValidity & (uint)NativeAppliedOwnershipGradeValidity.Gpu) != 0)
                    {
                        if (!IsKnownAdapterGrade(binding.GpuFromGrade)
                            || !IsKnownAdapterGrade(binding.GpuToGrade)
                            || binding.GpuFromGrade == binding.GpuToGrade)
                        {
                            throw new InvalidDataException(
                                "The Host Manager applied adapter ownership record has an invalid GPU transition.");
                        }
                    }
                    else if (binding.GpuFromGrade != 0
                        || binding.GpuToGrade != 0)
                    {
                        throw new InvalidDataException(
                            "The Host Manager applied adapter ownership record has invalid original GPU data.");
                    }
                    if (currentValidity.HasFlag(NativeAppliedOwnershipGradeValidity.Cpu))
                    {
                        if (!IsOwnedAdapterGrade(grades.CpuGrade))
                        {
                            throw new InvalidDataException(
                                "The Host Manager applied adapter ownership record has an invalid current CPU grade.");
                        }
                        _ = ToNativeAdapterGrade(grades.CpuGrade);
                    }
                    else if (grades.CpuGrade != 0)
                    {
                        throw new InvalidDataException(
                            "The Host Manager applied adapter ownership record has a CPU grade without ownership.");
                    }
                    if (currentValidity.HasFlag(NativeAppliedOwnershipGradeValidity.Gpu))
                    {
                        if (!IsOwnedAdapterGrade(grades.GpuGrade))
                        {
                            throw new InvalidDataException(
                                "The Host Manager applied adapter ownership record has an invalid current GPU grade.");
                        }
                        _ = ToNativeAdapterGrade(grades.GpuGrade);
                    }
                    else if (grades.GpuGrade != 0)
                    {
                        throw new InvalidDataException(
                            "The Host Manager applied adapter ownership record has a GPU grade without ownership.");
                    }
                    break;
                }
            default:
                throw new InvalidDataException(
                    $"Unknown Host Manager applied ownership scope {primary.Scope}.");
        }
    }

    private static bool IsKnownProcessGrade(int grade)
        => grade is >= (int)NativeAppliedOwnershipProcessGrade.Level4
            and <= (int)NativeAppliedOwnershipProcessGrade.A1;

    private static bool IsMemoryPriorityState(int value) => value is >= 0 and <= 5;

    private static bool IsMemoryPriorityTarget(int value) => value is >= 1 and <= 5;

    private static bool IsKnownAdapterGrade(int grade)
        => grade is >= (int)NativeAppliedOwnershipAdapterGrade.Freeze
            and <= (int)NativeAppliedOwnershipAdapterGrade.Extreme;

    private static bool IsOwnedAdapterGrade(int grade)
        => IsKnownAdapterGrade(grade)
            && grade != (int)NativeAppliedOwnershipAdapterGrade.Normal;

    private static bool IsCanonicalAtomicGroup(
        ulong groupId,
        uint memberIndex,
        uint memberCount)
        => groupId == 0
            ? memberIndex == 0 && memberCount == 0
            : memberCount > 1 && memberIndex < memberCount;

    internal static NativeSmartCoordinatorSoftwareKind ToNativeSoftwareKind(string value)
        => value switch
        {
            SoftwareKinds.Game => NativeSmartCoordinatorSoftwareKind.Game,
            SoftwareKinds.HighPerformance => NativeSmartCoordinatorSoftwareKind.HighPerformance,
            SoftwareKinds.WindowsSystem or SoftwareKinds.WindowsComponent or SoftwareKinds.WindowsService => NativeSmartCoordinatorSoftwareKind.WindowsSystem,
            SoftwareKinds.Adapted => NativeSmartCoordinatorSoftwareKind.Adapted,
            SoftwareKinds.RuntimePackage or SoftwareKinds.RuntimeProduct or SoftwareKinds.RuntimeRoot => NativeSmartCoordinatorSoftwareKind.Runtime,
            SoftwareKinds.Controlled => NativeSmartCoordinatorSoftwareKind.Controlled,
            SoftwareKinds.Managed => NativeSmartCoordinatorSoftwareKind.Managed,
            SoftwareKinds.Unattributed => NativeSmartCoordinatorSoftwareKind.Unattributed,
            SoftwareKinds.Other => NativeSmartCoordinatorSoftwareKind.GeneralApplication,
            _ => NativeSmartCoordinatorSoftwareKind.Unknown
        };

    private static byte CreateCapabilityMask(IReadOnlyList<AdapterCpuSchedulingGrade> grades)
    {
        byte result = 0;
        foreach (var grade in grades)
        {
            result |= checked((byte)(1 << (int)grade));
        }
        return result;
    }

    private static byte CreateCapabilityMask(IReadOnlyList<AdapterGpuSchedulingGrade> grades)
    {
        byte result = 0;
        foreach (var grade in grades)
        {
            result |= checked((byte)(1 << (int)grade));
        }
        return result;
    }

    private static NativeComputeScoreProjection CreateNativeComputeScoreProjection(
        HostManagerComputeScoringCycleResult? result)
    {
        if (result is null)
        {
            return NativeComputeScoreProjection.Empty;
        }
        if (result.RoundNumber == 0
            || result.Cpu is null && result.Gpu is null)
        {
            throw new InvalidDataException(
                "The canonical compute score result has no usable domain identity.");
        }

        var processCpu = new Dictionary<NativeProcessScoreKey, HostManagerComputeScore>();
        var softwareCpu = new Dictionary<ulong, HostManagerComputeScore>();
        var processGpu = new Dictionary<NativeGpuScoreKey, HostManagerComputeScore>();
        var softwareGpu = new Dictionary<NativeSoftwareGpuScoreKey, HostManagerComputeScore>();
        IndexDomain(result.Cpu, gpuDomain: false);
        IndexDomain(result.Gpu, gpuDomain: true);

        return new NativeComputeScoreProjection(
            result.Cpu is not null,
            result.Gpu is not null,
            processCpu,
            softwareCpu,
            processGpu,
            softwareGpu);

        void IndexDomain(
            HostManagerComputeScoreDomainSnapshot? domain,
            bool gpuDomain)
        {
            if (domain is null)
            {
                return;
            }
            if (domain.Scores.IsDefault)
            {
                throw new InvalidDataException(
                    "The canonical compute score domain has no complete score collection.");
            }

            foreach (var score in domain.Scores)
            {
                if (score.SoftwareKey == 0
                    || !double.IsFinite(score.Score)
                    || score.Score < 0)
                {
                    throw new InvalidDataException(
                        "The canonical compute score domain contains an invalid score row.");
                }

                switch (score.Kind)
                {
                    case NativeComputeScoringOutputKind.ProcessCpu when !gpuDomain:
                        if (score.TargetKey == 0
                            || score.ProcessId <= 0
                            || score.ProcessStartKey == 0
                            || score.AdapterKey != 0
                            || score.MemberCount != 1
                            || !processCpu.TryAdd(
                                new NativeProcessScoreKey(
                                    score.TargetKey,
                                    score.ProcessId,
                                    score.ProcessStartKey),
                                score))
                        {
                            throw new InvalidDataException(
                                "The canonical CPU process score index is invalid or duplicated.");
                        }
                        break;
                    case NativeComputeScoringOutputKind.SoftwareCpu when !gpuDomain:
                        if (score.TargetKey != 0
                            || score.ProcessId != 0
                            || score.ProcessStartKey != 0
                            || score.AdapterKey != 0
                            || score.MemberCount == 0
                            || !softwareCpu.TryAdd(score.SoftwareKey, score))
                        {
                            throw new InvalidDataException(
                                "The canonical CPU software score index is invalid or duplicated.");
                        }
                        break;
                    case NativeComputeScoringOutputKind.ProcessGpu when gpuDomain:
                        if (score.TargetKey == 0
                            || score.ProcessId <= 0
                            || score.ProcessStartKey == 0
                            || score.AdapterKey == 0
                            || score.MemberCount != 1
                            || !processGpu.TryAdd(
                                new NativeGpuScoreKey(
                                    score.TargetKey,
                                    score.ProcessId,
                                    score.ProcessStartKey,
                                    score.AdapterKey),
                                score))
                        {
                            throw new InvalidDataException(
                                "The canonical GPU process score index is invalid or duplicated.");
                        }
                        break;
                    case NativeComputeScoringOutputKind.SoftwareGpu when gpuDomain:
                        if (score.TargetKey != 0
                            || score.ProcessId != 0
                            || score.ProcessStartKey != 0
                            || score.AdapterKey == 0
                            || score.MemberCount == 0
                            || !softwareGpu.TryAdd(
                                new NativeSoftwareGpuScoreKey(
                                    score.SoftwareKey,
                                    score.AdapterKey),
                                score))
                        {
                            throw new InvalidDataException(
                                "The canonical GPU software score index is invalid or duplicated.");
                        }
                        break;
                    default:
                        throw new InvalidDataException(
                            "The canonical compute score row appears in the wrong domain.");
                }
            }
        }
    }

    private static void ExcludeUnprojectedComputeScores(
        Span<NativeSmartCoordinatorInputRow> rows,
        IReadOnlyList<(int Index, HostManagerComputeScore SoftwareScore)> projectedRows,
        NativeComputeScoreProjection projection,
        IReadOnlySet<NativeProcessScoreKey> seenCpuProcessScores,
        IReadOnlySet<NativeGpuScoreKey> seenGpuProcessScores,
        IReadOnlyDictionary<ulong, uint> observedCpuSoftwareMembers,
        IReadOnlyDictionary<NativeSoftwareGpuScoreKey, uint> observedGpuSoftwareMembers,
        ICollection<SchedulingFactExclusion> exclusions)
    {
        foreach (var pair in projection.ProcessCpu)
        {
            if (!seenCpuProcessScores.Contains(pair.Key))
            {
                exclusions.Add(new SchedulingFactExclusion(pair.Key.ProcessId, pair.Key.ProcessStartKey,
                    HostManagerTargetIdentity.CreateProcessTargetId(pair.Key.ProcessId, pair.Key.ProcessStartKey),
                    "cpu-score", "canonical-process-not-projected"));
            }
        }
        foreach (var pair in projection.ProcessGpu)
        {
            if (!seenGpuProcessScores.Contains(pair.Key))
            {
                exclusions.Add(new SchedulingFactExclusion(pair.Key.ProcessId, pair.Key.ProcessStartKey,
                    HostManagerTargetIdentity.CreateProcessTargetId(pair.Key.ProcessId, pair.Key.ProcessStartKey),
                    "gpu-score", "canonical-process-not-projected"));
            }
        }
        var incompleteCpuSoftware = new HashSet<ulong>();
        var incompleteGpuSoftware = new HashSet<NativeSoftwareGpuScoreKey>();
        foreach (var pair in projection.SoftwareCpu)
        {
            if (!observedCpuSoftwareMembers.TryGetValue(pair.Key, out var memberCount)
                || memberCount != pair.Value.MemberCount)
            {
                incompleteCpuSoftware.Add(pair.Key);
                exclusions.Add(new SchedulingFactExclusion(null, null, $"software:{pair.Key:x16}",
                    "cpu-score", "canonical-software-membership-incomplete"));
            }
        }
        foreach (var pair in projection.SoftwareGpu)
        {
            if (!observedGpuSoftwareMembers.TryGetValue(pair.Key, out var memberCount)
                || memberCount != pair.Value.MemberCount)
            {
                incompleteGpuSoftware.Add(pair.Key);
                exclusions.Add(new SchedulingFactExclusion(null, null, $"software:{pair.Key.SoftwareKey:x16}/gpu:{pair.Key.AdapterKey:x16}",
                    "gpu-score", "canonical-software-membership-incomplete"));
            }
        }
        foreach (var (index, score) in projectedRows)
        {
            var incomplete = score.Kind == NativeComputeScoringOutputKind.SoftwareCpu
                ? incompleteCpuSoftware.Contains(score.SoftwareKey)
                : incompleteGpuSoftware.Contains(new NativeSoftwareGpuScoreKey(score.SoftwareKey, score.AdapterKey));
            if (!incomplete) continue;

            // A partial software aggregate cannot be advertised as complete. The matching
            // process score and independent raw metric remain valid.
            ref var row = ref rows[index];
            row.ValidMask &= ~(NativeSmartCoordinatorInputValidity.SoftwareScore
                | NativeSmartCoordinatorInputValidity.ScoreMemberCount);
            row.SoftwareScore = 0;
            row.ScoreMemberCount = 0;
        }
    }

    private static void IncrementMemberCount<TKey>(
        IDictionary<TKey, uint> counts,
        TKey key)
        where TKey : notnull
    {
        counts.TryGetValue(key, out var current);
        counts[key] = checked(current + 1);
    }

    private readonly record struct NativeFactProjectionResult(
        uint InputCount,
        bool FullProcessSnapshot,
        bool AllAppliedOwnershipRecordsRepresented);

    private readonly record struct NativeProcessScoreKey(
        ulong TargetKey,
        int ProcessId,
        ulong ProcessStartKey);

    private readonly record struct NativeGpuScoreKey(
        ulong TargetKey,
        int ProcessId,
        ulong ProcessStartKey,
        ulong AdapterKey);

    private readonly record struct NativeSoftwareGpuScoreKey(
        ulong SoftwareKey,
        ulong AdapterKey);

    private sealed record NativeComputeScoreProjection(
        bool HasCpu,
        bool HasGpu,
        IReadOnlyDictionary<NativeProcessScoreKey, HostManagerComputeScore> ProcessCpu,
        IReadOnlyDictionary<ulong, HostManagerComputeScore> SoftwareCpu,
        IReadOnlyDictionary<NativeGpuScoreKey, HostManagerComputeScore> ProcessGpu,
        IReadOnlyDictionary<NativeSoftwareGpuScoreKey, HostManagerComputeScore> SoftwareGpu)
    {
        internal static NativeComputeScoreProjection Empty { get; } = new(
            false,
            false,
            new Dictionary<NativeProcessScoreKey, HostManagerComputeScore>(),
            new Dictionary<ulong, HostManagerComputeScore>(),
            new Dictionary<NativeGpuScoreKey, HostManagerComputeScore>(),
            new Dictionary<NativeSoftwareGpuScoreKey, HostManagerComputeScore>());
    }

    private static bool IsProcessGpuMetricsComplete(
        SchedulingProcessFactSnapshot processFacts,
        SchedulingGpuInventorySnapshot inventory,
        bool identityConsistent,
        IReadOnlyList<HostManagerGpuTargetInfo> facts)
    {
        if (!identityConsistent
            || !processFacts.IsGpuUsageCurrentComplete(inventory)
            || processFacts.RequestedMetricMask.HasFlag(SchedulingProcessMetricMask.GpuDedicatedMemory)
                && !processFacts.IsGpuDedicatedMemoryCurrentComplete(inventory))
        {
            return false;
        }
        processFacts.TryGetCurrentDataset(
            SchedulingProcessMetricMask.GpuDedicatedMemory,
            out var dedicatedMemoryDataset);

        var byAdapterKey = facts.ToDictionary(static fact => fact.AdapterKey);
        foreach (var adapter in inventory.Adapters)
        {
            if (!byAdapterKey.TryGetValue(adapter.AdapterKey, out var fact)
                || fact.GpuIndex != adapter.Index
                || adapter.UsageStatus != SamplingObservationStatus.Current
                || !fact.HasUsageMetric
                || !IsPercent(fact.GpuUsagePercent))
            {
                return false;
            }

            if (adapter.CapabilityMask.HasFlag(SchedulingGpuCapabilityMask.DedicatedMemory)
                && (adapter.CapacityStatus != SamplingObservationStatus.Current
                    || !fact.HasVramMetric
                    || dedicatedMemoryDataset is null
                    || !IsPercent(fact.VramUsedPercent)))
            {
                return false;
            }
        }

        return byAdapterKey.Count == inventory.Adapters.Count;
    }

    private static bool TryResolveCurrentGpuIndex(
        HostManagerSample sample,
        HostManagerGpuTargetInfo fact,
        SchedulingProcessMetricMask metric,
        out uint gpuIndex)
    {
        gpuIndex = 0;
        var inventory = sample.Hardware.GpuInventory;
        var processFacts = sample.ProcessFacts;
        if (!inventory.IsCurrentComplete()
            || !processFacts.TryGetCurrentDataset(metric, out var dataset)
            || dataset.TopologyFingerprint == 0
            || dataset.TopologyFingerprint != inventory.TopologyFingerprint)
        {
            return false;
        }

        for (var adapterIndex = 0; adapterIndex < inventory.Adapters.Count; adapterIndex++)
        {
            var adapter = inventory.Adapters[adapterIndex];
            if (adapter.AdapterKey != fact.AdapterKey)
            {
                continue;
            }

            if (adapter.Index != fact.GpuIndex)
            {
                return false;
            }

            if ((metric == SchedulingProcessMetricMask.GpuUsage
                    && adapter.UsageStatus != SamplingObservationStatus.Current)
                || (metric == SchedulingProcessMetricMask.GpuDedicatedMemory
                    && adapter.CapacityStatus != SamplingObservationStatus.Current))
            {
                return false;
            }

            gpuIndex = checked((uint)adapter.Index);
            return true;
        }

        return false;
    }

    private static bool IsPercent(double value)
        => double.IsFinite(value) && value is >= 0 and <= 100;
}

internal readonly record struct NativeAppliedOwnershipProcessIdentity(
    ulong TargetKey,
    ulong SoftwareKey,
    uint ProcessId,
    ulong ProcessStartKey);

internal readonly record struct NativeAppliedOwnershipProcessIncarnation(
    uint ProcessId,
    ulong ProcessStartKey);

internal readonly record struct NativeAppliedOwnershipProcessAnchor(
    ulong SoftwareKey,
    bool HasProcessOwnership,
    bool HasMemoryOwnership);

internal sealed record NativeAppliedOwnershipFactIndex(
    ulong LedgerRevision,
    IReadOnlyDictionary<NativeAppliedOwnershipProcessIdentity, NativeAppliedOwnershipRecord> ProcessRecords,
    IReadOnlyDictionary<NativeAppliedOwnershipProcessIdentity, NativeAppliedOwnershipRecord> MemoryProcessRecords,
    IReadOnlyDictionary<ulong, NativeAppliedOwnershipRecord> AdapterRecords,
    IReadOnlyDictionary<NativeAppliedOwnershipProcessIncarnation, NativeAppliedOwnershipProcessAnchor> ProcessAnchors);

internal static class HostManagerRuntimeStateCodec
{
    internal static NativeSmartCoordinatorRuntimeState Encode(string value)
        => value switch
        {
            HostManagerRuntimeStates.ForegroundFocused => NativeSmartCoordinatorRuntimeState.ForegroundFocused,
            HostManagerRuntimeStates.ForegroundUnfocused => NativeSmartCoordinatorRuntimeState.ForegroundUnfocused,
            HostManagerRuntimeStates.BackgroundWindow => NativeSmartCoordinatorRuntimeState.BackgroundWindow,
            HostManagerRuntimeStates.TrayOnly => NativeSmartCoordinatorRuntimeState.TrayOnly,
            HostManagerRuntimeStates.BackgroundProcess => NativeSmartCoordinatorRuntimeState.BackgroundProcess,
            HostManagerRuntimeStates.NotRunning => NativeSmartCoordinatorRuntimeState.NotRunning,
            _ => NativeSmartCoordinatorRuntimeState.Unknown
        };

    internal static NativeSmartCoordinatorInputFlags EncodeFlags(string value)
        => Encode(value) switch
        {
            NativeSmartCoordinatorRuntimeState.ForegroundFocused => NativeSmartCoordinatorInputFlags.ForegroundFocused | NativeSmartCoordinatorInputFlags.HasVisibleWindow,
            NativeSmartCoordinatorRuntimeState.ForegroundUnfocused => NativeSmartCoordinatorInputFlags.HasVisibleWindow,
            NativeSmartCoordinatorRuntimeState.BackgroundWindow => NativeSmartCoordinatorInputFlags.HasBackgroundWindow,
            NativeSmartCoordinatorRuntimeState.TrayOnly => NativeSmartCoordinatorInputFlags.HasHiddenWindow,
            _ => NativeSmartCoordinatorInputFlags.None
        };
}
