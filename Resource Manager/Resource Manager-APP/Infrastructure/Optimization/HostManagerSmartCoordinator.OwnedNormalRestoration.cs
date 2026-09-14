using ResourceManager.App.Domain.Settings;
using ResourceManager.App.Infrastructure.GpuPlacement;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.Optimization.Transactions;

namespace ResourceManager.App.Infrastructure.Optimization;

public sealed partial class HostManagerSmartCoordinator
{
    private const ulong DirectNativeRestoreActionIdNamespace = 1UL << 62;
    private long ownedRestoreActionSequence;

    private async Task<bool> RestoreOwnedEffectsForNormalModeAsync(
        HostManagerCycleEffectAdmission effectAdmission,
        HostManagerProcessEffectValidationCycleSnapshot processEffectValidationScope,
        HostManagerTransactionJournalAdmission admission,
        uint maximumActions,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(
                runtimePlanProvider.Current.OptimizationMode.Mode,
                AppOptimizationModes.Normal,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Owner-only restoration is restricted to Normal optimization mode.");
        }

        var placementState = await LoadRollbackStateAsync(cancellationToken);
        if (GpuActionFacts.AvailablePlacementEffects(placementState.AppliedPlacements).Count != 0)
        {
            if (!effectAdmission.TryAcquire(
                    HostManagerCycleEffectKind.LegacyPlacementRestore,
                    out var placementPermit))
            {
                return false;
            }
            placementState = await RestorePlacementStateCoreAsync(
                placementPermit,
                cancellationToken);
            if (GpuActionFacts.AvailablePlacementEffects(placementState.AppliedPlacements).Count != 0)
            {
                return false;
            }
        }

        var snapshot = await nativeActionTransactions.AppliedOwnership.ReadSnapshotAsync(
            cancellationToken);
        if (snapshot.Records.Count == 0)
        {
            return (await admission.ReadSnapshotAsync(cancellationToken)).Records.Count == 0
                && !GpuActionFacts.HasUnsettledActions(placementState.AppliedPlacements);
        }

        authoritativeAppliedFactsRequired = true;
        var ownership = CreateAppliedOwnershipFactIndex(snapshot);
        if (ownership.MemoryProcessRecords.Count != 0)
        {
            if (!effectAdmission.TryAcquire(
                    HostManagerCycleEffectKind.NonAdaptedMemoryTransaction,
                    out var memoryPermit)
                || !await RestoreAllOwnedProcessMemoryAsync(
                    memoryPermit,
                    processEffectValidationScope,
                    ownership,
                    admission,
                    maximumActions,
                    cancellationToken))
            {
                return false;
            }

            snapshot = await nativeActionTransactions.AppliedOwnership.ReadSnapshotAsync(
                cancellationToken);
            ownership = CreateAppliedOwnershipFactIndex(snapshot);
        }

        if (ownership.ProcessRecords.Count != 0 || ownership.AdapterRecords.Count != 0)
        {
            if (!effectAdmission.TryAcquire(
                    HostManagerCycleEffectKind.NativeActionTransaction,
                    out var nativePermit))
            {
                return false;
            }

            var cycleStartedAt = durableTimeSource.NextUtc();
            foreach (var action in CreateOwnedNormalRestoreActions(ownership))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (nativePermit.NewPointOfNoReturnRemaining < 2)
                {
                    return false;
                }

                var result = await ExecuteNativeTransactionAsync(
                    nativePermit,
                    processEffectValidationScope,
                    action,
                    sample: null,
                    admission,
                    cancellationToken);
                ValidateNativeFeedback(
                    result.Feedback,
                    action,
                    cycleStartedAt.ToUnixTimeMilliseconds());
                if (result.Settlement is { } settlement)
                {
                    await CommitAcceptedNativeTransactionsAsync(
                        admission,
                        [settlement],
                        CancellationToken.None);
                }
                if (result.Feedback.Status ==
                    NativeSmartCoordinatorFeedbackStatus.StateUncertain)
                {
                    return false;
                }
            }
        }

        var finalOwnership = await nativeActionTransactions.AppliedOwnership.ReadSnapshotAsync(
            cancellationToken);
        var finalJournal = await admission.ReadSnapshotAsync(cancellationToken);
        return finalOwnership.Records.Count == 0 && finalJournal.Records.Count == 0
            && !GpuActionFacts.HasUnsettledActions(placementState.AppliedPlacements);
    }

    private async Task<bool> RestoreAllOwnedProcessMemoryAsync(
        HostManagerCycleEffectPermit permit,
        HostManagerProcessEffectValidationCycleSnapshot processEffectValidationScope,
        NativeAppliedOwnershipFactIndex ownership,
        HostManagerTransactionJournalAdmission admission,
        uint maximumActions,
        CancellationToken cancellationToken)
    {
        permit.Require(HostManagerCycleEffectKind.NonAdaptedMemoryTransaction);
        uint restored = 0;
        var schedulingGeneration = nativeActionTransactions.CurrentJournalPlan
            .HotPublish.ConfigurationGeneration;
        foreach (var pair in ownership.MemoryProcessRecords
            .OrderBy(static pair => pair.Key.ProcessId)
            .ThenBy(static pair => pair.Key.ProcessStartKey)
            .ThenBy(static pair => pair.Key.SoftwareKey)
            .ThenBy(static pair => pair.Key.TargetKey))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (restored >= maximumActions || permit.OwnedMemoryRestoreRemaining == 0)
            {
                return false;
            }

            var directive = new HostManagerNonAdaptedMemoryProcessDirective(
                schedulingGeneration,
                pair.Key.SoftwareKey,
                SoftwareId: null,
                checked((int)pair.Key.ProcessId),
                pair.Key.ProcessStartKey,
                pair.Key.TargetKey,
                SoftwareRank: 0,
                NativeMemoryMode.Normal,
                HostManagerNonAdaptedMemoryProcessAction.RestoreOwnedConstraints,
                TargetMemoryPriority: null);
            var decision = HostManagerNonAdaptedMemoryModeExecutionAdmission.Decide(
                directive,
                pair.Value,
                allowFreshApply: false);
            if (decision.Kind !=
                HostManagerNonAdaptedMemoryDirectiveDecisionKind.RestoreToBaseline)
            {
                throw new InvalidDataException(
                    "A Normal-mode process-memory owner did not project to a baseline restore.");
            }

            var result = await RestoreOwnedProcessMemoryPriorityAsync(
                permit,
                processEffectValidationScope,
                decision,
                pair.Value,
                NextOwnedRestoreActionId(NonAdaptedMemoryActionIdNamespace),
                admission,
                cancellationToken);
            if (!result.TransactionPrepared || result.IsBlocked)
            {
                return false;
            }
            restored++;
        }
        return true;
    }

    private IEnumerable<NativeSmartCoordinatorAction> CreateOwnedNormalRestoreActions(
        NativeAppliedOwnershipFactIndex ownership)
    {
        var configurationGeneration = nativeActionTransactions.CurrentJournalPlan
            .HotPublish.ConfigurationGeneration;
        var wakeAfterMilliseconds = checked((uint)(
            appliedNativeRuntimePlan?.SmartCoordinator.HotPublish
                .ReservationTimeoutMilliseconds ?? 0));
        foreach (var pair in ownership.ProcessRecords
            .OrderBy(static pair => pair.Key.ProcessId)
            .ThenBy(static pair => pair.Key.ProcessStartKey)
            .ThenBy(static pair => pair.Key.TargetKey)
            .ThenBy(static pair => pair.Key.SoftwareKey))
        {
            var actionId = NextOwnedRestoreActionId(DirectNativeRestoreActionIdNamespace);
            yield return CreateOwnedProcessNormalRestoreAction(
                pair.Key,
                pair.Value,
                configurationGeneration,
                actionId,
                wakeAfterMilliseconds);
        }
        foreach (var pair in ownership.AdapterRecords.OrderBy(static pair => pair.Key))
        {
            var actionId = NextOwnedRestoreActionId(DirectNativeRestoreActionIdNamespace);
            yield return CreateOwnedAdapterNormalRestoreAction(
                pair.Key,
                pair.Value,
                configurationGeneration,
                actionId,
                wakeAfterMilliseconds);
        }
    }

    internal static NativeSmartCoordinatorAction CreateOwnedProcessNormalRestoreAction(
        NativeAppliedOwnershipProcessIdentity identity,
        NativeAppliedOwnershipRecord record,
        ulong configurationGeneration,
        ulong actionId,
        uint wakeAfterMilliseconds)
    {
        var validMask = NativeSmartCoordinatorActionValidity.ProcessIdentity
            | NativeSmartCoordinatorActionValidity.ProcessGrade;
        if (identity.SoftwareKey != 0)
        {
            validMask |= NativeSmartCoordinatorActionValidity.SoftwareIdentity;
        }
        return new NativeSmartCoordinatorAction
        {
            StructSize = checked((uint)NativeSmartCoordinatorSession.SizeOf<NativeSmartCoordinatorAction>()),
            Flags = NativeSmartCoordinatorActionFlags.RequiresFeedback,
            ValidMask = validMask,
            ReasonMask = NativeSmartCoordinatorReason.FeatureDisabled,
            ActionId = actionId,
            PlanEpoch = actionId,
            ConfigurationGeneration = configurationGeneration,
            TargetKey = identity.TargetKey,
            SoftwareKey = identity.SoftwareKey,
            ProcessStartKey = identity.ProcessStartKey,
            ProcessId = identity.ProcessId,
            WakeAfterMilliseconds = wakeAfterMilliseconds,
            Scope = NativeSmartCoordinatorActionScope.ProcessPolicy,
            Disposition = NativeSmartCoordinatorActionDisposition.Restore,
            DomainMask = NativeSmartCoordinatorGradeDomains.Process,
            FromProcessGrade = ToNativeProcessGrade(record.CurrentGrades.ProcessGrade),
            ToProcessGrade = NativeSmartCoordinatorProcessGrade.Normal,
            FromCpuGrade = NativeSmartCoordinatorAdapterGrade.Normal,
            ToCpuGrade = NativeSmartCoordinatorAdapterGrade.Normal,
            FromGpuGrade = NativeSmartCoordinatorAdapterGrade.Normal,
            ToGpuGrade = NativeSmartCoordinatorAdapterGrade.Normal
        };
    }

    internal static NativeSmartCoordinatorAction CreateOwnedAdapterNormalRestoreAction(
        ulong softwareKey,
        NativeAppliedOwnershipRecord record,
        ulong configurationGeneration,
        ulong actionId,
        uint wakeAfterMilliseconds)
    {
        var grades = record.CurrentGrades;
        var domains = NativeSmartCoordinatorGradeDomains.None;
        var validMask = NativeSmartCoordinatorActionValidity.SoftwareIdentity;
        if ((grades.ValidMask & (uint)NativeAppliedOwnershipGradeValidity.Cpu) != 0)
        {
            domains |= NativeSmartCoordinatorGradeDomains.Cpu;
            validMask |= NativeSmartCoordinatorActionValidity.CpuGrade;
        }
        if ((grades.ValidMask & (uint)NativeAppliedOwnershipGradeValidity.Gpu) != 0)
        {
            domains |= NativeSmartCoordinatorGradeDomains.Gpu;
            validMask |= NativeSmartCoordinatorActionValidity.GpuGrade;
        }
        return new NativeSmartCoordinatorAction
        {
            StructSize = checked((uint)NativeSmartCoordinatorSession.SizeOf<NativeSmartCoordinatorAction>()),
            Flags = NativeSmartCoordinatorActionFlags.RequiresFeedback,
            ValidMask = validMask,
            ReasonMask = NativeSmartCoordinatorReason.FeatureDisabled,
            ActionId = actionId,
            PlanEpoch = actionId,
            ConfigurationGeneration = configurationGeneration,
            TargetKey = softwareKey,
            SoftwareKey = softwareKey,
            WakeAfterMilliseconds = wakeAfterMilliseconds,
            Scope = NativeSmartCoordinatorActionScope.AdapterSoftware,
            Disposition = NativeSmartCoordinatorActionDisposition.Restore,
            DomainMask = domains,
            FromProcessGrade = NativeSmartCoordinatorProcessGrade.Normal,
            ToProcessGrade = NativeSmartCoordinatorProcessGrade.Normal,
            FromCpuGrade = domains.HasFlag(NativeSmartCoordinatorGradeDomains.Cpu)
                ? ToNativeAdapterGrade(grades.CpuGrade)
                : NativeSmartCoordinatorAdapterGrade.Normal,
            ToCpuGrade = NativeSmartCoordinatorAdapterGrade.Normal,
            FromGpuGrade = domains.HasFlag(NativeSmartCoordinatorGradeDomains.Gpu)
                ? ToNativeAdapterGrade(grades.GpuGrade)
                : NativeSmartCoordinatorAdapterGrade.Normal,
            ToGpuGrade = NativeSmartCoordinatorAdapterGrade.Normal
        };
    }

    private ulong NextOwnedRestoreActionId(ulong actionNamespace)
    {
        var sequence = Interlocked.Increment(ref ownedRestoreActionSequence);
        if (sequence <= 0 || (ulong)sequence >= DirectNativeRestoreActionIdNamespace)
        {
            throw new InvalidOperationException(
                "The Host Manager owner-only restore action sequence was exhausted.");
        }
        return actionNamespace | checked((ulong)sequence);
    }

    private async Task RequireNoAppliedOwnershipAsync(CancellationToken cancellationToken)
    {
        var ownership = await nativeActionTransactions.AppliedOwnership.ReadSnapshotAsync(
            cancellationToken);
        if (ownership.Records.Count != 0)
        {
            throw new InvalidOperationException(
                "Normal mode was saved, but one or more owned optimization effects still require restoration.");
        }
        var state = await LoadRollbackStateAsync(cancellationToken);
        if (GpuActionFacts.HasPlacementEffects(state.AppliedPlacements))
        {
            throw new InvalidOperationException(
                "Normal mode was saved, but one or more owned hardware placements still require restoration.");
        }
        if (GpuActionFacts.HasUnsettledActions(state.AppliedPlacements))
        {
            throw new InvalidOperationException(
                "Normal mode was saved, but a recorded window action remains unconfirmed.");
        }
    }
}
