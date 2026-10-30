using ResourceManager.Ledger.Identity;

namespace ResourceManager.Ledger;

[Flags]
public enum LedgerLeaseObservationSupport : uint { None = 0, WriteWatch = 1, ReadsUnobservable = 2 }
[Flags]
public enum LedgerLeaseObservationFlags : uint { None = 0, SharedAttribution = 1, ExceedsScratch = 2, QueryFailed = 4, PlatformUnavailable = 8 }

public enum LedgerLeaseAnalysisState : uint
{
    Active = 1,
    Ended = 2,
    ForceReleased = 3,
}

/// <summary>One retained diagnostic sample. Write facts are automatic page observations; reads are unobservable.</summary>
public sealed record LedgerLeaseAnalysisRecord<TUid>(
    TUid Uid, uint Slot, LedgerLeaseAnalysisState State, ulong AllocationNumber, ulong UseGeneration,
    ulong StartedEpoch, ulong EndedEpoch, ulong StartedMilliseconds, ulong EndedMilliseconds,
    ulong WriteWindowCount, ulong DirtyPageSamples, ulong LastObservedWriteMilliseconds, ulong MaximumNoWriteGapMilliseconds,
    LedgerLeaseObservationSupport Support, LedgerLeaseObservationFlags ObservationFlags)
    where TUid : unmanaged, ILedgerUid<TUid>
{
    public ulong HeldMilliseconds(ulong snapshotMilliseconds) =>
        (State == LedgerLeaseAnalysisState.Active ? snapshotMilliseconds : EndedMilliseconds) >= StartedMilliseconds
            ? (State == LedgerLeaseAnalysisState.Active ? snapshotMilliseconds : EndedMilliseconds) - StartedMilliseconds
            : 0;
}

/// <summary>A single Native snapshot; active/completed counts describe the retained bounded samples.</summary>
public sealed record LedgerLeaseAnalysisSnapshot<TUid>(
    uint RecordCapacity, ulong ActiveCount, ulong CompletedCount, ulong DroppedRecords,
    ulong UnavailableWriteSamples, ulong SnapshotMilliseconds,
    IReadOnlyList<LedgerLeaseAnalysisRecord<TUid>> Records)
    where TUid : unmanaged, ILedgerUid<TUid>;
