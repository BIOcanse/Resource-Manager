using System.Globalization;
using ResourceManager.App.Application.Optimization;
using ResourceManager.App.Application.GpuPlacement;
using ResourceManager.App.Domain.CpuTopology;
using ResourceManager.App.Domain.GpuPlacement;
using ResourceManager.App.Domain.Optimization;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.GpuPlacement;
using ResourceManager.App.Infrastructure.GpuPlacement.External;

namespace ResourceManager.App.Infrastructure.Optimization;

public sealed partial class HostManagerSmartCoordinator
{
    private const string AutomaticPlacementOwner = "host-manager-automatic-placement-v1";
    private const string CpuSetsAffinityKind = "cpu-sets";
    private const string AffinityMaskKind = "affinity-mask";

    private async Task<bool> RunAutomaticPlacementCycleAsync(
        HostManagerCycleEffectAdmission effectAdmission,
        HostManagerSmartCoordinatorRuntimePlan desiredRuntime,
        HostManagerSample sample,
        HostManagerComputeScoringCycleResult? computeScoring,
        bool hardwareSchedulingEnabled,
        CancellationToken cancellationToken,
        List<HostManagerAutomaticGpuPlacement>? firstUse = null)
    {
        if (effectAdmission.IsScoreOnly)
        {
            return true;
        }

        var state = await LoadRollbackStateAsync(cancellationToken);
        if (!hardwareSchedulingEnabled && !GpuActionFacts.HasPlacementEffects(state.AppliedPlacements))
        {
            return true;
        }
        if (!effectAdmission.TryAcquire(
                HostManagerCycleEffectKind.NativeActionTransaction,
                out var permit))
        {
            return !GpuActionFacts.HasPlacementEffects(state.AppliedPlacements);
        }

        IReadOnlyList<HostManagerPlacementDesired> fullDesired = [];
        if (hardwareSchedulingEnabled)
        {
            var topology = desiredRuntime.RuntimePlan.CpuPlacementTopology
                ?? throw new InvalidDataException(
                    "Hardware placement is enabled without a compiled CPU topology.");
            var processes = CreateAutomaticPlacementProcesses(sample, computeScoring, state.AppliedPlacements);
            processes = await PrepareAutomaticGpuCandidatesAsync(processes, state.AppliedPlacements, firstUse, cancellationToken);
            var planned = HostManagerAutomaticPlacementPlanner.Plan(
                topology,
                sample.Hardware,
                desiredRuntime.RuntimePlan.HardwareScores,
                processes,
                desiredRuntime.HostPlan.HostRecreate.PlacementCoordinator,
                desiredRuntime.RuntimePlan.GpuPlacement.GlobalPreciseProviderEnabled,
                desiredRuntime.HostPlan.HotPublish.PlacementCoordinator.GpuOverflow);
            fullDesired = await CreateAutomaticPlacementDesiredAsync(
                topology,
                sample,
                processes,
                planned,
                state.AppliedPlacements,
                cancellationToken,
                firstUse);
        }

        return await RunPreparedAutomaticPlacementCycleAsync(effectAdmission, state, fullDesired,
            GpuActionFacts.AvailablePlacementEffects(state.AppliedPlacements), cancellationToken);
    }

    private async Task<bool> RunPreparedAutomaticPlacementCycleAsync(
        HostManagerCycleEffectAdmission effectAdmission, HostManagerRollbackStateDocument state,
        IReadOnlyList<HostManagerPlacementDesired> fullDesired,
        IReadOnlyList<HostManagerAppliedPlacementReceipt> placementEffects,
        CancellationToken cancellationToken)
    {
        if (!effectAdmission.TryAcquire(HostManagerCycleEffectKind.NativeActionTransaction, out var permit))
            return false;
        EnsurePlacementCoordinatorWorkspace();
        var session = placementCoordinatorSession
            ?? throw new InvalidOperationException("The Host Manager placement coordinator session is unavailable.");
        var workspace = placementCoordinatorWorkspace
            ?? throw new InvalidOperationException("The Host Manager placement coordinator workspace is unavailable.");
        var appliedPlan = appliedPlacementCoordinatorPlan
            ?? throw new InvalidOperationException("The Host Manager placement coordinator plan is unavailable.");
        fullDesired = fullDesired.Where(item => !GpuActionFacts.BlocksEffect(state.AppliedPlacements, item.Placement, item.Record)).ToArray();
        var desiredByIdentity = fullDesired.ToDictionary(
            static item => HostManagerPlacementCoordinatorProjection.CreateIdentity(
                item.Placement,
                item.Record));
        var observations = CapturePlacementObservations(placementEffects);
        var bounded = BuildBoundedPlacementCycle(
            placementEffects,
            fullDesired,
            desiredByIdentity,
            observations,
            effectAdmission.RecoveryRemaining,
            effectAdmission.NewPointOfNoReturnRemaining);
        if (bounded.Applied.Count == 0 && bounded.Desired.Count == 0)
        {
            return true;
        }

        var desiredProjection = HostManagerPlacementCoordinatorProjection.ProjectDesired(
            bounded.Desired,
            workspace.Desired);
        var appliedProjection = HostManagerPlacementCoordinatorProjection.ProjectApplied(
            bounded.Applied,
            workspace.Applied);
        ApplyPlacementObservations(appliedProjection, observations, workspace.Applied);

        var observedAt = MonotonicMilliseconds();
        var actionCapacity = checked((uint)workspace.Actions.Length);
        var cycle = new NativePlacementCoordinatorCycleInput
        {
            AbiVersion = NativePlacementCoordinatorAbi.Version,
            StructSize = NativePlacementCoordinatorSession.SizeOf<NativePlacementCoordinatorCycleInput>(),
            ConfigurationGeneration = appliedPlan.HotPublish.ConfigurationGeneration,
            CycleEpoch = checked(++placementCoordinatorCycleEpoch),
            ObservedAtMilliseconds = observedAt,
            ValidMask = (ulong)NativePlacementCycleValidity.Required,
            DesiredCount = desiredProjection.RowCount,
            AppliedCount = appliedProjection.RowCount,
            ActionCapacity = actionCapacity
        };
        RequirePlacementStatus(
            session.Plan(
                ref cycle,
                workspace.Desired.AsSpan(0, checked((int)desiredProjection.RowCount)),
                workspace.Applied.AsSpan(0, checked((int)appliedProjection.RowCount)),
                workspace.Actions),
            NativePlacementCoordinatorStatus.Ok,
            "automatic-plan");
        ValidatePlacementCycle(
            cycle,
            observedAt,
            desiredProjection.RowCount,
            appliedProjection.RowCount,
            actionCapacity,
            appliedPlan,
            workspace);

        var actionIds = new HashSet<ulong>();
        var feedbackCount = 0;
        var canContinue = true;
        for (var index = 0; index < cycle.ActionCount; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var action = workspace.Actions[index];
            ValidateAutomaticPlacementAction(
                action,
                desiredProjection,
                appliedProjection,
                observedAt,
                appliedPlan,
                actionIds);
            if (action.Disposition == (uint)NativePlacementActionDisposition.Restore)
            {
                if (!permit.TryReserveOwnedNativeRestore(out var reservation)
                    || reservation is null)
                {
                    throw new InvalidOperationException(
                        "The automatic placement restore exceeded its admitted recovery budget.");
                }
                using (reservation)
                {
                    reservation.EnterPointOfNoReturn();
                    var projected = appliedProjection.Require(action);
                    var settlement = RestorePlacementRecord(projected.Record);
                    if (settlement.CanRemoveReceipt)
                    {
                        state = RemovePlacementRecord(state, projected.Identity);
                        await SavePlacementCheckpointAsync(
                            state,
                            "Host Manager settled an automatic placement restore.",
                            CancellationToken.None);
                    }
                    workspace.Feedback[index] = CreatePlacementFeedback(action, settlement);
                }
            }
            else
            {
                var projected = desiredProjection.Require(action);
                var applied = await ApplyAutomaticPlacementAsync(
                    permit,
                    state,
                    action,
                    projected,
                    cancellationToken);
                state = applied.State;
                workspace.Feedback[index] = applied.Feedback;
                canContinue = applied.CanContinue;
            }
            feedbackCount++;
            if (!canContinue) break;
        }

        if (feedbackCount != 0)
        {
            RequirePlacementStatus(
                session.ApplyFeedback(workspace.Feedback.AsSpan(0, feedbackCount)),
                NativePlacementCoordinatorStatus.Ok,
                "automatic-feedback");
        }
        return canContinue;
    }

    private static IReadOnlyList<HostManagerAutomaticPlacementProcess>
        CreateAutomaticPlacementProcesses(
        HostManagerSample sample,
        HostManagerComputeScoringCycleResult? computeScoring,
        IReadOnlyList<HostManagerAppliedPlacementReceipt> existingPlacements)
    {
        var scoreProjection = CreateNativeComputeScoreProjection(computeScoring);
        var processes = new List<HostManagerAutomaticPlacementProcess>(sample.Targets.Count);
        foreach (var target in sample.Targets)
        {
            if (target.ProcessIds.Count != 1)
            {
                throw new InvalidDataException(
                    $"Automatic placement target {target.TargetId} does not represent exactly one process.");
            }
            var processId = target.ProcessIds[0];
            if (!target.ProcessStartKeys.TryGetValue(processId, out var processStartKey)
                || processStartKey == 0)
            {
                throw new InvalidDataException(
                    $"Automatic placement target {target.TargetId} has no exact process identity.");
            }
            var targetKey = NativeStableIdentity.CreateCaseInsensitiveKey(target.TargetId);
            scoreProjection.ProcessCpu.TryGetValue(
                new NativeProcessScoreKey(targetKey, processId, processStartKey),
                out var cpuScore);
            var gpuScores = new Dictionary<ulong, double>();
            var observedGpuUsage = new Dictionary<ulong, double>();
            var observedGpuMemory = new Dictionary<ulong, double>();
            foreach (var gpu in target.Gpus)
            {
                if (scoreProjection.ProcessGpu.TryGetValue(
                        new NativeGpuScoreKey(
                            targetKey,
                            processId,
                            processStartKey,
                            gpu.AdapterKey),
                        out var gpuScore))
                {
                    gpuScores.Add(gpu.AdapterKey, gpuScore.Score);
                }
                if (gpu.HasUsageMetric)
                {
                    observedGpuUsage.Add(gpu.AdapterKey, gpu.GpuUsagePercent);
                }
                // UMA residency is valid without a dedicated-capacity percentage.
                if (gpu.ResidentMemoryBytes is double residentBytes && residentBytes >= 0 && double.IsFinite(residentBytes))
                    observedGpuMemory.Add(gpu.AdapterKey, residentBytes);
            }

            processes.Add(new HostManagerAutomaticPlacementProcess(
                target.TargetId,
                target.SoftwareId,
                target.DisplayName,
                processId,
                processStartKey,
                target.ProcessNames.Count == 1 ? target.ProcessNames[0] : target.DisplayName,
                target.ExecutablePaths.Count == 1 ? target.ExecutablePaths[0] : null,
                target.CanApplyPhysicalCorePlacement,
                target.GpuPlacementPolicy,
                cpuScore?.Score,
                gpuScores,
                observedGpuUsage)
            {
                SoftwareKind = target.SoftwareKind,
                ObservedDedicatedMemoryBytes = observedGpuMemory,
                CanMigrateGpu = !GpuActionFacts.BlocksProcess(existingPlacements, target.TargetId, processId, processStartKey)
            });
        }
        return processes;
    }

    private async Task<IReadOnlyList<HostManagerPlacementDesired>> CreateAutomaticPlacementDesiredAsync(
        CpuTopologySnapshot topology,
        HostManagerSample sample,
        IReadOnlyList<HostManagerAutomaticPlacementProcess> processes,
        HostManagerAutomaticPlacementPlan plan,
        IReadOnlyList<HostManagerAppliedPlacementReceipt> existingPlacements,
        CancellationToken cancellationToken,
        List<HostManagerAutomaticGpuPlacement>? firstUse = null)
    {
        var recordedState = existingPlacements;
        existingPlacements = GpuActionFacts.PlacementEffects(existingPlacements);
        var existingByKey = existingPlacements.ToDictionary(
            HostManagerPlacementReceiptKey.Create);
        var desired = new List<HostManagerPlacementDesired>(
            plan.Cpu.Count + plan.Gpu.Count + existingPlacements.Count);
        foreach (var cpu in plan.Cpu
            .OrderByDescending(static item => item.CanonicalProcessScore)
            .ThenBy(static item => item.Process.TargetId, StringComparer.OrdinalIgnoreCase))
        {
            existingByKey.TryGetValue(
                HostManagerPlacementReceiptKey.Create(
                    OptimizationResourceKinds.Cpu,
                    cpu.Process.TargetId),
                out var existing);
            var item = TryCreateCpuPlacementDesired(topology, cpu, existing);
            if (item is not null)
            {
                desired.Add(item);
            }
        }

        foreach (var gpu in plan.Gpu
            .OrderByDescending(static item => item.CanonicalProcessScore)
            .ThenBy(static item => item.Process.TargetId, StringComparer.OrdinalIgnoreCase))
        {
            if (GpuActionFacts.RemoteCallBlocksProcess(recordedState, gpu.Process.TargetId,
                    gpu.Process.ProcessId, gpu.Process.ProcessStartKey)) continue;
            existingByKey.TryGetValue(
                HostManagerPlacementReceiptKey.Create(
                    OptimizationResourceKinds.Gpu,
                    gpu.Process.TargetId),
                out var existing);
            if (!GpuActionFacts.BlocksProcess(recordedState, gpu.Process.TargetId,
                gpu.Process.ProcessId, gpu.Process.ProcessStartKey))
            {
                var shim = await PrepareGpuShimPlacementDesiredAsync(gpu, existing, firstUse, cancellationToken);
                if (shim is not null) desired.Add(shim);
            }
        }

        var processesByTarget = processes.ToDictionary(
            static process => process.TargetId,
            StringComparer.OrdinalIgnoreCase);
        var desiredIdentities = desired
            .Select(static item => HostManagerPlacementCoordinatorProjection.CreateIdentity(
                item.Placement,
                item.Record))
            .ToHashSet();
        foreach (var existing in existingPlacements)
        {
            if (!processesByTarget.TryGetValue(existing.TargetId, out var process))
            {
                continue;
            }
            foreach (var record in existing.Records.Where(IsAutomaticPlacementRecord))
            {
                var identity = HostManagerPlacementCoordinatorProjection.CreateIdentity(existing, record);
                if (desiredIdentities.Contains(identity))
                {
                    continue;
                }
                var retainForMissingScore = existing.ResourceKind.Equals(
                        OptimizationResourceKinds.Cpu,
                        StringComparison.OrdinalIgnoreCase)
                    ? WantsCpuPlacement(topology, process) && !process.CanonicalCpuScore.HasValue
                    : existing.ResourceKind.Equals(
                            OptimizationResourceKinds.Gpu,
                            StringComparison.OrdinalIgnoreCase)
                        && (record.Kind is HostManagerAppliedRecordKinds.GpuShimPolicy
                            or HostManagerAppliedRecordKinds.GpuRuntimeRebuildTrigger)
                        && WantsGpuShim(process)
                        && (process.CanonicalGpuScores.Count == 0
                            || !sample.Hardware.GpuInventory.IsCurrentComplete()
                            || process.Policy.TargetGpu == GpuPlacementTargets.AutoIdleGpu
                                && !plan.Gpu.Any(p => p.Process.ProcessId == process.ProcessId));
                if (retainForMissingScore)
                {
                    desired.Add(new HostManagerPlacementDesired(existing, record, 0));
                    desiredIdentities.Add(identity);
                }
            }
        }

        return desired;
    }

    private HostManagerPlacementDesired? TryCreateCpuPlacementDesired(
        CpuTopologySnapshot topology,
        HostManagerAutomaticCpuPlacement planned,
        HostManagerAppliedPlacementReceipt? existing)
    {
        var usesCpuSets = planned.Selector.CpuSetIds is { Count: > 0 };
        var existingRecord = TryGetAutomaticRecord(existing, HostManagerAppliedRecordKinds.CpuAffinity);
        if (existing is not null
            && (existingRecord is null
                || IsCpuSetsRecord(existingRecord) != usesCpuSets))
        {
            return null;
        }

        IReadOnlyList<uint>? previousCpuSetIds = null;
        long? previousAffinityMask = null;
        string processName;
        string executablePath;
        DateTimeOffset startedAt;
        if (existingRecord is not null)
        {
            if (!TryReadReceiptProcessIdentity(existingRecord, out _, out var existingStartKey)
                || existingStartKey != planned.Process.ProcessStartKey)
            {
                return null;
            }
            startedAt = DateTimeOffset.FromFileTime(checked((long)existingStartKey));
            processName = ReadMetadata(existingRecord, "processName") ?? planned.Process.ProcessName;
            executablePath = ReadMetadata(existingRecord, "executablePath")
                ?? planned.Process.ExecutablePath
                ?? string.Empty;
            if (usesCpuSets)
            {
                if (!TryParseCpuSetIds(
                        ReadMetadata(existingRecord, "previousCpuSetIds"),
                        out previousCpuSetIds))
                {
                    return null;
                }
            }
            else if (!TryParseAffinityMask(
                    ReadMetadata(existingRecord, "previousAffinityMask"),
                    out var parsedMask))
            {
                return null;
            }
            else
            {
                previousAffinityMask = parsedMask;
            }
        }
        else
        {
            var fields = usesCpuSets
                ? ProcessPlacementReadFields.DefaultCpuSets
                : ProcessPlacementReadFields.Affinity;
            var baseline = processPolicyWriter.ReadPlacementStateForRecovery(
                planned.Process.ProcessId,
                fields);
            if (baseline.Status != RecoveryReadStatus.Found
                || baseline.Value is null
                || !MatchesAutomaticProcessIdentity(planned.Process, baseline.Value))
            {
                return null;
            }
            startedAt = baseline.Value.StartedAt;
            processName = baseline.Value.ProcessName;
            executablePath = baseline.Value.ExecutablePath ?? string.Empty;
            if (usesCpuSets)
            {
                previousCpuSetIds = baseline.Value.DefaultCpuSetIds;
                if (previousCpuSetIds is null)
                {
                    return null;
                }
            }
            else
            {
                previousAffinityMask = baseline.Value.ProcessorAffinityMask;
                if (!previousAffinityMask.HasValue || previousAffinityMask.Value == 0)
                {
                    return null;
                }
            }
        }

        var metadata = CreateProcessPlacementMetadata(planned.Process, startedAt, processName, executablePath);
        metadata["affinityKind"] = usesCpuSets ? CpuSetsAffinityKind : AffinityMaskKind;
        metadata["source"] = planned.Source;
        metadata["selectedPhysicalCoreIds"] = string.Join(',', planned.PhysicalCoreIds);
        metadata["topologyIdentity"] = CreateCpuTopologyIdentity(topology);
        if (usesCpuSets)
        {
            var targetIds = planned.Selector.CpuSetIds!
                .Distinct()
                .Order()
                .ToArray();
            if (previousCpuSetIds!.Count != 0)
            {
                targetIds = targetIds.Intersect(previousCpuSetIds).Order().ToArray();
            }
            if (targetIds.Length == 0)
            {
                return null;
            }
            if (existing is null && CpuSetIdsEqual(targetIds, previousCpuSetIds))
            {
                return null;
            }
            metadata["previousCpuSetIds"] = FormatCpuSetIds(previousCpuSetIds);
            metadata["appliedCpuSetIds"] = FormatCpuSetIds(targetIds);
        }
        else
        {
            var targetMask = planned.Selector.AffinityMask!.Value & previousAffinityMask!.Value;
            if (targetMask == 0)
            {
                return null;
            }
            if (existing is null && targetMask == previousAffinityMask.Value)
            {
                return null;
            }
            metadata["previousAffinityMask"] = CpuAffinityPlanner.FormatMask(previousAffinityMask.Value);
            metadata["appliedAffinityMask"] = CpuAffinityPlanner.FormatMask(targetMask);
        }

        var record = new HostManagerAppliedRecord(
            HostManagerAppliedRecordKinds.CpuAffinity,
            existingRecord?.RecordId
                ?? CreateStableId($"automatic-placement|cpu|{planned.Process.TargetId}"),
            metadata);
        var now = durableTimeSource.NextUtc();
        var placement = new HostManagerAppliedPlacementReceipt(
            planned.Process.TargetId,
            planned.Process.DisplayName,
            planned.Process.SoftwareId,
            OptimizationResourceKinds.Cpu,
            [record],
            existing?.AppliedAt ?? now,
            now);
        return new HostManagerPlacementDesired(
            placement,
            record,
            ToPlacementPriority(planned.CanonicalProcessScore));
    }

    private static Dictionary<string, string> CreateProcessPlacementMetadata(
        HostManagerAutomaticPlacementProcess process,
        DateTimeOffset startedAt,
        string processName,
        string executablePath)
        => new(StringComparer.Ordinal)
        {
            ["owner"] = AutomaticPlacementOwner,
            ["processId"] = process.ProcessId.ToString(CultureInfo.InvariantCulture),
            ["processStartKey"] = process.ProcessStartKey.ToString(CultureInfo.InvariantCulture),
            ["processStartedAt"] = startedAt.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture),
            ["processName"] = processName,
            ["executablePath"] = executablePath
        };

    private static HostManagerAppliedRecord? TryGetAutomaticRecord(
        HostManagerAppliedPlacementReceipt? placement,
        string expectedKind)
    {
        if (placement is null)
        {
            return null;
        }
        return placement.Records.SingleOrDefault(record => record.Kind.Equals(expectedKind, StringComparison.OrdinalIgnoreCase)
            && IsAutomaticPlacementRecord(record));
    }

    private static bool IsAutomaticPlacementRecord(HostManagerAppliedRecord record)
        => string.Equals(
            ReadMetadata(record, "owner"),
            AutomaticPlacementOwner,
            StringComparison.Ordinal);

    private static bool WantsCpuPlacement(
        CpuTopologySnapshot topology,
        HostManagerAutomaticPlacementProcess process)
    {
        if (!process.CanApplyPhysicalPlacement)
        {
            return false;
        }
        return process.Policy.CpuManualLockedPositionIds.Count != 0
            || topology.Ccds.Count > 1
                && process.Policy.CpuMaximumOccupancyMode.Equals(
                    CpuMaximumOccupancyModes.SingleCcd,
                    StringComparison.OrdinalIgnoreCase);
    }

    private static HostManagerBoundedPlacementCycle BuildBoundedPlacementCycle(
        IReadOnlyList<HostManagerAppliedPlacementReceipt> appliedPlacements,
        IReadOnlyList<HostManagerPlacementDesired> fullDesired,
        IReadOnlyDictionary<HostManagerPlacementNativeIdentity, HostManagerPlacementDesired> desiredByIdentity,
        IReadOnlyDictionary<HostManagerPlacementNativeIdentity, HostManagerPlacementObservation> observations,
        uint recoveryBudget,
        uint newBudget)
    {
        var selectedApplied = new List<HostManagerAppliedPlacementReceipt>();
        var selectedDesired = new List<HostManagerPlacementDesired>();
        var existingIdentities = appliedPlacements
            .SelectMany(placement => placement.Records.Select(record => HostManagerPlacementCoordinatorProjection.CreateIdentity(placement, record)))
            .ToHashSet();
        var selectedDesiredIdentities = new HashSet<HostManagerPlacementNativeIdentity>();
        foreach (var placement in appliedPlacements
            .OrderBy(static item => item.ResourceKind, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static item => item.TargetId, StringComparer.OrdinalIgnoreCase))
        {
            var selectedRecords = new List<HostManagerAppliedRecord>();
            foreach (var record in placement.Records)
            {
                var identity = HostManagerPlacementCoordinatorProjection.CreateIdentity(placement, record);
                desiredByIdentity.TryGetValue(identity, out var matchingDesired);
                if (!observations.TryGetValue(identity, out var observation))
                {
                    throw new InvalidDataException(
                        "Automatic placement has no observation for a durable receipt.");
                }
                var sameDesired = matchingDesired is not null
                    && HostManagerPlacementCoordinatorProjection.CreatePayloadDigest(
                        "applied",
                        placement,
                        record) == HostManagerPlacementCoordinatorProjection.CreatePayloadDigest(
                        "applied",
                        matchingDesired.Placement,
                        matchingDesired.Record);
                var needsRecovery = observation.Status != NativePlacementObservationStatus.Unavailable
                    && (!observation.ReceiptMatches || !sameDesired);
                if (needsRecovery && recoveryBudget == 0)
                {
                    continue;
                }
                if (needsRecovery)
                {
                    recoveryBudget--;
                }
                selectedRecords.Add(record);
                if (matchingDesired is not null)
                {
                    selectedDesired.Add(matchingDesired);
                    selectedDesiredIdentities.Add(identity);
                }
            }
            if (selectedRecords.Count != 0) selectedApplied.Add(placement with { Records = selectedRecords });
        }

        foreach (var item in fullDesired
            .OrderByDescending(static item => item.Priority)
            .ThenBy(static item => item.Placement.ResourceKind, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static item => item.Placement.TargetId, StringComparer.OrdinalIgnoreCase))
        {
            var identity = HostManagerPlacementCoordinatorProjection.CreateIdentity(
                item.Placement,
                item.Record);
            if (selectedDesiredIdentities.Contains(identity)
                || existingIdentities.Contains(identity))
            {
                continue;
            }
            if (item.ActionReservation is null && newBudget == 0)
            {
                continue;
            }
            selectedDesired.Add(item);
            selectedDesiredIdentities.Add(identity);
            if (item.ActionReservation is null) newBudget--;
        }
        return new HostManagerBoundedPlacementCycle(selectedDesired, selectedApplied);
    }

    private IReadOnlyDictionary<HostManagerPlacementNativeIdentity, HostManagerPlacementObservation>
        CapturePlacementObservations(
        IReadOnlyList<HostManagerAppliedPlacementReceipt> placements)
    {
        var observations = new Dictionary<HostManagerPlacementNativeIdentity, HostManagerPlacementObservation>();
        foreach (var placement in placements)
        {
            foreach (var record in placement.Records)
            {
                var identity = HostManagerPlacementCoordinatorProjection.CreateIdentity(placement, record);
                var receiptDigest = HostManagerPlacementCoordinatorProjection.CreatePayloadDigest(
                    "applied",
                    placement,
                    record);
                var previousDigest = HostManagerPlacementCoordinatorProjection.CreatePayloadDigest(
                    "previous",
                    placement,
                    record);
                var observation = record.Kind switch
                {
                    HostManagerAppliedRecordKinds.CpuAffinity => ObserveCpuPlacement(
                        placement,
                        record,
                        receiptDigest,
                        previousDigest),
                    HostManagerAppliedRecordKinds.GpuPreference => ObserveGpuPreference(
                        placement,
                        record,
                        receiptDigest,
                        previousDigest),
                    HostManagerAppliedRecordKinds.GpuShimPolicy => ObserveGpuShimPolicy(placement, record, receiptDigest, previousDigest),
                    HostManagerAppliedRecordKinds.GpuRuntimeRebuildTrigger when ExternalGpuRuntimePlacementRecord.IsRecord(record)
                        => ObserveExternalGpuRuntimePlacement(record, receiptDigest, previousDigest),
                    _ => HostManagerPlacementObservation.Unchecked
                };
                if (!observations.TryAdd(identity, observation))
                {
                    throw new InvalidDataException(
                        "Automatic placement received duplicate durable record identities.");
                }
            }
        }
        return observations;
    }

    private HostManagerPlacementObservation ObserveCpuPlacement(
        HostManagerAppliedPlacementReceipt placement,
        HostManagerAppliedRecord record,
        ulong receiptDigest,
        ulong previousDigest)
    {
        if (!TryReadReceiptProcessIdentity(record, out var processId, out var processStartKey))
        {
            return HostManagerPlacementObservation.Unchecked;
        }
        var usesCpuSets = IsCpuSetsRecord(record);
        var read = processPolicyWriter.ReadPlacementStateForRecovery(
            processId,
            usesCpuSets
                ? ProcessPlacementReadFields.DefaultCpuSets
                : ProcessPlacementReadFields.Affinity);
        if (read.Status == RecoveryReadStatus.Unavailable)
        {
            return HostManagerPlacementObservation.Unavailable(read.NativeErrorCode);
        }
        if (read.Status != RecoveryReadStatus.Found
            || read.Value is null
            || !MatchesReceiptProcessIdentity(record, processStartKey, read.Value))
        {
            return HostManagerPlacementObservation.NotFound(read.NativeErrorCode);
        }

        if (usesCpuSets)
        {
            if (!TryParseCpuSetIds(ReadMetadata(record, "appliedCpuSetIds"), out var applied)
                || !TryParseCpuSetIds(ReadMetadata(record, "previousCpuSetIds"), out var previous)
                || read.Value.DefaultCpuSetIds is null)
            {
                return HostManagerPlacementObservation.Unchecked;
            }
            if (CpuSetIdsEqual(read.Value.DefaultCpuSetIds, applied))
            {
                return HostManagerPlacementObservation.FoundReceipt(receiptDigest);
            }
            if (CpuSetIdsEqual(read.Value.DefaultCpuSetIds, previous))
            {
                return HostManagerPlacementObservation.FoundPrevious(previousDigest);
            }
            return HostManagerPlacementObservation.FoundForeign(
                CreateForeignPlacementDigest(
                    placement,
                    record,
                    FormatCpuSetIds(read.Value.DefaultCpuSetIds),
                    receiptDigest,
                    previousDigest));
        }

        if (!TryParseAffinityMask(ReadMetadata(record, "appliedAffinityMask"), out var appliedMask)
            || !TryParseAffinityMask(ReadMetadata(record, "previousAffinityMask"), out var previousMask)
            || read.Value.ProcessorAffinityMask is null)
        {
            return HostManagerPlacementObservation.Unchecked;
        }
        if (read.Value.ProcessorAffinityMask == appliedMask)
        {
            return HostManagerPlacementObservation.FoundReceipt(receiptDigest);
        }
        if (read.Value.ProcessorAffinityMask == previousMask)
        {
            return HostManagerPlacementObservation.FoundPrevious(previousDigest);
        }
        return HostManagerPlacementObservation.FoundForeign(
            CreateForeignPlacementDigest(
                placement,
                record,
                CpuAffinityPlanner.FormatMask(read.Value.ProcessorAffinityMask.Value),
                receiptDigest,
                previousDigest));
    }

    private HostManagerPlacementObservation ObserveGpuPreference(
        HostManagerAppliedPlacementReceipt placement,
        HostManagerAppliedRecord record,
        ulong receiptDigest,
        ulong previousDigest)
    {
        var path = ReadMetadata(record, "path");
        var appliedValue = ReadMetadata(record, "appliedValue");
        var previousValue = ReadMetadata(record, "previousValue");
        if (string.IsNullOrWhiteSpace(path)
            || string.IsNullOrWhiteSpace(appliedValue)
            || previousValue is null
            || !bool.TryParse(ReadMetadata(record, "hadValue"), out var hadValue))
        {
            return HostManagerPlacementObservation.Unchecked;
        }
        var read = graphicsPreferenceStore.ReadValueForRecovery(path);
        if (read.Status == RecoveryReadStatus.Unavailable)
        {
            return HostManagerPlacementObservation.Unavailable(read.NativeErrorCode);
        }
        if (read.Status == RecoveryReadStatus.NotFoundOrExited)
        {
            return hadValue
                ? HostManagerPlacementObservation.FoundForeign(
                    CreateForeignPlacementDigest(
                        placement,
                        record,
                        "missing",
                        receiptDigest,
                        previousDigest))
                : HostManagerPlacementObservation.FoundPrevious(previousDigest);
        }
        var current = read.Value ?? string.Empty;
        if (string.Equals(current, appliedValue, StringComparison.Ordinal))
        {
            return HostManagerPlacementObservation.FoundReceipt(receiptDigest);
        }
        if (hadValue && string.Equals(current, previousValue, StringComparison.Ordinal))
        {
            return HostManagerPlacementObservation.FoundPrevious(previousDigest);
        }
        return HostManagerPlacementObservation.FoundForeign(
            CreateForeignPlacementDigest(
                placement,
                record,
                current,
                receiptDigest,
                previousDigest));
    }

    private static void ApplyPlacementObservations(
        HostManagerPlacementProjection projection,
        IReadOnlyDictionary<HostManagerPlacementNativeIdentity, HostManagerPlacementObservation> observations,
        NativePlacementAppliedInput[] destination)
    {
        foreach (var projected in projection.Records)
        {
            if (!observations.TryGetValue(projected.Identity, out var observation))
            {
                throw new InvalidDataException(
                    "Automatic placement projection lost a durable observation.");
            }
            var input = destination[projected.RowIndex];
            input.ObservationStatus = (uint)observation.Status;
            if (observation.Status == NativePlacementObservationStatus.Found)
            {
                input.CurrentDigest = observation.CurrentDigest;
                input.ValidMask |= (ulong)NativePlacementAppliedValidity.CurrentDigest;
            }
            destination[projected.RowIndex] = input;
        }
    }

    private async Task<HostManagerAutomaticPlacementApplyResult> ApplyAutomaticPlacementAsync(
        HostManagerCycleEffectPermit permit,
        HostManagerRollbackStateDocument state,
        NativePlacementAction action,
        HostManagerPlacementProjectedDesired desired,
        CancellationToken cancellationToken)
    {
        var reservation = desired.ActionReservation;
        var ownsReservation = reservation is null;
        if (ownsReservation && (!permit.TryReserveNewPointOfNoReturn(1, out reservation)
            || reservation is null))
        {
            throw new InvalidOperationException(
                "The automatic placement apply exceeded its admitted new-effect budget.");
        }
        try
        {
            permit.RequireSingleNewActionReservation(reservation!);
            var externalAction = ExternalGpuRuntimePlacementRecord.IsRecord(desired.Record);
            var gpu = desired.Record.Kind is HostManagerAppliedRecordKinds.GpuPreference or HostManagerAppliedRecordKinds.GpuShimPolicy
                || externalAction;
            if (gpu && (cancellationToken.IsCancellationRequested || RemainingGpuActionTime(action.DeadlineMilliseconds) <= TimeSpan.Zero))
                return new(state, CreateAutomaticPlacementFeedback(action, NativePlacementFeedbackStatus.RetryableFailure, 1460, 0), false);
            state = AddAutomaticPlacementRecord(state, desired);
            if (gpu)
            {
                if (!await SaveAutomaticGpuPreparationAsync(state, desired, action.DeadlineMilliseconds, cancellationToken))
                    return new(state, CreateAutomaticPlacementFeedback(action, NativePlacementFeedbackStatus.RetryableFailure, 1460, 0), false);
            }
            else
                await SavePlacementCheckpointAsync(state, "Host Manager prepared an automatic placement write.", cancellationToken);

            if (gpu)
            {
                var processIdentity = ReadAutomaticPlacementProcessIdentity(desired.Record);
                if (cancellationToken.IsCancellationRequested || RemainingGpuActionTime(action.DeadlineMilliseconds) <= TimeSpan.Zero)
                    return new(state, CreateAutomaticPlacementFeedback(action, NativePlacementFeedbackStatus.RetryableFailure, 1460, 0), false);
                if (!processIdentity.Matches)
                {
                    state = RemovePlacementRecord(state, desired.Identity);
                    await SavePlacementCheckpointAsync(
                        state,
                        "Host Manager rejected an automatic GPU preference write because the exact process identity was unavailable.",
                        CancellationToken.None);
                    return new HostManagerAutomaticPlacementApplyResult(
                        state,
                        CreateAutomaticPlacementFeedback(
                            action,
                            processIdentity.Status == RecoveryReadStatus.Unavailable
                                ? NativePlacementFeedbackStatus.RetryableFailure
                                : NativePlacementFeedbackStatus.OwnershipLost,
                            processIdentity.NativeErrorCode,
                            0));
                }
            }

            var before = ReadAutomaticPlacementValue(desired.Placement, desired.Record);
            if (gpu && (cancellationToken.IsCancellationRequested || RemainingGpuActionTime(action.DeadlineMilliseconds) <= TimeSpan.Zero))
            {
                // Only the intent was saved. Existing recovery verifies the unchanged original value.
                return new(state, CreateAutomaticPlacementFeedback(action, NativePlacementFeedbackStatus.RetryableFailure, 1460, 0), false);
            }
            if (before.Relation != HostManagerPlacementValueRelation.Previous)
            {
                state = RemovePlacementRecord(state, desired.Identity);
                await SavePlacementCheckpointAsync(
                    state,
                    "Host Manager rejected an automatic placement write because its baseline changed.",
                    CancellationToken.None);
                return new HostManagerAutomaticPlacementApplyResult(
                    state,
                    CreateAutomaticPlacementFeedback(
                        action,
                        NativePlacementFeedbackStatus.OwnershipLost,
                        before.NativeErrorCode,
                        0));
            }

            if (externalAction)
            {
                permit.EnterSingleNewAction(reservation!);
                var (externalState, canContinue, releaseRecord, result) = await ExecuteGpuShimActionAsync(
                    state, desired, action.DeadlineMilliseconds, cancellationToken);
                state = externalState;
                if (releaseRecord)
                {
                    state = RemovePlacementRecord(state, desired.Identity);
                    await SavePlacementCheckpointAsync(state,
                        "Host Manager settled an external GPU action without a confirmed transfer.", CancellationToken.None);
                }
                var confirmed = ExternalGpuRuntimePlacementRecord.IsConfirmed(result)
                    && !runningGpuPlacementActions.HasUnreleasedExternalControl;
                return new HostManagerAutomaticPlacementApplyResult(state,
                    CreateAutomaticPlacementFeedback(action, confirmed
                        ? NativePlacementFeedbackStatus.Applied : NativePlacementFeedbackStatus.RetryableFailure,
                        confirmed ? 0 : 1460, confirmed ? desired.Input.DesiredDigest : 0), canContinue);
            }

            permit.EnterSingleNewAction(reservation!);
            var writeError = WriteAutomaticPlacement(desired.Record);
            var after = ReadAutomaticPlacementValue(desired.Placement, desired.Record);
            if (after.Relation == HostManagerPlacementValueRelation.Applied)
            {
                var feedback = CreateAutomaticPlacementFeedback(
                    action, NativePlacementFeedbackStatus.Applied, writeError, desired.Input.DesiredDigest);
                var canContinue = true;
                if (desired.RuntimeGpuAction is not null)
                {
                    bool releasePolicy;
                    (state, canContinue, releasePolicy, _) = await ExecuteGpuShimActionAsync(state, desired, action.DeadlineMilliseconds, cancellationToken);
                    if (releasePolicy)
                    {
                        var restored = gpuShimRuntime.RestorePolicyRecord(desired.Record);
                        if (restored.CanRemoveReceipt)
                        {
                            state = RemovePlacementRecord(state, desired.Identity);
                            await SavePlacementCheckpointAsync(state,
                                "Host Manager released a settled GPU action policy without confirmed placement.", CancellationToken.None);
                        }
                        feedback = CreateAutomaticPlacementFeedback(action,
                            restored.Kind == HostManagerPlacementSettlementKind.OwnershipLost
                                ? NativePlacementFeedbackStatus.OwnershipLost : NativePlacementFeedbackStatus.RetryableFailure,
                            restored.NativeErrorCode, 0);
                        canContinue &= restored.CanRemoveReceipt;
                    }
                }
                return new HostManagerAutomaticPlacementApplyResult(
                    state,
                    feedback,
                    canContinue);
            }
            if (after.Relation is HostManagerPlacementValueRelation.Previous
                or HostManagerPlacementValueRelation.NotFound)
            {
                state = RemovePlacementRecord(state, desired.Identity);
                await SavePlacementCheckpointAsync(
                    state,
                    "Host Manager observed no automatic placement write and removed its prepared receipt.",
                    CancellationToken.None);
                return new HostManagerAutomaticPlacementApplyResult(
                    state,
                    CreateAutomaticPlacementFeedback(
                        action,
                        after.Relation == HostManagerPlacementValueRelation.NotFound
                            ? NativePlacementFeedbackStatus.OwnershipLost
                            : NativePlacementFeedbackStatus.RetryableFailure,
                        after.NativeErrorCode != 0 ? after.NativeErrorCode : writeError,
                        0));
            }
            if (after.Relation == HostManagerPlacementValueRelation.Foreign)
            {
                state = RemovePlacementRecord(state, desired.Identity);
                await SavePlacementCheckpointAsync(
                    state,
                    "Host Manager released automatic placement ownership after a foreign write.",
                    CancellationToken.None);
                return new HostManagerAutomaticPlacementApplyResult(
                    state,
                    CreateAutomaticPlacementFeedback(
                        action,
                        NativePlacementFeedbackStatus.OwnershipLost,
                        after.NativeErrorCode,
                        0));
            }
            return new HostManagerAutomaticPlacementApplyResult(
                state,
                CreateAutomaticPlacementFeedback(
                    action,
                    NativePlacementFeedbackStatus.RetryableFailure,
                    after.NativeErrorCode != 0 ? after.NativeErrorCode : writeError,
                    0));
        }
        finally
        {
            if (ownsReservation) reservation?.Dispose();
        }
    }

    private int WriteAutomaticPlacement(HostManagerAppliedRecord record)
    {
        if (record.Kind == HostManagerAppliedRecordKinds.GpuShimPolicy)
        {
            try { return gpuShimRuntime.TryApplyPolicyRecord(record) ? 0 : 1306; }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(exception, "Automatic GPU shim policy publication failed for {Target}.", record.RecordId);
                return exception.HResult;
            }
        }
        if (record.Kind.Equals(HostManagerAppliedRecordKinds.CpuAffinity, StringComparison.OrdinalIgnoreCase))
        {
            if (!TryReadReceiptProcessIdentity(record, out var processId, out var processStartKey))
            {
                return 87;
            }
            ProcessResourcePolicyBatchRequest request;
            if (IsCpuSetsRecord(record))
            {
                if (!TryParseCpuSetIds(ReadMetadata(record, "appliedCpuSetIds"), out var cpuSetIds)
                    || cpuSetIds.Count == 0)
                {
                    return 87;
                }
                request = new ProcessResourcePolicyBatchRequest(
                    processId,
                    DateTimeOffset.FromFileTime(checked((long)processStartKey)),
                    CpuSetIds: cpuSetIds);
            }
            else
            {
                if (!TryParseAffinityMask(ReadMetadata(record, "appliedAffinityMask"), out var affinityMask))
                {
                    return 87;
                }
                request = new ProcessResourcePolicyBatchRequest(
                    processId,
                    DateTimeOffset.FromFileTime(checked((long)processStartKey)),
                    AffinityMask: affinityMask);
            }
            var result = processPolicyWriter.TryApplyBatch([request]).SingleOrDefault();
            var field = result?.Find(IsCpuSetsRecord(record)
                ? ProcessResourcePolicyBatchFields.CpuSets
                : ProcessResourcePolicyBatchFields.AffinityMask);
            return field is { Succeeded: true } ? 0 : unchecked((int)(field?.ErrorCode ?? 0));
        }

        return 87;
    }

    private HostManagerPlacementValueObservation ReadAutomaticPlacementValue(
        HostManagerAppliedPlacementReceipt placement,
        HostManagerAppliedRecord record)
    {
        var receiptDigest = HostManagerPlacementCoordinatorProjection.CreatePayloadDigest(
            "applied",
            placement,
            record);
        var previousDigest = HostManagerPlacementCoordinatorProjection.CreatePayloadDigest(
            "previous",
            placement,
            record);
        var observation = record.Kind switch
        {
            HostManagerAppliedRecordKinds.CpuAffinity => ObserveCpuPlacement(
                placement,
                record,
                receiptDigest,
                previousDigest),
            HostManagerAppliedRecordKinds.GpuPreference => ObserveGpuPreference(
                placement,
                record,
                receiptDigest,
                previousDigest),
            HostManagerAppliedRecordKinds.GpuShimPolicy => ObserveGpuShimPolicy(placement, record, receiptDigest, previousDigest),
            HostManagerAppliedRecordKinds.GpuRuntimeRebuildTrigger when ExternalGpuRuntimePlacementRecord.IsRecord(record)
                => ObserveExternalGpuRuntimePlacement(record, receiptDigest, previousDigest),
            _ => HostManagerPlacementObservation.Unchecked
        };
        var relation = observation.Status switch
        {
            NativePlacementObservationStatus.Unavailable => HostManagerPlacementValueRelation.Unavailable,
            NativePlacementObservationStatus.NotFoundOrExited => HostManagerPlacementValueRelation.NotFound,
            NativePlacementObservationStatus.Found when observation.CurrentDigest == receiptDigest =>
                HostManagerPlacementValueRelation.Applied,
            NativePlacementObservationStatus.Found when observation.CurrentDigest == previousDigest =>
                HostManagerPlacementValueRelation.Previous,
            NativePlacementObservationStatus.Found => HostManagerPlacementValueRelation.Foreign,
            _ => HostManagerPlacementValueRelation.Unavailable
        };
        return new HostManagerPlacementValueObservation(relation, observation.NativeErrorCode);
    }

    private HostManagerPlacementObservation ObserveExternalGpuRuntimePlacement(
        HostManagerAppliedRecord record, ulong receiptDigest, ulong previousDigest)
    {
        var identity = ReadAutomaticPlacementProcessIdentity(record);
        if (identity.Status == RecoveryReadStatus.Unavailable)
            return HostManagerPlacementObservation.Unavailable(identity.NativeErrorCode);
        if (!identity.Matches) return HostManagerPlacementObservation.NotFound(identity.NativeErrorCode);
        if (record.Metadata?.GetValueOrDefault(ExternalGpuRuntimePlacementRecord.SettledKey) == "false")
            return HostManagerPlacementObservation.FoundPrevious(previousDigest);
        if (!ExternalGpuRuntimePlacementRecord.TryReadResult(record, out var result) || result is null)
            return HostManagerPlacementObservation.Unavailable(87);
        if (ExternalGpuRuntimePlacementRecord.IsConfirmed(result))
            return HostManagerPlacementObservation.FoundReceipt(receiptDigest);
        return result.Status is RunningGpuPlacementActionStatuses.Skipped or RunningGpuPlacementActionStatuses.NotApplied
            ? HostManagerPlacementObservation.FoundPrevious(previousDigest)
            : HostManagerPlacementObservation.Unavailable(1460);
    }

    private HostManagerPlacementProcessIdentityObservation ReadAutomaticPlacementProcessIdentity(
        HostManagerAppliedRecord record)
    {
        if (!TryReadReceiptProcessIdentity(record, out var processId, out var processStartKey))
        {
            throw new InvalidDataException(
                "Automatic placement receipt has no exact process identity.");
        }

        var read = processPolicyWriter.ReadProcessInstanceForRecovery(processId);
        return read.Status switch
        {
            RecoveryReadStatus.Found when read.Value is not null =>
                new HostManagerPlacementProcessIdentityObservation(
                    MatchesExactReceiptProcessIdentity(
                        processId,
                        processStartKey,
                        read.Value),
                    read.Status,
                    read.NativeErrorCode),
            RecoveryReadStatus.NotFoundOrExited when read.Value is null =>
                new HostManagerPlacementProcessIdentityObservation(
                    false,
                    read.Status,
                    read.NativeErrorCode),
            RecoveryReadStatus.Unavailable when read.Value is null =>
                new HostManagerPlacementProcessIdentityObservation(
                    false,
                    read.Status,
                    read.NativeErrorCode),
            _ => throw new InvalidDataException(
                "Automatic placement process identity read has an invalid shape.")
        };
    }

    private async Task SavePlacementCheckpointAsync(
        HostManagerRollbackStateDocument state,
        string message,
        CancellationToken cancellationToken)
    {
        await SaveRollbackStateAsync(
            state with
            {
                LastRunAt = durableTimeSource.NextUtc(),
                Message = message
            },
            cancellationToken);
    }

    private static HostManagerRollbackStateDocument RemovePlacementRecord(
        HostManagerRollbackStateDocument state,
        HostManagerPlacementNativeIdentity identity)
    {
        var remaining = new List<HostManagerAppliedPlacementReceipt>();
        foreach (var placement in state.AppliedPlacements)
        {
            var records = placement.Records
                .Where(record => HostManagerPlacementCoordinatorProjection.CreateIdentity(
                        placement,
                        record) != identity)
                .ToArray();
            if (records.Length != 0)
            {
                remaining.Add(placement with { Records = records });
            }
        }
        return state with { AppliedPlacements = remaining };
    }

    private static void ValidateAutomaticPlacementAction(
        NativePlacementAction action,
        HostManagerPlacementDesiredProjection desired,
        HostManagerPlacementProjection applied,
        ulong observedAt,
        PlacementCoordinatorRuntimePlan plan,
        ISet<ulong> actionIds)
    {
        var flags = (NativePlacementActionFlags)action.Flags;
        var reason = (NativePlacementActionReason)action.ReasonMask;
        var expectedDeadline = checked(observedAt + (ulong)plan.HotPublish.ActionTimeoutMilliseconds);
        if (action.StructSize != NativePlacementCoordinatorSession.SizeOf<NativePlacementAction>()
            || action.ActionId == 0
            || !actionIds.Add(action.ActionId)
            || (flags & ~NativePlacementActionFlags.Known) != 0
            || (reason & ~NativePlacementActionReason.Known) != 0
            || reason == NativePlacementActionReason.None
            || action.DeadlineMilliseconds != expectedDeadline
            || action.Reserved != 0)
        {
            throw new InvalidDataException(
                $"Native placement action {action.ActionId} has an invalid common shape.");
        }

        if (action.Disposition == (uint)NativePlacementActionDisposition.Apply)
        {
            var projected = desired.Require(action);
            var input = projected.Input;
            if (action.TargetKey != input.TargetKey
                || action.RecordKey != input.RecordKey
                || action.ResourceKind != input.ResourceKind
                || action.PlacementKind != input.PlacementKind
                || action.DesiredDigest != input.DesiredDigest
                || action.PreviousDigest != 0
                || action.Priority != input.Priority
                || action.ProcessId != input.ProcessId
                || action.ProcessStartKey != input.ProcessStartKey
                || flags.HasFlag(NativePlacementActionFlags.ProcessIdentityValid)
                    != ((input.ValidMask & (ulong)NativePlacementDesiredValidity.ProcessIdentity) != 0))
            {
                throw new InvalidDataException(
                    $"Native placement action {action.ActionId} has an invalid apply shape.");
            }
            return;
        }
        if (action.Disposition != (uint)NativePlacementActionDisposition.Restore)
        {
            throw new InvalidDataException(
                $"Native placement action {action.ActionId} has an unknown disposition.");
        }

        var appliedRecord = applied.Require(action);
        var appliedInput = appliedRecord.Input;
        // A missing desired row leaves DesiredDigest as opaque native session history.
        var hasDesiredRecord = desired.TryGet(appliedRecord.Identity, out var desiredRecord);
        if (action.TargetKey != appliedInput.TargetKey
            || action.RecordKey != appliedInput.RecordKey
            || action.ResourceKind != appliedInput.ResourceKind
            || action.PlacementKind != appliedInput.PlacementKind
            || (hasDesiredRecord && action.DesiredDigest != desiredRecord.Input.DesiredDigest)
            || action.PreviousDigest != appliedInput.PreviousDigest
            || action.Priority != 0
            || action.ProcessId != appliedInput.ProcessId
            || action.ProcessStartKey != appliedInput.ProcessStartKey
            || flags.HasFlag(NativePlacementActionFlags.ProcessIdentityValid)
                != ((appliedInput.ValidMask & (ulong)NativePlacementAppliedValidity.ProcessIdentity) != 0))
        {
            throw new InvalidDataException(
                $"Native placement action {action.ActionId} has an invalid restore shape.");
        }
    }

    private static NativePlacementFeedback CreateAutomaticPlacementFeedback(
        NativePlacementAction action,
        NativePlacementFeedbackStatus status,
        int nativeErrorCode,
        ulong observedDigest)
        => new()
        {
            StructSize = NativePlacementCoordinatorSession.SizeOf<NativePlacementFeedback>(),
            Status = (uint)status,
            ActionId = action.ActionId,
            TargetKey = action.TargetKey,
            RecordKey = action.RecordKey,
            CompletedAtMilliseconds = MonotonicMilliseconds(),
            SystemErrorCode = unchecked((uint)nativeErrorCode),
            Flags = 0,
            ObservedDigest = observedDigest,
            ValidMask = observedDigest == 0
                ? 0
                : (ulong)NativePlacementFeedbackValidity.ObservedDigest
        };

    private static bool MatchesAutomaticProcessIdentity(
        HostManagerAutomaticPlacementProcess process,
        ProcessPlacementRecoverySnapshot snapshot)
        => snapshot.ProcessId == process.ProcessId
            && checked((ulong)snapshot.StartedAt.ToFileTime()) == process.ProcessStartKey;

    private static bool MatchesReceiptProcessIdentity(
        HostManagerAppliedRecord record,
        ulong processStartKey,
        ProcessPlacementRecoverySnapshot snapshot)
    {
        if (checked((ulong)snapshot.StartedAt.ToFileTime()) != processStartKey)
        {
            return false;
        }
        var expectedName = ReadMetadata(record, "processName");
        if (!string.IsNullOrWhiteSpace(expectedName)
            && !expectedName.Equals(snapshot.ProcessName, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        var expectedPath = ReadMetadata(record, "executablePath");
        return string.IsNullOrWhiteSpace(expectedPath)
            || expectedPath.Equals(snapshot.ExecutablePath ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    internal static bool MatchesExactReceiptProcessIdentity(
        int expectedProcessId,
        ulong expectedProcessStartKey,
        ProcessInstanceRecoverySnapshot snapshot)
        => snapshot.ProcessId == expectedProcessId
            && checked((ulong)snapshot.StartedAt.ToFileTime()) == expectedProcessStartKey;

    private static bool TryReadReceiptProcessIdentity(
        HostManagerAppliedRecord record,
        out int processId,
        out ulong processStartKey)
    {
        processId = 0;
        processStartKey = 0;
        if (!int.TryParse(
                ReadMetadata(record, "processId"),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out processId)
            || processId <= 0)
        {
            return false;
        }
        if (ulong.TryParse(
                ReadMetadata(record, "processStartKey"),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out processStartKey)
            && processStartKey != 0)
        {
            return true;
        }
        if (!long.TryParse(
                ReadMetadata(record, "processStartedAt"),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var unixMilliseconds)
            || unixMilliseconds <= 0)
        {
            return false;
        }
        try
        {
            processStartKey = checked((ulong)DateTimeOffset
                .FromUnixTimeMilliseconds(unixMilliseconds)
                .ToFileTime());
            return processStartKey != 0;
        }
        catch (ArgumentOutOfRangeException)
        {
            processStartKey = 0;
            return false;
        }
        catch (OverflowException)
        {
            processStartKey = 0;
            return false;
        }
    }

    private static bool IsCpuSetsRecord(HostManagerAppliedRecord record)
        => string.Equals(
            ReadMetadata(record, "affinityKind"),
            CpuSetsAffinityKind,
            StringComparison.OrdinalIgnoreCase);

    private static bool TryParseCpuSetIds(string? value, out IReadOnlyList<uint> ids)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            ids = [];
            return true;
        }
        var parsed = new List<uint>();
        foreach (var part in value.Split(
            ',',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!uint.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var id)
                || id == 0)
            {
                ids = [];
                return false;
            }
            parsed.Add(id);
        }
        ids = parsed.Distinct().Order().ToArray();
        return ids.Count == parsed.Count;
    }

    private static string FormatCpuSetIds(IReadOnlyList<uint> ids)
        => string.Join(',', ids.Distinct().Order());

    private static bool CpuSetIdsEqual(IReadOnlyList<uint> left, IReadOnlyList<uint> right)
        => left.Order().SequenceEqual(right.Order());

    private static bool TryParseAffinityMask(string? value, out long mask)
    {
        mask = 0;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }
        var parsed = value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? long.TryParse(
                value[2..],
                NumberStyles.HexNumber,
                CultureInfo.InvariantCulture,
                out mask)
            : long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out mask);
        return parsed && mask != 0;
    }

    private static string CreateCpuTopologyIdentity(CpuTopologySnapshot topology)
    {
        var ccds = string.Join(
            ';',
            topology.Ccds
                .OrderBy(static ccd => ccd.Index)
                .Select(static ccd => $"{ccd.Id}:{string.Join(',', ccd.PhysicalCoreIndexes.Order())}"));
        var logical = string.Join(
            ';',
            topology.LogicalProcessors
                .OrderBy(static item => item.Id)
                .Select(static item =>
                    $"{item.Id}:{item.ProcessorGroup}:{item.GroupRelativeIndex}:{item.PhysicalCoreId}:{item.CpuSetId}"));
        return CreateStableId(
            $"{topology.CpuName}|{topology.PhysicalCoreCount}|{topology.LogicalProcessorCount}|{ccds}|{logical}");
    }

    private static uint ToPlacementPriority(double canonicalScore)
    {
        if (!double.IsFinite(canonicalScore) || canonicalScore < 0)
        {
            throw new InvalidDataException("Automatic placement received an invalid canonical process score.");
        }
        return checked((uint)Math.Clamp(
            Math.Round(canonicalScore, MidpointRounding.AwayFromZero),
            0,
            uint.MaxValue));
    }

    private static ulong CreateForeignPlacementDigest(
        HostManagerAppliedPlacementReceipt placement,
        HostManagerAppliedRecord record,
        string currentValue,
        ulong receiptDigest,
        ulong previousDigest)
    {
        for (var suffix = 0; suffix < 4; suffix++)
        {
            var digest = NativeStableIdentity.CreateCaseInsensitiveKey(
                $"foreign\0{placement.ResourceKind}\0{placement.TargetId}\0{record.Kind}\0{record.RecordId}\0{currentValue}\0{suffix}");
            if (digest != 0 && digest != receiptDigest && digest != previousDigest)
            {
                return digest;
            }
        }
        throw new InvalidDataException("Automatic placement could not encode a foreign observation.");
    }

    private sealed record HostManagerBoundedPlacementCycle(
        IReadOnlyList<HostManagerPlacementDesired> Desired,
        IReadOnlyList<HostManagerAppliedPlacementReceipt> Applied);

    private sealed record HostManagerAutomaticPlacementApplyResult(
        HostManagerRollbackStateDocument State,
        NativePlacementFeedback Feedback,
        bool CanContinue = true);

    private enum HostManagerPlacementValueRelation : byte
    {
        Applied = 1,
        Previous = 2,
        Foreign = 3,
        NotFound = 4,
        Unavailable = 5
    }

    private readonly record struct HostManagerPlacementValueObservation(
        HostManagerPlacementValueRelation Relation,
        int NativeErrorCode);

    private readonly record struct HostManagerPlacementProcessIdentityObservation(
        bool Matches,
        RecoveryReadStatus Status,
        int NativeErrorCode);

    private readonly record struct HostManagerPlacementObservation(
        NativePlacementObservationStatus Status,
        ulong CurrentDigest,
        bool ReceiptMatches,
        int NativeErrorCode)
    {
        internal static HostManagerPlacementObservation Unchecked { get; } = new(
            NativePlacementObservationStatus.Unchecked,
            0,
            false,
            0);

        internal static HostManagerPlacementObservation FoundReceipt(ulong digest)
            => new(NativePlacementObservationStatus.Found, digest, true, 0);

        internal static HostManagerPlacementObservation FoundPrevious(ulong digest)
            => new(NativePlacementObservationStatus.Found, digest, false, 0);

        internal static HostManagerPlacementObservation FoundForeign(ulong digest)
            => new(NativePlacementObservationStatus.Found, digest, false, 0);

        internal static HostManagerPlacementObservation NotFound(int nativeErrorCode)
            => new(NativePlacementObservationStatus.NotFoundOrExited, 0, false, nativeErrorCode);

        internal static HostManagerPlacementObservation Unavailable(int nativeErrorCode)
            => new(NativePlacementObservationStatus.Unavailable, 0, false, nativeErrorCode);
    }
}
