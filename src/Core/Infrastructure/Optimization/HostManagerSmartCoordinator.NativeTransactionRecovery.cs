using ResourceManager.App.Application.Optimization;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.Optimization.Transactions;

namespace ResourceManager.App.Infrastructure.Optimization;

public sealed partial class HostManagerSmartCoordinator
{
    private async Task<NativeTransactionRecoveryResult> RecoverNativeTransactionsAsync(
        HostManagerCycleEffectAdmission effectAdmission,
        CancellationToken cancellationToken)
    {
        if (!effectAdmission.TryAcquire(
                HostManagerCycleEffectKind.NativeTransactionRecovery,
                out var permit))
        {
            return new NativeTransactionRecoveryResult(false, false);
        }
        permit.Require(HostManagerCycleEffectKind.NativeTransactionRecovery);
        await nativeActionTransactions.EnsureReadyAsync(cancellationToken);
        HostManagerTransactionJournalAdmission? admission = null;
        var acquired = nativeActionTransactions.JournalState switch
        {
            HostManagerTransactionJournalRuntimeState.ReadyForExecution =>
                nativeActionTransactions.TryAcquireExecutionAdmission(out admission),
            HostManagerTransactionJournalRuntimeState.RecoveryRequired =>
                nativeActionTransactions.TryAcquireRecoveryAdmission(out admission),
            _ => false
        };
        if (!acquired || admission is null)
        {
            return new NativeTransactionRecoveryResult(true, false);
        }

        await using (admission)
        {
            var snapshot = await admission.ReadSnapshotAsync(cancellationToken);
            var recoveryOwnership = await nativeActionTransactions.AppliedOwnership
                .ReadSnapshotAsync(cancellationToken);
            if (!processEffectValidationScopeAuthorityOwner
                    .ReconcileDeclaredNativeHandoff(
                        CreateAuthoritativeProcessEffectHandoffs(
                            snapshot,
                            recoveryOwnership)))
            {
                return new NativeTransactionRecoveryResult(true, true);
            }
            await ReconcileNativePayloadsIfRequiredAsync(
                admission,
                snapshot,
                cancellationToken);
            var changed = false;
            foreach (var record in snapshot.Records)
            {
                if (record.Scope != (uint)NativeTransactionJournalScope.Resource &&
                    record.Phase == (uint)NativeTransactionJournalPhase.RecoveryRetryPending &&
                    record.RetryNotBeforeUtcMilliseconds > CurrentUtcMilliseconds())
                {
                    continue;
                }
                var outcome = await RecoverNativeTransactionRecordAsync(
                    permit,
                    admission,
                    record,
                    cancellationToken);
                changed |= outcome;
            }

            if (changed)
            {
                var settled = await admission.ReadSnapshotAsync(cancellationToken);
                await ReconcileNativePayloadsIfRequiredAsync(
                    admission, settled, cancellationToken);
            }
            return new NativeTransactionRecoveryResult(false, changed);
        }
    }

    private async Task<bool> RecoverNativeTransactionRecordAsync(
        HostManagerCycleEffectPermit permit,
        HostManagerTransactionJournalAdmission admission,
        NativeTransactionJournalRecord record,
        CancellationToken cancellationToken)
    {
        if (record.Scope == (uint)NativeTransactionJournalScope.Resource)
        {
            if (!permit.TryReserveRecovery(1, out _)) return false;
            return await RecoverResourceTransactionRecordAsync(
                admission,
                record,
                cancellationToken);
        }
        if (record.Phase == (uint)NativeTransactionJournalPhase.RecoveryBlocked &&
            record.DomainMask == 0)
        {
            // The durable decoder retained a target identity, but no trustworthy effect proof.
            return false;
        }
        if (record.Phase == (uint)NativeTransactionJournalPhase.AuthoritativeResyncPending)
        {
            if (!permit.TryReserveRecovery(1, out _)) return false;
            await RemoveRecoveredOwnershipAsync(record, cancellationToken);
            await CompleteAuthoritativeResyncAsync(admission, record, cancellationToken);
            return true;
        }

        var ownership = await nativeActionTransactions.AppliedOwnership.GetAsync(
            CreateAppliedOwnershipPrimary(in record), cancellationToken);
        var ownershipChanged = ownership.Status == NativeAppliedOwnershipStatus.Ok &&
            !RecoveryPayloadMatchesOwnership(record, ownership.Record);
        var exited = record.Phase == (uint)NativeTransactionJournalPhase.RecoveryBlocked &&
            record.Scope == (uint)NativeTransactionJournalScope.Process &&
            ProcessInstanceRecovery.IsConfirmedExited(
                processPolicyWriter.ReadProcessInstanceForRecovery(
                    checked((int)record.Identity.ProcessId)),
                record.Identity.ProcessId,
                record.Identity.ProcessStartKey);
        if (ownershipChanged || exited)
        {
            if (!permit.TryReserveRecovery(1, out _)) return false;
            await ApplyRecoveryOutcomeAsync(admission, record,
                ownershipChanged ? NativeTransactionJournalRecoveryOutcome.OwnershipLost
                    : NativeTransactionJournalRecoveryOutcome.ProcessExited,
                0, 0, cancellationToken);
            await RemoveRecoveredOwnershipAsync(record, cancellationToken);
            await CompleteAuthoritativeResyncAsync(admission, record, cancellationToken);
            admission.RequirePayloadReconciliation();
            return true;
        }
        if (record.Phase == (uint)NativeTransactionJournalPhase.RecoveryBlocked)
        {
            return false;
        }

        if (!permit.TryReserveRecovery(1, out _)) return false;
        NativeTransactionJournalPayloadReference payloadReference;
        NativeTransactionJournalPayloadProvenance payloadProvenance;
        byte[] payload;
        try
        {
            payloadReference = HostManagerTransactionJournalProjection.CreatePayloadReference(
                in record);
            payloadProvenance = HostManagerTransactionJournalProjection.CreatePayloadProvenance(
                in record);
            payload = await admission.ReadPayloadAsync(
                payloadReference,
                payloadProvenance,
                cancellationToken);
        }
        catch (Exception exception) when (
            exception is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            await ApplyRecoveryOutcomeAsync(
                admission,
                record,
                NativeTransactionJournalRecoveryOutcome.InvalidProof,
                0,
                unchecked((uint)exception.HResult),
                cancellationToken);
            return true;
        }

        var effect = await RestoreRecoveredPayloadAsync(
            record,
            payload,
            nativeActionTransactions.CurrentJournalPlan.Recreate.PayloadByteBudget,
            cancellationToken);
        var outcome = effect.Status switch
        {
            TransactionEffectStatus.Succeeded =>
                NativeTransactionJournalRecoveryOutcome.Restored,
            TransactionEffectStatus.OwnershipLost =>
                NativeTransactionJournalRecoveryOutcome.OwnershipLost,
            TransactionEffectStatus.Rejected =>
                NativeTransactionJournalRecoveryOutcome.InvalidProof,
            TransactionEffectStatus.FailedUnchanged =>
                NativeTransactionJournalRecoveryOutcome.RetryableFailure,
            TransactionEffectStatus.ProcessExited =>
                NativeTransactionJournalRecoveryOutcome.ProcessExited,
            _ => NativeTransactionJournalRecoveryOutcome.Unavailable
        };
        await ApplyRecoveryOutcomeAsync(
            admission,
            record,
            outcome,
            effect.SystemStatus,
            effect.SystemError,
            cancellationToken);

        if (outcome is NativeTransactionJournalRecoveryOutcome.Restored or
            NativeTransactionJournalRecoveryOutcome.AlreadyRestored or
            NativeTransactionJournalRecoveryOutcome.OwnershipLost or
            NativeTransactionJournalRecoveryOutcome.ProcessExited)
        {
            var transitioned = await RequireJournalRecordAsync(
                admission,
                record.Identity,
                cancellationToken);
            await RemoveRecoveredOwnershipAsync(transitioned, cancellationToken);
            await CompleteAuthoritativeResyncAsync(
                admission,
                transitioned,
                cancellationToken);
            if (outcome != NativeTransactionJournalRecoveryOutcome.OwnershipLost)
            {
                _ = await admission.DeletePayloadAsync(
                    payloadReference,
                    payloadProvenance,
                    cancellationToken);
            }
            return true;
        }

        return true;
    }

    private async Task<bool> RecoverResourceTransactionRecordAsync(
        HostManagerTransactionJournalAdmission admission,
        NativeTransactionJournalRecord record,
        CancellationToken cancellationToken)
    {
        // A trim releases memory; it has no original value or ownership to restore.
        // Its former scheduler session cannot consume feedback after a restart.
        await ApplyRecoveryOutcomeAsync(
            admission,
            record,
            NativeTransactionJournalRecoveryOutcome.AlreadyRestored,
            record.StableSystemStatus,
            record.StableSystemError,
            cancellationToken);
        admission.RequirePayloadReconciliation();
        return true;
    }

    private async Task<TransactionEffectResult> RestoreRecoveredPayloadAsync(
        NativeTransactionJournalRecord record,
        byte[] payload,
        long payloadByteBudget,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.FromUnixTimeMilliseconds(
            checked((long)CheckedAdd(
                CurrentUtcMilliseconds(),
                checked((ulong)appliedNativeRuntimePlan!.SmartCoordinator.HotPublish
                    .ReservationTimeoutMilliseconds))));
        if ((NativeTransactionJournalScope)record.Scope == NativeTransactionJournalScope.Process)
        {
            var inspection = processPolicyTransaction.InspectForRecovery(
                payload,
                record.ProcessFromGrade,
                record.ProcessToGrade);
            switch (inspection.Status)
            {
                case HostManagerProcessPolicyInspectionStatus.ProcessExited:
                    return TransactionEffectResult.ProcessExited();
                case HostManagerProcessPolicyInspectionStatus.Baseline:
                    return TransactionEffectResult.Succeeded();
                case HostManagerProcessPolicyInspectionStatus.Foreign:
                case HostManagerProcessPolicyInspectionStatus.IdentityChanged:
                    return TransactionEffectResult.OwnershipLost();
                case HostManagerProcessPolicyInspectionStatus.InvalidPayload:
                case HostManagerProcessPolicyInspectionStatus.InvalidTarget:
                    return TransactionEffectResult.Rejected();
                case HostManagerProcessPolicyInspectionStatus.Unavailable:
                    return TransactionEffectResult.StateUncertain();
                case HostManagerProcessPolicyInspectionStatus.Owned:
                    break;
                default:
                    throw new InvalidDataException(
                        "Process-policy recovery inspection returned an unknown status.");
            }

            if (!TryAuthorizeProcessEffectRecovery(
                    ResolveRecoveryProcessEffectValidationFamily(record),
                    record.Identity))
            {
                return TransactionEffectResult.StateUncertain();
            }
            return MapProcessRecoveryRestoreResult(
                processPolicyTransaction.RestoreForRecovery(
                    payload,
                    record.ProcessFromGrade,
                    record.ProcessToGrade));
        }
        return TransactionEffectResult.Rejected();
    }

    private static HostManagerProcessEffectValidationFamily
        ResolveRecoveryProcessEffectValidationFamily(
        NativeTransactionJournalRecord record)
        => record.DomainMask == (uint)NativeTransactionJournalDomain.PhysicalMemory
            ? HostManagerProcessEffectValidationFamily.NonAdaptedMemoryTransaction
            : HostManagerProcessEffectValidationFamily.NativeProcessPolicyTransaction;

    private static TransactionEffectResult MapProcessRecoveryRestoreResult(
        HostManagerProcessPolicyRestoreResult result)
        => result.Status switch
        {
            HostManagerProcessPolicyRestoreStatus.Restored or
                HostManagerProcessPolicyRestoreStatus.AlreadyRestored =>
                TransactionEffectResult.Succeeded(),
            HostManagerProcessPolicyRestoreStatus.ProcessExited =>
                TransactionEffectResult.ProcessExited(),
            HostManagerProcessPolicyRestoreStatus.OwnershipLost =>
                TransactionEffectResult.OwnershipLost(),
            HostManagerProcessPolicyRestoreStatus.InvalidPayload or
                HostManagerProcessPolicyRestoreStatus.InvalidTarget or
                HostManagerProcessPolicyRestoreStatus.InvalidGrade or
                HostManagerProcessPolicyRestoreStatus.Level4Rejected =>
                TransactionEffectResult.Rejected(),
            HostManagerProcessPolicyRestoreStatus.Failed =>
                TransactionEffectResult.FailedUnchanged(),
            _ => TransactionEffectResult.StateUncertain()
        };

    private async Task ApplyRecoveryOutcomeAsync(
        HostManagerTransactionJournalAdmission admission,
        NativeTransactionJournalRecord record,
        NativeTransactionJournalRecoveryOutcome outcome,
        uint systemStatus,
        uint systemError,
        CancellationToken cancellationToken)
    {
        var snapshot = await admission.ReadSnapshotAsync(cancellationToken);
        var observedAt = Math.Max(CurrentUtcMilliseconds(), record.UpdatedAtUtcMilliseconds);
        var retryNotBefore = outcome is NativeTransactionJournalRecoveryOutcome.RetryableFailure or
            NativeTransactionJournalRecoveryOutcome.Unavailable
                ? CheckedAdd(
                    observedAt,
                    checked((ulong)nativeActionTransactions.CurrentJournalPlan.HotPublish
                        .RetryDelayMilliseconds))
                : 0;
        var evidence = HostManagerTransactionJournalProjection.CreateRecoveryEvidence(
            in record,
            snapshot.Header.JournalRevision,
            outcome,
            observedAt,
            retryNotBefore,
            systemStatus,
            systemError);
        RequireJournalStatus(
            await admission.ApplyRecoveryEvidenceAsync(evidence, cancellationToken),
            $"recovery-{outcome}");
    }

    private async Task RemoveRecoveredOwnershipAsync(
        NativeTransactionJournalRecord record,
        CancellationToken cancellationToken)
    {
        var primary = CreateAppliedOwnershipPrimary(in record);
        var read = await nativeActionTransactions.AppliedOwnership.GetAsync(
            primary,
            cancellationToken);
        if (read.Status == NativeAppliedOwnershipStatus.NoData)
        {
            return;
        }
        if (read.Status != NativeAppliedOwnershipStatus.Ok)
        {
            throw new InvalidDataException(
                $"Applied ownership recovery read failed with {read.Status}.");
        }

        var ownershipRecord = read.Record;
        if (!RecoveryPayloadMatchesOwnership(record, ownershipRecord))
        {
            // A later transaction owns this target. Only settle the older journal record.
            return;
        }

        var snapshot = await nativeActionTransactions.AppliedOwnership.ReadSnapshotAsync(
            cancellationToken);
        var remove = HostManagerAppliedOwnershipProjection.CreateRecoveryRemove(
            in ownershipRecord,
            snapshot.Header.LedgerRevision,
            Math.Max(CurrentUtcMilliseconds(), ownershipRecord.UpdatedAtUtcMilliseconds));
        RequireOwnershipStatus(
            await nativeActionTransactions.AppliedOwnership.RemoveAsync(
                remove,
                cancellationToken),
            "recovery-remove");
    }

    private static bool RecoveryPayloadMatchesOwnership(
        NativeTransactionJournalRecord record,
        NativeAppliedOwnershipRecord ownership)
        => ownership.Payload.Slot == record.PayloadSlot &&
            ownership.Payload.Generation == record.PayloadGeneration &&
            ownership.Payload.Length == record.PayloadLength &&
            ownership.Payload.DigestLow == record.PayloadDigestLow &&
            ownership.Payload.DigestHigh == record.PayloadDigestHigh;

    private async Task CompleteAuthoritativeResyncAsync(
        HostManagerTransactionJournalAdmission admission,
        NativeTransactionJournalRecord record,
        CancellationToken cancellationToken)
    {
        var current = await RequireJournalRecordAsync(
            admission,
            record.Identity,
            cancellationToken);
        if (current.Phase != (uint)NativeTransactionJournalPhase.AuthoritativeResyncPending)
        {
            return;
        }
        var journal = await admission.ReadSnapshotAsync(cancellationToken);
        var ownership = await nativeActionTransactions.AppliedOwnership.ReadSnapshotAsync(
            cancellationToken);
        var evidence = HostManagerTransactionJournalProjection.CreateRecoveryEvidence(
            in current,
            journal.Header.JournalRevision,
            NativeTransactionJournalRecoveryOutcome.AuthoritativeResyncCompleted,
            Math.Max(CurrentUtcMilliseconds(), current.UpdatedAtUtcMilliseconds),
            0,
            0,
            0,
            ownership.Header.LedgerRevision);
        RequireJournalStatus(
            await admission.ApplyRecoveryEvidenceAsync(evidence, cancellationToken),
            "authoritative-resync-completed");
    }

    private static NativeAppliedOwnershipPrimaryIdentity CreateAppliedOwnershipPrimary(
        in NativeTransactionJournalRecord record)
        => (NativeTransactionJournalScope)record.Scope switch
        {
            NativeTransactionJournalScope.Process => new NativeAppliedOwnershipPrimaryIdentity
            {
                Scope = (uint)NativeAppliedOwnershipScope.Process,
                TargetId = record.Identity.TargetId,
                SoftwareId = record.Identity.SoftwareId,
                ProcessStartKey = record.Identity.ProcessStartKey,
                ProcessId = record.Identity.ProcessId
            },
            _ => throw new InvalidDataException(
                $"Journal scope {record.Scope} is not an applied ownership scope.")
        };

    private readonly record struct NativeTransactionRecoveryResult(
        bool IsBlocked,
        bool Changed);

}
