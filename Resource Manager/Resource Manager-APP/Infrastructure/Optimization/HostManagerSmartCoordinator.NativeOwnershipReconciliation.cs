using ResourceManager.App.Application.Optimization;
using ResourceManager.App.Domain.ResourceBreakdown;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.Optimization.Transactions;

namespace ResourceManager.App.Infrastructure.Optimization;

public sealed partial class HostManagerSmartCoordinator
{
    private async Task<NativeAppliedOwnershipSnapshot> ReconcileExitedProcessOwnershipAsync(
        HostManagerCycleEffectPermit permit,
        SchedulingProcessFactSnapshot processFacts,
        HostManagerTransactionJournalAdmission admission,
        CancellationToken cancellationToken)
    {
        permit.Require(HostManagerCycleEffectKind.ExitedOwnershipReconciliation);
        _ = memoryCleanupAttemptJournal.ReconcileAndCaptureBlocked(
            processPolicyWriter.ReadProcessInstanceForRecovery);
        if (!processEffectValidationScopeAuthorityOwner
                .ReconcileDeclaredMemoryCleanupHandoff(
                    memoryCleanupAttemptJournal.CapturePendingBatches()))
        {
            throw new InvalidDataException(
                "The validation scope could not reconcile its declared memory-cleanup handoff.");
        }
        var ownershipSnapshot =
            await nativeActionTransactions.AppliedOwnership.ReadSnapshotAsync(cancellationToken);
        var candidates = SelectMissingProcessOwnership(processFacts, ownershipSnapshot);
        var changed = false;
        foreach (var candidate in candidates)
        {
            var processRead =
                processPolicyWriter.ReadProcessInstanceForRecovery(
                    checked((int)candidate.Primary.ProcessId));
            if (!ProcessInstanceRecovery.IsConfirmedExited(
                    processRead,
                    candidate.Primary.ProcessId,
                    candidate.Primary.ProcessStartKey))
            {
                continue;
            }
            if (!permit.TryReserveRecovery(1, out _))
            {
                break;
            }

            var currentSnapshot =
                await nativeActionTransactions.AppliedOwnership.ReadSnapshotAsync(
                    cancellationToken);
            var remove = HostManagerAppliedOwnershipProjection.CreateRecoveryRemove(
                in candidate,
                currentSnapshot.Header.LedgerRevision,
                CurrentUtcMilliseconds(candidate.UpdatedAtUtcMilliseconds));
            RequireOwnershipStatus(
                await nativeActionTransactions.AppliedOwnership.RemoveAsync(
                    remove,
                    cancellationToken),
                "reconcile-exited-process");

            changed = true;
            authoritativeAppliedFactsRequired = true;
            var payloadReference =
                HostManagerAppliedOwnershipProjection.CreatePayloadReference(in candidate);
            var payloadBinding =
                HostManagerAppliedOwnershipProjection.CreatePayloadBinding(in candidate);
            _ = await admission.DeletePayloadAsync(
                payloadReference,
                payloadBinding,
                cancellationToken);
        }

        if (admission.PayloadReconciliationRequired)
        {
            var journalSnapshot = await admission.ReadSnapshotAsync(cancellationToken);
            await ReconcileNativePayloadsIfRequiredAsync(
                admission,
                journalSnapshot,
                cancellationToken);
        }
        return changed
            ? await nativeActionTransactions.AppliedOwnership.ReadSnapshotAsync(
                cancellationToken)
            : ownershipSnapshot;
    }

    internal static IReadOnlyList<NativeAppliedOwnershipRecord> SelectMissingProcessOwnership(
        SchedulingProcessFactSnapshot processFacts,
        NativeAppliedOwnershipSnapshot ownershipSnapshot)
    {
        ArgumentNullException.ThrowIfNull(processFacts);
        ArgumentNullException.ThrowIfNull(ownershipSnapshot);
        if (!processFacts.IsInventoryCurrentComplete())
        {
            return [];
        }

        var ownership = CreateAppliedOwnershipFactIndex(ownershipSnapshot);
        if (ownership.ProcessRecords.Count == 0
            && ownership.MemoryProcessRecords.Count == 0)
        {
            return [];
        }

        var live = new HashSet<NativeAppliedOwnershipProcessIdentity>();
        foreach (var process in processFacts.Processes)
        {
            var incarnation = new NativeAppliedOwnershipProcessIncarnation(
                checked((uint)process.ProcessId),
                process.ProcessStartKey);
            var softwareKey = ownership.ProcessAnchors.TryGetValue(
                incarnation,
                out var anchor)
                ? anchor.SoftwareKey
                : NativeStableIdentity.CreateCaseInsensitiveKey(process.SoftwareId);
            live.Add(new NativeAppliedOwnershipProcessIdentity(
                NativeStableIdentity.CreateCaseInsensitiveKey(
                    HostManagerTargetIdentity.CreateProcessTargetId(
                        process.ProcessId,
                        process.ProcessStartKey)),
                softwareKey,
                checked((uint)process.ProcessId),
                process.ProcessStartKey));
            live.Add(new NativeAppliedOwnershipProcessIdentity(
                NativeStableIdentity.CreateCaseInsensitiveKey(
                    HostManagerTargetIdentity.CreateProcessMemoryPolicyTargetId(
                        process.ProcessId,
                        process.ProcessStartKey)),
                softwareKey,
                checked((uint)process.ProcessId),
                process.ProcessStartKey));
        }

        return ownership.ProcessRecords
            .Concat(ownership.MemoryProcessRecords)
            .Where(pair => !live.Contains(pair.Key))
            .OrderBy(static pair => pair.Key.ProcessId)
            .ThenBy(static pair => pair.Key.ProcessStartKey)
            .ThenBy(static pair => pair.Key.TargetKey)
            .Select(static pair => pair.Value)
            .ToArray();
    }

}
