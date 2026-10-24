using ResourceManager.App.Domain.Settings;
using ResourceManager.App.Domain.Optimization;
using ResourceManager.App.Infrastructure.GpuPlacement;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.Optimization.Transactions;

namespace ResourceManager.App.Infrastructure.Optimization;

public sealed partial class HostManagerSmartCoordinator
{
    private const ulong DirectNativeRestoreActionIdNamespace = 1UL << 62;
    private long ownedRestoreActionSequence;
    private (uint Scope, uint ProcessId, ulong StartKey, ulong Target, ulong Software)? lastNormalRestoreTarget;

    private async Task<bool> RestoreOwnedEffectsForNormalModeAsync(
        HostManagerCycleEffectAdmission effectAdmission,
        HostManagerProcessEffectValidationCycleSnapshot processEffectValidationScope,
        HostManagerTransactionJournalAdmission admission,
        uint maximumActions,
        CancellationToken cancellationToken,
        bool placementStateAvailable = true,
        bool gpuControlAvailable = true,
        bool forShutdown = false,
        Action? runPublicResourceLifecycle = null)
    {
        if (!(forShutdown && LifecycleState == HostManagerSmartCoordinatorLifecycleState.Closing)
            && !string.Equals(
                runtimePlanProvider.Current.OptimizationMode.Mode,
                AppOptimizationModes.Normal,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Owner-only restoration requires Normal mode or the admitted shutdown cleanup.");
        }

        ResetCpuExclusivityState();
        var placementsRestored = false;
        var snapshot = await nativeActionTransactions.AppliedOwnership.ReadSnapshotAsync(cancellationToken);
        authoritativeAppliedFactsRequired |= snapshot.Records.Count != 0;
        var ownership = CreateAppliedOwnershipFactIndex(snapshot);
        var restoreWork = ownership.MemoryProcessRecords.Values
            .Select(record => (Kind: NormalRestoreWorkKind.Memory, Record: record,
                Action: (NativeSmartCoordinatorAction?)null))
            .Concat(CreateOwnedNormalRestoreActions(ownership).Select(action =>
                (Kind: NormalRestoreWorkKind.Native,
                 Record: action.Scope == NativeSmartCoordinatorActionScope.AdapterSoftware
                    ? ownership.AdapterRecords[action.SoftwareKey]
                    : ownership.ProcessRecords[new(action.TargetKey, action.SoftwareKey,
                        action.ProcessId, action.ProcessStartKey)],
                 Action: (NativeSmartCoordinatorAction?)action)))
            .Append((Kind: NormalRestoreWorkKind.Placement, Record: default,
                Action: (NativeSmartCoordinatorAction?)null))
            .Concat(runPublicResourceLifecycle is null ? [] : new[]
            {
                (Kind: NormalRestoreWorkKind.PublicResource, Record: default(NativeAppliedOwnershipRecord),
                    Action: (NativeSmartCoordinatorAction?)null)
            })
            .OrderBy(item => NormalRestoreWorkKey(item.Kind, item.Record.Primary))
            .ToArray();
        // Rotate the first admitted opportunity each cycle, even when its write failed unchanged.
        // The cursor orders work only; durable ownership and the journal remain authoritative.
        var start = lastNormalRestoreTarget is { } last
            ? Array.FindIndex(restoreWork, item => NormalRestoreWorkKey(item.Kind, item.Record.Primary).CompareTo(last) > 0)
            : 0;
        if (start < 0) start = 0;
        var cycleStartedAt = durableTimeSource.NextUtc();
        uint attempted = 0;
        (uint Scope, uint ProcessId, ulong StartKey, ulong Target, ulong Software)? nextTarget = null;
        for (var offset = 0; offset < restoreWork.Length; offset++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var item = restoreWork[(start + offset) % restoreWork.Length];
            if (item.Kind == NormalRestoreWorkKind.PublicResource)
            {
                if (effectAdmission.NewPointOfNoReturnRemaining != 0)
                    nextTarget ??= NormalRestoreWorkKey(item.Kind, item.Record.Primary);
                runPublicResourceLifecycle!();
                continue;
            }
            if (item.Kind == NormalRestoreWorkKind.Placement)
            {
                if (placementStateAvailable)
                {
                    var placementState = await LoadRollbackStateAsync(cancellationToken);
                    if (effectAdmission.RecoveryRemaining != 0
                        && GpuActionFacts.AvailablePlacementEffects(placementState.AppliedPlacements)
                            .Any(item => gpuControlAvailable || !string.Equals(item.ResourceKind,
                                OptimizationResourceKinds.Gpu, StringComparison.OrdinalIgnoreCase))
                        && effectAdmission.TryAcquire(HostManagerCycleEffectKind.LegacyPlacementRestore,
                            out var placementPermit))
                    {
                        nextTarget ??= NormalRestoreWorkKey(item.Kind, item.Record.Primary);
                        placementState = await RestorePlacementStateCoreAsync(
                            placementPermit, cancellationToken, gpuControlAvailable);
                    }
                    placementsRestored = gpuControlAvailable
                        && !GpuActionFacts.HasPlacementEffects(placementState.AppliedPlacements)
                        && !GpuActionFacts.HasUnsettledActions(placementState.AppliedPlacements);
                }
                continue;
            }
            var domains = item.Action is { } nativeAction
                ? (NativeTransactionJournalDomain)nativeAction.DomainMask
                : NativeTransactionJournalDomain.PhysicalMemory;
            if (await SkipPendingJournalTargetAsync(admission, item.Record.Primary,
                    domains, cancellationToken))
            {
                continue;
            }
            if (attempted >= maximumActions || effectAdmission.RecoveryRemaining == 0)
            {
                continue;
            }
            var kind = item.Action is null
                ? HostManagerCycleEffectKind.NonAdaptedMemoryTransaction
                : HostManagerCycleEffectKind.NativeActionTransaction;
            if (!effectAdmission.TryAcquire(kind, out var permit)
                || item.Action is not null && permit.NewPointOfNoReturnRemaining == 0)
            {
                continue;
            }
            nextTarget ??= NormalRestoreWorkKey(item.Kind, item.Record.Primary);
            if (item.Action is not { } action)
            {
                await RestoreOwnedProcessMemoryForNormalAsync(permit,
                    processEffectValidationScope, item.Record, admission, cancellationToken);
            }
            else
            {
                var result = await ExecuteNativeTransactionAsync(
                    permit, processEffectValidationScope, action, sample: null,
                    admission, cancellationToken);
                ValidateNativeFeedback(result.Feedback, action,
                    cycleStartedAt.ToUnixTimeMilliseconds());
                if (result.Settlement is { } settlement)
                {
                    await CommitAcceptedNativeTransactionsAsync(
                        admission, [settlement], CancellationToken.None);
                }
            }
            attempted++;
        }

        if (nextTarget is { } next) lastNormalRestoreTarget = next;
        var finalOwnership = await nativeActionTransactions.AppliedOwnership.ReadSnapshotAsync(
            cancellationToken);
        var finalJournal = await admission.ReadSnapshotAsync(cancellationToken);
        return finalOwnership.Records.Count == 0 && finalJournal.Records.Count == 0
            && placementsRestored;
    }

    private enum NormalRestoreWorkKind { Placement, Memory, Native, PublicResource }

    private static (uint Scope, uint ProcessId, ulong StartKey, ulong Target, ulong Software)
        NormalRestoreWorkKey(NormalRestoreWorkKind kind, NativeAppliedOwnershipPrimaryIdentity identity)
        => kind switch
        {
            NormalRestoreWorkKind.Placement => (0, 0, 0, 0, 0),
            NormalRestoreWorkKind.PublicResource => (3, 0, 0, 0, 0),
            _ => (identity.Scope, identity.ProcessId, identity.ProcessStartKey,
                identity.TargetId, identity.SoftwareId)
        };

    private async Task RestoreOwnedProcessMemoryForNormalAsync(
        HostManagerCycleEffectPermit permit,
        HostManagerProcessEffectValidationCycleSnapshot processEffectValidationScope,
        NativeAppliedOwnershipRecord record,
        HostManagerTransactionJournalAdmission admission,
        CancellationToken cancellationToken)
    {
        var identity = record.Primary;
        var directive = new HostManagerNonAdaptedMemoryProcessDirective(
            nativeActionTransactions.CurrentJournalPlan.HotPublish.ConfigurationGeneration,
            identity.SoftwareId,
            SoftwareId: null,
            checked((int)identity.ProcessId),
            identity.ProcessStartKey,
            identity.TargetId,
            SoftwareRank: 0,
            NativeMemoryMode.Normal,
            HostManagerNonAdaptedMemoryProcessAction.RestoreOwnedConstraints,
            TargetMemoryPriority: null);
        var decision = HostManagerNonAdaptedMemoryModeExecutionAdmission.Decide(
            directive, record, allowFreshApply: false);
        if (decision.Kind != HostManagerNonAdaptedMemoryDirectiveDecisionKind.RestoreToBaseline)
        {
            throw new InvalidDataException(
                "A Normal-mode process-memory owner did not project to a baseline restore.");
        }
        _ = await RestoreOwnedProcessMemoryPriorityAsync(
            permit, processEffectValidationScope, decision, record,
            NextOwnedRestoreActionId(NonAdaptedMemoryActionIdNamespace),
            admission, cancellationToken);
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

    private async Task<bool> CanParkNormalSchedulingAsync(CancellationToken cancellationToken)
    {
        if (HasPendingGpuOwnerWork()
            || lastCycleActionBudget?.DeferredReason is not null
            || publicResourceNotifications is null
            || HasAvailablePublicResourceLifecycle()
            || !legacyStatePrepared
            || nativeWorkspace?.Snapshot.PendingCount > 0)
        {
            return false;
        }

        var ownership = await nativeActionTransactions.AppliedOwnership.ReadSnapshotAsync(
            cancellationToken);
        if (ownership.Records.Count != 0)
        {
            return false;
        }

        var state = await LoadRollbackStateAsync(cancellationToken);
        return !GpuActionFacts.HasPlacementEffects(state.AppliedPlacements)
            && !GpuActionFacts.HasUnsettledActions(state.AppliedPlacements);
    }
}
