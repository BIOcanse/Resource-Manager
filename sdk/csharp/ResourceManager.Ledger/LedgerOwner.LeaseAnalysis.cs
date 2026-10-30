using ResourceManager.Ledger.Identity;
using ResourceManager.Ledger.Interop;

namespace ResourceManager.Ledger;

public sealed unsafe partial class LedgerOwner<TUid>
    where TUid : unmanaged, ILedgerUid<TUid>
{
    public bool LeaseAnalysisAvailable => _api.GetLeaseAnalysis != null;

    /// <summary>Queries diagnostics without acquiring a resource lease or changing activity.</summary>
    public LedgerLeaseAnalysisSnapshot<TUid> GetLeaseAnalysis()
    {
        using CallScope call = EnterCall();
        if (_api.GetLeaseAnalysis == null)
            throw new NotSupportedException("This Native build has lease analysis compiled out.");
        NativeLeaseAnalysisSummary summary = default;
        LedgerStatus status = (LedgerStatus)_api.GetLeaseAnalysis(_instance, null, 0, &summary);
        if (status != LedgerStatus.Ok) throw new LedgerException(status, "query lease analysis capacity");
        if (summary.RecordCapacity is 0 or > 65536)
            throw new InvalidOperationException("Native lease-analysis capacity is invalid.");
        var native = new NativeLeaseAnalysisRecord[summary.RecordCapacity];
        fixed (NativeLeaseAnalysisRecord* output = native)
        {
            status = (LedgerStatus)_api.GetLeaseAnalysis(_instance, output, (ulong)native.Length, &summary);
        }
        if (status != LedgerStatus.Ok) throw new LedgerException(status, "query lease analysis");
        if (summary.WrittenCount > native.Length || summary.RecordCapacity != native.Length)
            throw new InvalidOperationException("Native lease-analysis snapshot extent is invalid.");
        var records = new LedgerLeaseAnalysisRecord<TUid>[summary.WrittenCount];
        for (int i = 0; i < records.Length; i++)
        {
            NativeLeaseAnalysisRecord value = native[i];
            if (value.State is < 1 or > 3 || (value.SupportFlags & ~3u) != 0 || (value.ObservationFlags & ~15u) != 0)
                throw new InvalidOperationException("Native lease-analysis record is invalid.");
            TUid uid = TUid.FromBytes(new ReadOnlySpan<byte>(value.Uid, TUid.ByteLength));
            records[i] = new(uid, value.Slot, (LedgerLeaseAnalysisState)value.State,
                value.AllocationNumber, value.UseGeneration, value.StartedEpoch, value.EndedEpoch,
                value.StartedMilliseconds, value.EndedMilliseconds, value.WriteWindowCount, value.DirtyPageSamples,
                value.LastObservedWriteMilliseconds, value.MaximumNoWriteGapMilliseconds, (LedgerLeaseObservationSupport)value.SupportFlags, (LedgerLeaseObservationFlags)value.ObservationFlags);
        }
        return new(summary.RecordCapacity, summary.ActiveCount, summary.CompletedCount,
            summary.DroppedRecords, summary.UnavailableWriteSamples, summary.SnapshotMilliseconds,
            Array.AsReadOnly(records));
    }

}
