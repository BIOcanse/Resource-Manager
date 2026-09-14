using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.Optimization.Transactions;

namespace ResourceManager.App.Infrastructure.Optimization;

public sealed partial class HostManagerSmartCoordinator
{
    private async Task ReconcileNativePayloadsIfRequiredAsync(
        HostManagerTransactionJournalAdmission admission,
        NativeTransactionJournalSnapshot journalSnapshot,
        CancellationToken cancellationToken)
    {
        if (!admission.PayloadReconciliationRequired)
        {
            return;
        }

        var ownershipSnapshot =
            await nativeActionTransactions.AppliedOwnership.ReadSnapshotAsync(cancellationToken);
        var live = new List<NativeTransactionJournalPayloadLiveReference>(
            checked(journalSnapshot.Records.Count + ownershipSnapshot.Records.Count));
        foreach (var record in journalSnapshot.Records)
        {
            live.Add(new NativeTransactionJournalPayloadLiveReference(
                HostManagerTransactionJournalProjection.CreatePayloadReference(in record),
                HostManagerTransactionJournalProjection.CreatePayloadProvenance(in record)));
        }
        foreach (var record in ownershipSnapshot.Records)
        {
            var reference =
                HostManagerAppliedOwnershipProjection.CreatePayloadReference(in record);
            var binding =
                HostManagerAppliedOwnershipProjection.CreatePayloadBinding(in record);
            live.Add(new NativeTransactionJournalPayloadLiveReference(
                reference,
                NativeTransactionJournalPayloadProvenance.Create(binding)));
        }

        _ = await admission.ReconcilePayloadsAsync(
            live.ToArray(),
            cancellationToken);
    }
}
