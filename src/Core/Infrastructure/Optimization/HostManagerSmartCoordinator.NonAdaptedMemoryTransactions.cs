using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.Optimization.Transactions;

namespace ResourceManager.App.Infrastructure.Optimization;

public sealed partial class HostManagerSmartCoordinator
{
    private const ulong NonAdaptedMemoryActionIdNamespace = 1UL << 63;

    private async Task<NonAdaptedMemoryTransactionBatchResult>
        ApplyNonAdaptedMemoryModeTransactionsAsync(
            HostManagerCycleEffectPermit permit,
            HostManagerProcessEffectValidationCycleSnapshot processEffectValidationScope,
            HostManagerSchedulingPlanBinding currentPlanBinding,
            HostManagerSample sample,
            NativeAppliedOwnershipFactIndex ownershipIndex,
            HostManagerTransactionJournalAdmission admission,
            uint maximumTransactions,
            bool allowFreshApply,
            CancellationToken cancellationToken)
    {
        permit.Require(HostManagerCycleEffectKind.NonAdaptedMemoryTransaction);
        if (maximumTransactions == 0)
        {
            RecordRemainingMemoryDirectiveSkips("transaction_limit");
            return default;
        }

        var journalPlan = nativeActionTransactions.CurrentJournalPlan;
        var ownershipPlan = nativeActionTransactions.AppliedOwnership.CurrentPlan;
        if (journalPlan.HostPlan.PlanEpoch != currentPlanBinding.HostPlanEpoch
            || !string.Equals(
                journalPlan.HostPlan.PlanSha256,
                currentPlanBinding.HostPlanSha256,
                StringComparison.Ordinal)
            || journalPlan.Recreate !=
                journalPlan.HostPlan.HostRecreate.TransactionJournal
            || journalPlan.HotPublish !=
                journalPlan.HostPlan.HotPublish.TransactionJournal
            || ownershipPlan.HostPlan.PlanEpoch != currentPlanBinding.HostPlanEpoch
            || !string.Equals(
                ownershipPlan.HostPlan.PlanSha256,
                currentPlanBinding.HostPlanSha256,
                StringComparison.Ordinal)
            || ownershipPlan.Recreate !=
                ownershipPlan.HostPlan.HostRecreate.AppliedOwnership
            || ownershipPlan.HotPublish !=
                ownershipPlan.HostPlan.HotPublish.AppliedOwnership)
        {
            throw new InvalidDataException(
                "Non-adapted memory-mode execution is not bound to the current transaction-journal and applied-ownership Host plan.");
        }
        var authority = schedulingAuthorityOwner.Capture();
        var admitted = HostManagerNonAdaptedMemoryModeExecutionAdmission.Admit(
            authority,
            NonAdaptedMemoryModeProjection,
            currentPlanBinding,
            sample.ProcessFacts);
        var ownerRecoveryBatch = admitted
            ?? HostManagerNonAdaptedMemoryModeExecutionAdmission
                .AdmitNonReadyOwnerRecovery(
                    authority,
                    currentPlanBinding,
                    sample.ProcessFacts);
        if (admitted is null) RecordRemainingMemoryDirectiveSkips("authority_not_ready");
        if (ownerRecoveryBatch is null)
        {
            return default;
        }

        var projectionGapRestores = HostManagerNonAdaptedMemoryModeExecutionAdmission
            .CreateProjectionGapRestores(
                ownerRecoveryBatch,
                sample.ProcessFacts,
                ownershipIndex);
        var restoreWork = new List<NonAdaptedMemoryTransactionWorkItem>(
            projectionGapRestores.Length + (admitted?.Processes.Length ?? 0));
        var freshApplyWork = new List<NonAdaptedMemoryTransactionWorkItem>(
            admitted?.Processes.Length ?? 0);
        for (var index = 0; index < projectionGapRestores.Length; index++)
        {
            var restore = projectionGapRestores[index];
            var actionId = NonAdaptedMemoryActionIdNamespace |
                checked((ulong)index + 1);
            restoreWork.Add(new(restore.Decision, restore.Ownership, actionId));
        }

        var unavailableOwnedProcessIncarnations =
            new HashSet<NativeAppliedOwnershipProcessIncarnation>();
        if (admitted is not null)
        {
            for (var index = 0; index < admitted.Processes.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var directive = admitted.Processes[index];
                if (await SkipPendingJournalTargetAsync(admission,
                        HostManagerNonAdaptedMemoryModeExecutionAdmission.CreatePrimary(directive),
                        NativeTransactionJournalDomain.PhysicalMemory, cancellationToken))
                {
                    RecordMemoryDirectiveSkip(directive, "pending_journal");
                    continue;
                }
                var incarnation = new NativeAppliedOwnershipProcessIncarnation(
                    checked((uint)directive.ProcessId),
                    directive.ProcessStartKey);
                var hasAnchor = ownershipIndex.ProcessAnchors.TryGetValue(
                    incarnation,
                    out var anchor);
                NativeAppliedOwnershipRecord? ownership;
                HostManagerNonAdaptedMemoryDirectiveDecision decision;
                if (hasAnchor && anchor.SoftwareKey != directive.SoftwareKey)
                {
                    if (!anchor.HasMemoryOwnership)
                    {
                        RecordMemoryDirectiveSkip(directive, "attribution_changed");
                        continue;
                    }
                    var ownedIdentity = CreateMemoryPolicyOwnershipIdentity(
                        anchor.SoftwareKey,
                        checked((uint)directive.ProcessId),
                        directive.ProcessStartKey);
                    if (!ownershipIndex.MemoryProcessRecords.TryGetValue(
                            ownedIdentity,
                            out var ownedRecord))
                    {
                        throw new InvalidDataException(
                            "The process-incarnation ownership anchor lost its exact memory record.");
                    }
                    ownership = ownedRecord;
                    decision = HostManagerNonAdaptedMemoryModeExecutionAdmission
                        .CreateAttributionDriftRestore(directive, in ownedRecord);
                }
                else
                {
                    var primary = HostManagerNonAdaptedMemoryModeExecutionAdmission
                        .CreatePrimary(directive);
                    var ownershipRead = await nativeActionTransactions.AppliedOwnership
                        .GetAsync(primary, cancellationToken);
                    ownership = ownershipRead.Status switch
                    {
                        NativeAppliedOwnershipStatus.Ok => ownershipRead.Record,
                        NativeAppliedOwnershipStatus.NoData => null,
                        _ => throw new InvalidDataException(
                            $"Process-memory ownership read failed with {ownershipRead.Status}.")
                    };
                    decision = HostManagerNonAdaptedMemoryModeExecutionAdmission.Decide(
                        directive,
                        ownership,
                        allowFreshApply);
                }
                if (ownership is not null
                    && decision.Kind is
                        HostManagerNonAdaptedMemoryDirectiveDecisionKind.None or
                        HostManagerNonAdaptedMemoryDirectiveDecisionKind.PagedFrozenClosed)
                {
                    var inspection = await InspectOwnedProcessMemoryPriorityAsync(
                        ownership.Value,
                        admission,
                        cancellationToken);
                    if (inspection == HostManagerProcessPolicyInspectionStatus.Owned)
                    {
                        RecordMemoryDirectiveSkip(directive,
                            decision.Kind == HostManagerNonAdaptedMemoryDirectiveDecisionKind.PagedFrozenClosed
                                ? "paged_frozen_closed" : "already_owned");
                        continue;
                    }
                    if (inspection == HostManagerProcessPolicyInspectionStatus.Unavailable)
                    {
                        // Only this exact process read is unavailable; keep its durable owner untouched
                        // while allowing independent process-memory work to use the shared cycle budget.
                        unavailableOwnedProcessIncarnations.Add(incarnation);
                        RecordMemoryDirectiveSkip(directive, "owned_memory_unavailable");
                        continue;
                    }
                    if (inspection is HostManagerProcessPolicyInspectionStatus.InvalidPayload
                        or HostManagerProcessPolicyInspectionStatus.InvalidTarget)
                    {
                        throw new InvalidDataException(
                            $"The process-memory ownership payload cannot be inspected: {inspection}.");
                    }
                    decision = HostManagerNonAdaptedMemoryModeExecutionAdmission.Decide(
                        directive,
                        ownership,
                        allowFreshApply: false);
                }
                if (decision.Kind is
                    HostManagerNonAdaptedMemoryDirectiveDecisionKind.None or
                    HostManagerNonAdaptedMemoryDirectiveDecisionKind.PagedFrozenClosed)
                {
                    RecordMemoryDirectiveSkip(directive,
                        decision.Kind == HostManagerNonAdaptedMemoryDirectiveDecisionKind.PagedFrozenClosed
                            ? "paged_frozen_closed"
                            : directive.Action == HostManagerNonAdaptedMemoryProcessAction.RestoreOwnedConstraints
                                ? "no_owned_constraints" : "fresh_apply_disabled");
                    continue;
                }

                var actionId = NonAdaptedMemoryActionIdNamespace |
                    checked((ulong)projectionGapRestores.Length + (ulong)index + 1);
                var work = new NonAdaptedMemoryTransactionWorkItem(
                    decision,
                    ownership,
                    actionId);
                if (decision.Kind ==
                    HostManagerNonAdaptedMemoryDirectiveDecisionKind.FreshApply)
                {
                    if (!processEffectValidationScope.Allows(
                            new HostManagerComputeProcessIdentity(
                                directive.ProcessId,
                                directive.ProcessStartKey)))
                    {
                        RecordMemoryDirectiveSkip(directive, "process_scope_excluded");
                        continue;
                    }
                    freshApplyWork.Add(work);
                }
                else if (decision.Kind is
                    HostManagerNonAdaptedMemoryDirectiveDecisionKind.RestoreToBaseline or
                    HostManagerNonAdaptedMemoryDirectiveDecisionKind
                        .AttributionDriftRestoreToBaseline)
                {
                    restoreWork.Add(work);
                }
                else
                {
                    throw new InvalidDataException(
                        $"Process-memory decision {decision.Kind} is not executable.");
                }
            }
        }
        if (unavailableOwnedProcessIncarnations.Count != 0)
        {
            freshApplyWork.RemoveAll(work =>
            {
                var directive = work.Decision.TransactionDirective;
                var unavailable = unavailableOwnedProcessIncarnations.Contains(
                    new NativeAppliedOwnershipProcessIncarnation(
                        checked((uint)directive.ProcessId),
                        directive.ProcessStartKey));
                if (unavailable) RecordMemoryDirectiveSkip(directive, "owned_memory_unavailable");
                return unavailable;
            });
        }

        uint transactionCount = 0;
        uint restoreTransactionCount = 0;
        foreach (var work in restoreWork)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (restoreTransactionCount >= maximumTransactions
                || permit.OwnedMemoryRestoreRemaining == 0)
            {
                RecordRemainingMemoryDirectiveSkips(restoreTransactionCount >= maximumTransactions
                    ? "transaction_limit" : "restore_budget_exhausted");
                return new(transactionCount, false);
            }

            var result = await RestoreOwnedProcessMemoryPriorityAsync(
                permit,
                processEffectValidationScope,
                work.Decision,
                work.Ownership ?? throw new InvalidDataException(
                    "A process-memory restore decision lost its exact ownership record."),
                work.ActionId,
                admission,
                cancellationToken);
            if (result.TransactionPrepared)
            {
                transactionCount++;
                restoreTransactionCount++;
            }
            if (result.IsBlocked)
            {
                RecordRemainingMemoryDirectiveSkips("memory_transaction_blocked");
                return new(transactionCount, true);
            }
        }

        uint freshTransactionCount = 0;
        foreach (var work in freshApplyWork)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (freshTransactionCount >= maximumTransactions
                || permit.NewPointOfNoReturnRemaining == 0)
            {
                RecordRemainingMemoryDirectiveSkips(freshTransactionCount >= maximumTransactions
                    ? "transaction_limit" : "new_ponr_budget_exhausted");
                break;
            }

            var result = await ApplyFreshProcessMemoryPriorityAsync(
                permit,
                processEffectValidationScope,
                work.Decision,
                work.ActionId,
                admission,
                cancellationToken);
            if (result.TransactionPrepared)
            {
                transactionCount++;
                freshTransactionCount++;
            }
            if (result.IsBlocked)
            {
                RecordRemainingMemoryDirectiveSkips("memory_transaction_blocked");
                return new(transactionCount, true);
            }
        }

        return new(transactionCount, false);
    }

    private async Task<HostManagerProcessPolicyInspectionStatus>
        InspectOwnedProcessMemoryPriorityAsync(
            NativeAppliedOwnershipRecord ownership,
            HostManagerTransactionJournalAdmission admission,
            CancellationToken cancellationToken)
    {
        var payloadReference = HostManagerAppliedOwnershipProjection
            .CreatePayloadReference(in ownership);
        var payloadBinding = HostManagerAppliedOwnershipProjection
            .CreatePayloadBinding(in ownership);
        var payload = await admission.ReadOwnedPayloadAsync(
            payloadReference,
            payloadBinding,
            cancellationToken);
        var inspection = processPolicyTransaction.InspectMemoryPriority(
            payload,
            checked((uint)ownership.CurrentGrades.ProcessGrade));
        if (inspection.ProcessId != checked((int)ownership.Primary.ProcessId)
            || inspection.ProcessStartKey != checked((long)ownership.Primary.ProcessStartKey))
        {
            throw new InvalidDataException(
                "The inspected process-memory payload changed its process incarnation.");
        }
        return inspection.Status;
    }

    private async Task<NonAdaptedMemoryTransactionResult>
        ApplyFreshProcessMemoryPriorityAsync(
            HostManagerCycleEffectPermit permit,
            HostManagerProcessEffectValidationCycleSnapshot processEffectValidationScope,
            HostManagerNonAdaptedMemoryDirectiveDecision decision,
            ulong actionId,
            HostManagerTransactionJournalAdmission admission,
            CancellationToken cancellationToken)
    {
        permit.Require(HostManagerCycleEffectKind.NonAdaptedMemoryTransaction);
        var directive = decision.TransactionDirective;
        if (!HostManagerProcessEffectValidationCyclePolicy.AllowsFreshProcessMemory(
                processEffectValidationScope))
        {
            return SkipMemoryDirective(directive, "process_scope_excluded");
        }
        if (await SkipPendingJournalTargetAsync(admission,
                HostManagerNonAdaptedMemoryModeExecutionAdmission.CreatePrimary(directive),
                NativeTransactionJournalDomain.PhysicalMemory, cancellationToken))
        {
            return SkipMemoryDirective(directive, "pending_journal");
        }
        var target = directive.TargetMemoryPriority
            ?? throw new InvalidDataException(
                "A fresh process-memory apply has no explicit target.");
        var capture = processPolicyTransaction.CaptureMemoryPriority(
            directive.ProcessId,
            target);
        if (TryRecordMemoryCaptureSkip(directive, capture, target))
        {
            return default;
        }
        if (permit.NewPointOfNoReturnRemaining == 0)
        {
            return SkipMemoryDirective(directive, "new_ponr_budget_exhausted");
        }
        if (!permit.TryReserveNewPointOfNoReturn(1, out var effectReservation)
            || effectReservation is null)
        {
            return SkipMemoryDirective(directive, "new_ponr_budget_exhausted");
        }
        using var effectReservationScope = effectReservation;

        var journalPlan = nativeActionTransactions.CurrentJournalPlan;
        var action = HostManagerProcessMemoryTransactionProjection.CreateApply(
            directive,
            journalPlan.HotPublish.ConfigurationGeneration,
            actionId,
            nativeHostSessionIncarnation);
        var journalSnapshot = await admission.ReadSnapshotAsync(cancellationToken);
        var preparedAt = CurrentUtcMilliseconds();
        var recoveryDeadline = CheckedAdd(
            preparedAt,
            checked((ulong)journalPlan.HotPublish.RecoveryDeadlineMilliseconds));
        var actionBinding = HostManagerProcessMemoryTransactionProjection
            .CreatePayloadBinding(
                action,
                capture.Payload,
                journalSnapshot.Header.JournalInstanceLow,
                journalSnapshot.Header.JournalInstanceHigh,
                preparedAt,
                checked((uint)journalPlan.HotPublish.MaximumRecoveryAttempts),
                recoveryDeadline);
        var provenance = NativeTransactionJournalPayloadProvenance.Create(
            actionBinding);
        var payloadReference = await admission.PersistPayloadAsync(
            actionBinding,
            capture.Payload,
            cancellationToken);
        HostManagerProcessEffectValidationAdmissionPermit? validationPermit = null;
        if (!TryBeginProcessEffectAdmission(
                processEffectValidationScope,
                HostManagerProcessEffectValidationFamily.NonAdaptedMemoryTransaction,
                "fresh-apply-pre-ponr",
                directive.ProcessId,
                directive.ProcessStartKey,
                out validationPermit))
        {
            _ = await admission.DeletePayloadAsync(
                payloadReference,
                actionBinding,
                CancellationToken.None);
            return SkipMemoryDirective(directive, "validation_admission_refused");
        }
        var prepareCompleted = false;
        var prepareRejectedWithoutMutation = false;
        NativeTransactionJournalRecord record;
        try
        {
            var prepare = HostManagerProcessMemoryTransactionProjection.CreatePrepare(
                action,
                capture.Payload,
                actionBinding,
                actionBinding,
                payloadReference,
                provenance,
                journalSnapshot.Header.JournalRevision,
                preparedAt);
            if (validationPermit is not null
                && !TryDeclareProcessEffectAdmission(
                    validationPermit,
                    prepare.Identity))
            {
                throw new InvalidOperationException(
                    "The process-memory apply validation target was not durably declared.");
            }
            var prepareStatus = await admission.PrepareAsync(
                prepare,
                cancellationToken);
            prepareRejectedWithoutMutation =
                prepareStatus != NativeTransactionJournalStatus.Ok;
            if (prepareStatus == NativeTransactionJournalStatus.InvalidTransition
                && await SkipPendingJournalTargetAsync(admission,
                    HostManagerNonAdaptedMemoryModeExecutionAdmission.CreatePrimary(directive),
                    NativeTransactionJournalDomain.PhysicalMemory, cancellationToken))
            {
                _ = await admission.DeletePayloadAsync(payloadReference, actionBinding, CancellationToken.None);
                return SkipMemoryDirective(directive, "pending_journal");
            }
            RequireJournalStatus(
                prepareStatus,
                "prepare-process-memory-apply");
            prepareCompleted = true;
            RecordMemoryDirectivePrepared(decision.DesiredDirective);
            record = await RequireJournalRecordAsync(
                admission,
                prepare.Identity,
                cancellationToken);
        }
        catch (Exception exception)
        {
            MarkProcessEffectAdmissionUnsettled(
                validationPermit,
                "process-memory-apply-prepare-failed");
            if (NativeTransactionJournalPayloadPreparePolicy.ShouldDeleteAfterFailure(
                    payloadOwnedByAttempt: true,
                    prepareCompleted,
                    prepareRejectedWithoutMutation,
                    exception))
            {
                _ = await admission.DeletePayloadAsync(
                    payloadReference,
                    actionBinding,
                    CancellationToken.None);
            }
            throw;
        }
        if (!TryCommitProcessEffectAdmission(
                validationPermit
                    ?? throw new InvalidOperationException(
                        "A scoped process-memory apply lost its validation permit."),
                record.Identity))
        {
            throw new InvalidOperationException(
                "The process-memory apply validation admission was not durably handed off.");
        }

        effectReservation.EnterPointOfNoReturn();
        var apply = processPolicyTransaction.ApplyMemoryPriority(
            capture.Payload,
            target);
        var effect = MapProcessApplyResult(apply);
        return await CompleteProcessMemoryTransactionAsync(
            action,
            admission,
            record,
            effect,
            capture.Payload,
            payloadReference,
            actionBinding,
            actionBinding,
            plannedTransition: null,
            deletePayloadWhenTerminal: effect.Status !=
                TransactionEffectStatus.Succeeded,
            cancellationToken);
    }

    private async Task<NonAdaptedMemoryTransactionResult>
        RestoreOwnedProcessMemoryPriorityAsync(
            HostManagerCycleEffectPermit permit,
            HostManagerProcessEffectValidationCycleSnapshot processEffectValidationScope,
            HostManagerNonAdaptedMemoryDirectiveDecision decision,
            NativeAppliedOwnershipRecord ownership,
            ulong actionId,
            HostManagerTransactionJournalAdmission admission,
            CancellationToken cancellationToken)
    {
        permit.Require(HostManagerCycleEffectKind.NonAdaptedMemoryTransaction);
        if (await SkipPendingJournalTargetAsync(admission, ownership.Primary,
                NativeTransactionJournalDomain.PhysicalMemory, cancellationToken))
        {
            return SkipMemoryDirective(decision.DesiredDirective, "pending_journal");
        }
        var journalPlan = nativeActionTransactions.CurrentJournalPlan;
        var action = decision.Kind ==
            HostManagerNonAdaptedMemoryDirectiveDecisionKind
                .AttributionDriftRestoreToBaseline
            ? HostManagerProcessMemoryTransactionProjection.CreateAttributionDriftRestore(
                decision.TransactionDirective,
                in ownership,
                journalPlan.HotPublish.ConfigurationGeneration,
                actionId,
                nativeHostSessionIncarnation,
                decision.CurrentOwnedMemoryPriority)
            : HostManagerProcessMemoryTransactionProjection.CreateRestore(
                decision.TransactionDirective,
                journalPlan.HotPublish.ConfigurationGeneration,
                actionId,
                nativeHostSessionIncarnation,
                decision.CurrentOwnedMemoryPriority);
        var payloadReference = HostManagerAppliedOwnershipProjection
            .CreatePayloadReference(in ownership);
        var payloadSourceBinding = HostManagerAppliedOwnershipProjection
            .CreatePayloadBinding(in ownership);
        var payloadProvenance = NativeTransactionJournalPayloadProvenance.Create(
            payloadSourceBinding);
        if (permit.OwnedMemoryRestoreRemaining == 0)
        {
            return SkipMemoryDirective(decision.DesiredDirective, "restore_budget_exhausted");
        }
        var payload = await admission.ReadPayloadAsync(
            payloadReference,
            payloadSourceBinding,
            cancellationToken);
        if (!permit.TryReserveOwnedMemoryRestore(1, out var recoveryReservation)
            || recoveryReservation is null)
        {
            return SkipMemoryDirective(decision.DesiredDirective, "restore_budget_exhausted");
        }
        using var recoveryReservationScope = recoveryReservation;
        var journalSnapshot = await admission.ReadSnapshotAsync(cancellationToken);
        var preparedAt = CurrentUtcMilliseconds();
        var recoveryDeadline = CheckedAdd(
            preparedAt,
            checked((ulong)journalPlan.HotPublish.RecoveryDeadlineMilliseconds));
        var actionBinding = HostManagerProcessMemoryTransactionProjection
            .CreatePayloadBinding(
                action,
                payload,
                journalSnapshot.Header.JournalInstanceLow,
                journalSnapshot.Header.JournalInstanceHigh,
                preparedAt,
                checked((uint)journalPlan.HotPublish.MaximumRecoveryAttempts),
                recoveryDeadline);
        var prepare = HostManagerProcessMemoryTransactionProjection.CreatePrepare(
            action,
            payload,
            actionBinding,
            payloadSourceBinding,
            payloadReference,
            payloadProvenance,
            journalSnapshot.Header.JournalRevision,
            preparedAt);
        HostManagerProcessEffectValidationAdmissionPermit? validationPermit = null;
        var sourceIdentity = CreateProcessEffectSourceIdentity(in ownership);
        if (!TryBeginProcessEffectRecoveryAdmission(
                HostManagerProcessEffectValidationFamily.NonAdaptedMemoryTransaction,
                "owned-restore-pre-ponr",
                decision.TransactionDirective.ProcessId,
                decision.TransactionDirective.ProcessStartKey,
                sourceIdentity,
                out validationPermit))
        {
            return SkipMemoryDirective(decision.DesiredDirective, "recovery_admission_refused");
        }
        NativeTransactionJournalRecord record;
        try
        {
            if (validationPermit is not null
                && !TryDeclareProcessEffectAdmission(
                    validationPermit,
                    prepare.Identity))
            {
                throw new InvalidOperationException(
                    "The process-memory restore validation target was not durably declared.");
            }
            var prepareStatus = await admission.PrepareAsync(prepare, cancellationToken);
            if (prepareStatus == NativeTransactionJournalStatus.InvalidTransition
                && await SkipPendingJournalTargetAsync(admission, ownership.Primary,
                    NativeTransactionJournalDomain.PhysicalMemory, cancellationToken))
            {
                return SkipMemoryDirective(decision.DesiredDirective, "pending_journal");
            }
            RequireJournalStatus(prepareStatus, "prepare-process-memory-restore");
            RecordMemoryDirectivePrepared(decision.DesiredDirective);
            record = await RequireJournalRecordAsync(
                admission,
                prepare.Identity,
                cancellationToken);
        }
        catch
        {
            MarkProcessEffectAdmissionUnsettled(
                validationPermit,
                "process-memory-restore-prepare-failed");
            throw;
        }
        if (!TryCommitProcessEffectAdmission(
                validationPermit
                    ?? throw new InvalidOperationException(
                        "A scoped process-memory restore lost its validation permit."),
                record.Identity))
        {
            throw new InvalidOperationException(
                "The process-memory restore validation admission was not durably handed off.");
        }
        var preparedSnapshot = await admission.ReadSnapshotAsync(cancellationToken);
        var transitionEvidence = HostManagerProcessMemoryTransactionProjection
            .CreateRestoreTransitionEvidence(
                action,
                payload,
                preparedSnapshot.Header,
                record,
                payloadReference,
                actionBinding,
                ownership);
        var transitionPlan = await nativeActionTransactions.AppliedOwnership
            .PlanTransitionAsync(
                ownership.Primary,
                transitionEvidence.Binding,
                transitionEvidence.Payload,
                cancellationToken);
        RequireOwnershipStatus(transitionPlan.Status, "plan-process-memory-restore");

        recoveryReservation.EnterPointOfNoReturn();
        var restore = processPolicyTransaction.RestoreMemoryPriority(
            payload,
            decision.CurrentOwnedMemoryPriority);
        var effect = MapProcessRestoreResult(restore);
        return await CompleteProcessMemoryTransactionAsync(
            action,
            admission,
            record,
            effect,
            payload,
            payloadReference,
            actionBinding,
            payloadSourceBinding,
            transitionPlan.Transition,
            deletePayloadWhenTerminal: effect.Status ==
                TransactionEffectStatus.Succeeded,
            cancellationToken);
    }

    private async Task<NonAdaptedMemoryTransactionResult>
        CompleteProcessMemoryTransactionAsync(
            HostManagerProcessMemoryTransactionAction action,
            HostManagerTransactionJournalAdmission admission,
            NativeTransactionJournalRecord record,
            TransactionEffectResult effect,
            byte[] payload,
            NativeTransactionJournalPayloadReference payloadReference,
            NativeTransactionJournalPayloadBinding actionBinding,
            NativeTransactionJournalPayloadBinding payloadSourceBinding,
            NativeAppliedOwnershipTransitionInput? plannedTransition,
            bool deletePayloadWhenTerminal,
            CancellationToken cancellationToken)
    {
        if (effect.Status == TransactionEffectStatus.ProcessExited)
        {
            await CompleteExitedProcessMemoryTransactionAsync(
                admission,
                record,
                effect,
                cancellationToken);
            return new(true, false);
        }
        if (effect.Status is TransactionEffectStatus.StateUncertain or
            TransactionEffectStatus.OwnershipLost)
        {
            await ApplyNonTerminalRecoveryEvidenceAsync(
                admission,
                record,
                effect,
                nativeActionTransactions.CurrentJournalPlan.HotPublish
                    .RetryDelayMilliseconds,
                cancellationToken);
            return new(true, true);
        }
        if (effect.Status is not (
            TransactionEffectStatus.Succeeded or
            TransactionEffectStatus.FailedUnchanged or
            TransactionEffectStatus.Rejected))
        {
            throw new InvalidDataException(
                $"Process-memory effect {effect.Status} cannot be completed.");
        }

        record = await MutateJournalRecordAsync(
            admission,
            record,
            NativeTransactionJournalMutationEvent.ConfirmEffectObserved,
            effect.SystemStatus,
            effect.SystemError,
            cancellationToken);
        if (effect.Status == TransactionEffectStatus.Succeeded)
        {
            if (plannedTransition is { } transition)
            {
                var completed = HostManagerAppliedOwnershipProjection
                    .CompleteTransition(
                        in transition,
                        in record,
                        CurrentUtcMilliseconds(record.UpdatedAtUtcMilliseconds));
                RequireOwnershipStatus(
                    await nativeActionTransactions.AppliedOwnership.TransitionAsync(
                        completed,
                        cancellationToken),
                    "complete-process-memory-restore");
            }
            else
            {
                var ownershipSnapshot = await nativeActionTransactions.AppliedOwnership
                    .ReadSnapshotAsync(cancellationToken);
                var journalSnapshot = await admission.ReadSnapshotAsync(
                    cancellationToken);
                var promote = HostManagerProcessMemoryTransactionProjection
                    .CreatePromote(
                        action,
                        payload,
                        journalSnapshot.Header,
                        record,
                        payloadReference,
                        actionBinding,
                        ownershipSnapshot.Header.LedgerRevision,
                        CurrentUtcMilliseconds(record.UpdatedAtUtcMilliseconds));
                RequireOwnershipStatus(
                    await nativeActionTransactions.AppliedOwnership.PromoteAsync(
                        promote,
                        cancellationToken),
                    "promote-process-memory-apply");
            }
        }

        var feedbackStatus = effect.Status switch
        {
            TransactionEffectStatus.Succeeded =>
                NativeTransactionJournalFeedbackStatus.Succeeded,
            TransactionEffectStatus.FailedUnchanged =>
                NativeTransactionJournalFeedbackStatus.FailedUnchanged,
            TransactionEffectStatus.Rejected =>
                NativeTransactionJournalFeedbackStatus.Rejected,
            _ => throw new InvalidOperationException(
                $"Process-memory terminal status {effect.Status} has no feedback mapping.")
        };
        var actualPriority = effect.Status == TransactionEffectStatus.Succeeded
            ? action.ToMemoryPriority
            : action.FromMemoryPriority;
        var feedbackSnapshot = await admission.ReadSnapshotAsync(cancellationToken);
        var stage = HostManagerProcessMemoryTransactionProjection.CreateFeedback(
            action,
            record,
            feedbackSnapshot.Header.JournalRevision,
            CurrentUtcMilliseconds(record.UpdatedAtUtcMilliseconds),
            feedbackStatus,
            actualPriority,
            effect.SystemStatus,
            effect.SystemError);
        RequireJournalStatus(
            await admission.StageFeedbackAsync(stage, cancellationToken),
            "stage-process-memory-feedback");
        var staged = await RequireJournalRecordAsync(
            admission,
            record.Identity,
            cancellationToken);
        var acknowledgementSnapshot = await admission.ReadSnapshotAsync(
            cancellationToken);
        var acknowledgement = HostManagerTransactionJournalProjection.CreateAck(
            in staged,
            acknowledgementSnapshot.Header.JournalRevision,
            NativeTransactionJournalAckResult.Accepted,
            CurrentUtcMilliseconds(staged.UpdatedAtUtcMilliseconds),
            retryNotBeforeUtcMilliseconds: 0,
            stableSystemStatus: 0,
            stableSystemError: 0);
        RequireJournalStatus(
            await admission.AcknowledgeAsync(acknowledgement, cancellationToken),
            "acknowledge-process-memory-feedback");
        if (deletePayloadWhenTerminal)
        {
            _ = await admission.DeletePayloadAsync(
                payloadReference,
                payloadSourceBinding,
                cancellationToken);
            if (admission.PayloadReconciliationRequired)
            {
                var journalSnapshot = await admission.ReadSnapshotAsync(
                    cancellationToken);
                await ReconcileNativePayloadsIfRequiredAsync(
                    admission,
                    journalSnapshot,
                    cancellationToken);
            }
        }
        return new(true, false);
    }

    private async Task CompleteExitedProcessMemoryTransactionAsync(
        HostManagerTransactionJournalAdmission admission,
        NativeTransactionJournalRecord record,
        TransactionEffectResult effect,
        CancellationToken cancellationToken)
    {
        await ApplyRecoveryOutcomeAsync(
            admission,
            record,
            NativeTransactionJournalRecoveryOutcome.ProcessExited,
            effect.SystemStatus,
            effect.SystemError,
            cancellationToken);
        var transitioned = await RequireJournalRecordAsync(
            admission,
            record.Identity,
            cancellationToken);
        await RemoveRecoveredOwnershipAsync(transitioned, cancellationToken);
        await CompleteAuthoritativeResyncAsync(
            admission,
            transitioned,
            cancellationToken);
        authoritativeAppliedFactsRequired = true;

        var payloadReference = HostManagerTransactionJournalProjection
            .CreatePayloadReference(in record);
        var payloadProvenance = HostManagerTransactionJournalProjection
            .CreatePayloadProvenance(in record);
        _ = await admission.DeletePayloadAsync(
            payloadReference,
            payloadProvenance,
            cancellationToken);
        if (admission.PayloadReconciliationRequired)
        {
            var journalSnapshot = await admission.ReadSnapshotAsync(cancellationToken);
            await ReconcileNativePayloadsIfRequiredAsync(
                admission,
                journalSnapshot,
                cancellationToken);
        }
    }

    internal bool TryRecordMemoryCaptureSkip(
        HostManagerNonAdaptedMemoryProcessDirective directive,
        HostManagerProcessPolicyCaptureResult capture,
        uint target)
    {
        if (GetMemoryCaptureSkipReason(directive, capture, target) is not { } reason) return false;
        RecordMemoryDirectiveSkip(directive, reason);
        return true;
    }

    internal static string? GetMemoryCaptureSkipReason(
        HostManagerNonAdaptedMemoryProcessDirective directive,
        HostManagerProcessPolicyCaptureResult capture,
        uint target)
    {
        if (capture.Status != HostManagerProcessPolicyCaptureStatus.Captured)
        {
            return capture.Status == HostManagerProcessPolicyCaptureStatus.Unavailable
                ? "capture_unavailable" : "capture_invalid_request";
        }
        if (capture.ProcessId != directive.ProcessId
            || capture.ProcessStartKey != checked((long)directive.ProcessStartKey)
            || capture.Fields != HostManagerProcessPolicyTransactionFields.MemoryPriority)
        {
            return "capture_identity_mismatch";
        }
        if (!HostManagerProcessPolicyRollbackPayloadCodec.TryDecode(
                capture.Payload,
                out var decoded)
            || decoded.ProcessId != directive.ProcessId
            || decoded.ProcessStartKey != checked((long)directive.ProcessStartKey)
            || decoded.Fields != HostManagerProcessPolicyTransactionFields.MemoryPriority
            || decoded.TargetMemoryPriority != target)
        {
            return "capture_payload_mismatch";
        }
        if (decoded.BaselineMemoryPriority == target)
        {
            return "baseline_at_target";
        }
        return null;
    }

    private NonAdaptedMemoryTransactionResult SkipMemoryDirective(
        HostManagerNonAdaptedMemoryProcessDirective directive, string reason)
    {
        RecordMemoryDirectiveSkip(directive, reason);
        return default;
    }

    private readonly record struct NonAdaptedMemoryTransactionResult(
        bool TransactionPrepared,
        bool IsBlocked);

    private readonly record struct NonAdaptedMemoryTransactionWorkItem(
        HostManagerNonAdaptedMemoryDirectiveDecision Decision,
        NativeAppliedOwnershipRecord? Ownership,
        ulong ActionId);

    private readonly record struct NonAdaptedMemoryTransactionBatchResult(
        uint TransactionCount,
        bool IsBlocked);
}
