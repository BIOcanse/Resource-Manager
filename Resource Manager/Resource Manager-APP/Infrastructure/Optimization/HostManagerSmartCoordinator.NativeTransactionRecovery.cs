using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.Optimization.NativeScheduling;
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
            return new NativeTransactionRecoveryResult(
                nativeActionTransactions.JournalState !=
                    HostManagerTransactionJournalRuntimeState.ReadyForExecution,
                false);
        }
        permit.Require(HostManagerCycleEffectKind.NativeTransactionRecovery);
        await nativeActionTransactions.EnsureReadyAsync(cancellationToken);
        if (nativeActionTransactions.JournalState == HostManagerTransactionJournalRuntimeState.ReadyForExecution)
        {
            if (!nativeActionTransactions.TryAcquireExecutionAdmission(out var executionAdmission) ||
                executionAdmission is null)
            {
                return new NativeTransactionRecoveryResult(true, true);
            }
            await using (executionAdmission)
            {
                var readySnapshot =
                    await executionAdmission.ReadSnapshotAsync(cancellationToken);
                var readyOwnership = await nativeActionTransactions.AppliedOwnership
                    .ReadSnapshotAsync(cancellationToken);
                if (!processEffectValidationScopeAuthorityOwner
                        .ReconcileDeclaredNativeHandoff(
                            CreateAuthoritativeProcessEffectHandoffs(
                                readySnapshot,
                                readyOwnership)))
                {
                    return new NativeTransactionRecoveryResult(true, true);
                }
                await ReconcileNativePayloadsIfRequiredAsync(
                    executionAdmission,
                    readySnapshot,
                    cancellationToken);
                return new NativeTransactionRecoveryResult(false, false);
            }
        }
        if (nativeActionTransactions.JournalState != HostManagerTransactionJournalRuntimeState.RecoveryRequired)
        {
            return new NativeTransactionRecoveryResult(true, true);
        }
        if (!nativeActionTransactions.TryAcquireRecoveryAdmission(out var admission) ||
            admission is null)
        {
            return new NativeTransactionRecoveryResult(true, true);
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
            var blocked = snapshot.Header.RecoveryBlockedCount != 0;
            var changed = false;
            foreach (var record in snapshot.Records)
            {
                if (record.Phase == (uint)NativeTransactionJournalPhase.RecoveryBlocked)
                {
                    blocked = true;
                    continue;
                }
                if (record.Phase == (uint)NativeTransactionJournalPhase.RecoveryRetryPending &&
                    record.RetryNotBeforeUtcMilliseconds > CurrentUtcMilliseconds())
                {
                    continue;
                }
                if (!permit.TryReserveRecovery(1, out _))
                {
                    blocked = true;
                    break;
                }

                var outcome = await RecoverNativeTransactionRecordAsync(
                    admission,
                    record,
                    cancellationToken);
                changed |= outcome.Changed;
                blocked |= outcome.Blocked;
                snapshot = await admission.ReadSnapshotAsync(cancellationToken);
            }

            snapshot = await admission.ReadSnapshotAsync(cancellationToken);
            blocked |= snapshot.Header.RecoveryBlockedCount != 0 ||
                snapshot.Header.EffectInvocationUncertainCount != 0;
            return new NativeTransactionRecoveryResult(blocked, changed);
        }
    }

    private async Task<NativeTransactionRecoveryRecordResult> RecoverNativeTransactionRecordAsync(
        HostManagerTransactionJournalAdmission admission,
        NativeTransactionJournalRecord record,
        CancellationToken cancellationToken)
    {
        if (record.Scope == (uint)NativeTransactionJournalScope.Resource)
        {
            return await RecoverResourceTransactionRecordAsync(
                admission,
                record,
                cancellationToken);
        }
        if (record.Phase == (uint)NativeTransactionJournalPhase.AuthoritativeResyncPending)
        {
            await RemoveRecoveredOwnershipAsync(record, cancellationToken);
            await CompleteAuthoritativeResyncAsync(admission, record, cancellationToken);
            return new NativeTransactionRecoveryRecordResult(true, false);
        }

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
            return new NativeTransactionRecoveryRecordResult(true, true);
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
            return new NativeTransactionRecoveryRecordResult(true, false);
        }

        return new NativeTransactionRecoveryRecordResult(
            true,
            outcome == NativeTransactionJournalRecoveryOutcome.InvalidProof);
    }

    private async Task<NativeTransactionRecoveryRecordResult>
        RecoverResourceTransactionRecordAsync(
            HostManagerTransactionJournalAdmission admission,
            NativeTransactionJournalRecord record,
            CancellationToken cancellationToken)
    {
        NativeTransactionJournalPayloadReference payloadReference;
        NativeTransactionJournalPayloadProvenance payloadProvenance;
        HostManagerResourceTransactionPayload payload;
        HostManagerRetiredSelfResourceJournalDecision decision;
        try
        {
            var snapshot = await admission.ReadSnapshotAsync(
                cancellationToken);
            payloadReference =
                HostManagerTransactionJournalProjection.CreatePayloadReference(
                    in record);
            payloadProvenance =
                HostManagerTransactionJournalProjection
                    .CreatePayloadProvenance(in record);
            payload = HostManagerResourceTransactionProjection.DecodePayload(
                await admission.ReadPayloadAsync(
                    payloadReference,
                    payloadProvenance,
                    cancellationToken));
            HostManagerResourceTransactionProjection.RequireRecordMatches(
                snapshot.Header,
                record,
                payload);
            HostManagerRetiredSelfResourceJournalRecovery.RequireExactPayload(
                payload);
            decision = HostManagerRetiredSelfResourceJournalRecovery.Classify(
                record);
        }
        catch (Exception exception) when (
            exception is IOException
                or InvalidDataException
                or UnauthorizedAccessException)
        {
            await ApplyRecoveryOutcomeAsync(
                admission,
                record,
                NativeTransactionJournalRecoveryOutcome.InvalidProof,
                0,
                unchecked((uint)exception.HResult),
                cancellationToken);
            return new NativeTransactionRecoveryRecordResult(true, true);
        }

        if (decision is
            HostManagerRetiredSelfResourceJournalDecision
                .AcknowledgeSealedFeedback
            or HostManagerRetiredSelfResourceJournalDecision
                .AcknowledgeStateUncertain)
        {
            var snapshot = await admission.ReadSnapshotAsync(
                cancellationToken);
            var ack = HostManagerTransactionJournalProjection.CreateAck(
                in record,
                snapshot.Header.JournalRevision,
                NativeTransactionJournalAckResult.Accepted,
                Math.Max(
                    CurrentUtcMilliseconds(),
                    record.UpdatedAtUtcMilliseconds),
                retryNotBeforeUtcMilliseconds: 0,
                stableSystemStatus: 0,
                stableSystemError: record.FeedbackSystemError);
            RequireJournalStatus(
                await admission.AcknowledgeAsync(ack, cancellationToken),
                "resource-recovery-acknowledge-feedback");
            var read = await admission.GetAsync(
                record.Identity,
                cancellationToken);
            if (read.Status == NativeTransactionJournalStatus.NoData)
            {
                await DeleteRecoveredResourcePayloadExactAsync(
                    admission,
                    payloadReference,
                    payloadProvenance,
                    cancellationToken);
                return new NativeTransactionRecoveryRecordResult(true, false);
            }
            if (read.Status != NativeTransactionJournalStatus.Ok)
            {
                throw new InvalidDataException(
                    $"Resource recovery feedback read failed with {read.Status}.");
            }
            if (read.Record.Phase ==
                (uint)NativeTransactionJournalPhase.AuthoritativeResyncPending)
            {
                await CompleteResourceAuthoritativeResyncAsync(
                    admission,
                    read.Record,
                    payload.Selection.Authority.SourceSnapshotGeneration,
                    cancellationToken);
                await DeleteRecoveredResourcePayloadExactAsync(
                    admission,
                    payloadReference,
                    payloadProvenance,
                    cancellationToken);
                return new NativeTransactionRecoveryRecordResult(true, false);
            }
            if (read.Record.Phase ==
                    (uint)NativeTransactionJournalPhase.ReconciliationPending
                && decision == HostManagerRetiredSelfResourceJournalDecision
                    .AcknowledgeStateUncertain)
            {
                return new NativeTransactionRecoveryRecordResult(true, true);
            }
            throw new InvalidDataException(
                "Retired Host-self feedback did not reach a terminal or explicitly uncertain journal phase.");
        }
        if (decision == HostManagerRetiredSelfResourceJournalDecision
                .SettleKnownNoEffect)
        {
            await ApplyRecoveryOutcomeAsync(
                admission,
                record,
                NativeTransactionJournalRecoveryOutcome.AlreadyRestored,
                record.StableSystemStatus,
                record.StableSystemError,
                cancellationToken);
            var transitioned = await RequireJournalRecordAsync(
                admission,
                record.Identity,
                cancellationToken);
            await CompleteResourceAuthoritativeResyncAsync(
                admission,
                transitioned,
                payload.Selection.Authority.SourceSnapshotGeneration,
                cancellationToken);
            await DeleteRecoveredResourcePayloadExactAsync(
                admission,
                payloadReference,
                payloadProvenance,
                cancellationToken);
            return new NativeTransactionRecoveryRecordResult(true, false);
        }
        if (decision == HostManagerRetiredSelfResourceJournalDecision
                .CompleteAuthoritativeResync)
        {
            await CompleteResourceAuthoritativeResyncAsync(
                admission,
                record,
                payload.Selection.Authority.SourceSnapshotGeneration,
                cancellationToken);
            await DeleteRecoveredResourcePayloadExactAsync(
                admission,
                payloadReference,
                payloadProvenance,
                cancellationToken);
            return new NativeTransactionRecoveryRecordResult(true, false);
        }
        if (decision == HostManagerRetiredSelfResourceJournalDecision
                .MarkEffectInvocationUncertain)
        {
            await ApplyRecoveryOutcomeAsync(
                admission,
                record,
                NativeTransactionJournalRecoveryOutcome
                    .EffectInvocationUncertain,
                record.StableSystemStatus,
                record.StableSystemError,
                cancellationToken);
            return new NativeTransactionRecoveryRecordResult(true, true);
        }
        if (decision == HostManagerRetiredSelfResourceJournalDecision
                .PreserveBlocked)
        {
            return new NativeTransactionRecoveryRecordResult(false, true);
        }
        throw new InvalidDataException(
            "The retired Host-self journal decision was not handled.");
    }

    private static async Task DeleteRecoveredResourcePayloadExactAsync(
        HostManagerTransactionJournalAdmission admission,
        NativeTransactionJournalPayloadReference reference,
        NativeTransactionJournalPayloadProvenance provenance,
        CancellationToken cancellationToken)
    {
        if (!await admission.DeletePayloadAsync(
                reference,
                provenance,
                cancellationToken))
        {
            throw new IOException(
                "The exact durable resource recovery payload was not deleted.");
        }
    }

    private async Task CompleteResourceAuthoritativeResyncAsync(
        HostManagerTransactionJournalAdmission admission,
        NativeTransactionJournalRecord record,
        ulong authoritativeFactsGeneration,
        CancellationToken cancellationToken)
    {
        if (authoritativeFactsGeneration == 0)
        {
            throw new InvalidDataException(
                "Resource recovery has no authoritative source generation.");
        }
        var current = await RequireJournalRecordAsync(
            admission,
            record.Identity,
            cancellationToken);
        if (current.Phase !=
            (uint)NativeTransactionJournalPhase.AuthoritativeResyncPending)
        {
            throw new InvalidDataException(
                "Resource recovery did not reach authoritative resync.");
        }
        var journal = await admission.ReadSnapshotAsync(
            cancellationToken);
        var evidence = HostManagerTransactionJournalProjection
            .CreateRecoveryEvidence(
                in current,
                journal.Header.JournalRevision,
                NativeTransactionJournalRecoveryOutcome
                    .AuthoritativeResyncCompleted,
                Math.Max(
                    CurrentUtcMilliseconds(),
                    current.UpdatedAtUtcMilliseconds),
                retryNotBeforeUtcMilliseconds: 0,
                stableSystemStatus: 0,
                stableSystemError: current.StableSystemError,
                authoritativeFactsGeneration:
                    authoritativeFactsGeneration);
        RequireJournalStatus(
            await admission.ApplyRecoveryEvidenceAsync(
                evidence,
                cancellationToken),
            "resource-authoritative-resync-completed");
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
        if ((NativeTransactionJournalScope)record.Scope != NativeTransactionJournalScope.Software)
        {
            return TransactionEffectResult.Rejected();
        }

        var maximumEnvelopeBytes = ToExplicitPayloadLimit(payloadByteBudget);
        var restored = await adapterSchedulingTransaction.RestoreAsync(
            new HostManagerAdapterSchedulingRestoreCommand(
                payload,
                maximumEnvelopeBytes,
                maximumEnvelopeBytes,
                deadline),
            cancellationToken);
        return restored.Status switch
        {
            HostManagerAdapterSchedulingRestoreStatus.Restored or
                HostManagerAdapterSchedulingRestoreStatus.AlreadyRestored =>
                TransactionEffectResult.Succeeded(),
            HostManagerAdapterSchedulingRestoreStatus.OwnershipLost or
                HostManagerAdapterSchedulingRestoreStatus.Conflict =>
                TransactionEffectResult.OwnershipLost(),
            HostManagerAdapterSchedulingRestoreStatus.InvalidPayload =>
                TransactionEffectResult.Rejected(),
            _ => TransactionEffectResult.StateUncertain()
        };
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
        var payload = HostManagerAppliedOwnershipProjection.CreatePayloadReference(
            in ownershipRecord);
        if (payload.Slot != record.PayloadSlot ||
            payload.Generation != record.PayloadGeneration ||
            payload.Length != record.PayloadLength ||
            payload.DigestLow != record.PayloadDigestLow ||
            payload.DigestHigh != record.PayloadDigestHigh)
        {
            throw new InvalidDataException(
                "The recovery journal and applied ownership payload identities differ.");
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
            NativeTransactionJournalScope.Software => new NativeAppliedOwnershipPrimaryIdentity
            {
                Scope = (uint)NativeAppliedOwnershipScope.Adapter,
                SoftwareId = record.Identity.SoftwareId
            },
            _ => throw new InvalidDataException(
                $"Journal scope {record.Scope} is not an applied ownership scope.")
        };

    private readonly record struct NativeTransactionRecoveryResult(
        bool IsBlocked,
        bool Changed);

    private readonly record struct NativeTransactionRecoveryRecordResult(
        bool Changed,
        bool Blocked);
}
