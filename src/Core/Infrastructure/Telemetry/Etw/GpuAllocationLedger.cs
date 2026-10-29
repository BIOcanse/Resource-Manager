using System.Runtime.InteropServices;

namespace ResourceManager.App.Infrastructure.Telemetry.Etw;

public sealed record GpuAllocationAmount(double PrivateBytes, double SharedBytes)
{
    public double TotalBytes => PrivateBytes + SharedBytes;
}

public sealed record GpuAllocationReading(
    IReadOnlyDictionary<ulong, IReadOnlyDictionary<int, GpuAllocationAmount>> Adapters)
{
    public IReadOnlyDictionary<ulong, double> KernelMetadataBytes { get; init; } = new Dictionary<ulong, double>();
    public ulong Generation { get; init; }
    public long ObservedAtUtcTicks { get; init; }
}

/// <summary>The write surface the ETW decoder drives.</summary>
internal interface IGpuAllocationLedgerUpdates
{
    void SetSegment(ulong adapter, uint segment, uint flags);
    void SetAdapter(ulong adapter, ulong luid);
    void SetAllocation(ulong adapter, ulong global, ulong bytes, bool kernelMetadata = false);
    void SetCommittedGlobal(ulong global, uint segment, ulong placement = 0);
    void SetCommittedReference(ulong reference, int pid, uint segment, ulong bytes, ulong placement = 0);
    void AddResidentRange(ulong adapter, ulong global, uint segment, ulong offset, ulong bytes, ulong placement = 0);
    void DiscardResident(ulong adapter, ulong global, uint segment, ulong? placement = null);
    void RecordTransferredSource(ulong adapter, ulong global, uint segment, ulong placement, ulong bytes);
    void CompleteEviction(ulong global);
    void Open(ulong adapter, ulong global, ulong reference, int pid, long observedFileTime);
    void Close(ulong reference);
    void Delete(ulong adapter, ulong global);
}

// All mutation and reads are serialized by the collector. Handles identify a live allocation, not a permanent object.
// Each allocation is one struct row. Its references, resident ranges and transferred sources are struct rows in
// shared tables, linked to it by index, so an allocation carries no collection of its own. Per adapter and per
// process totals are kept as events arrive, so a read walks processes, not allocations. A segment that changes
// its dedicated classification while ranges sit in it marks the totals stale; the next read recomputes them once.
// A call that throws leaves the ledger unusable for reading: the collector discards it, as it did before.
public sealed class GpuAllocationLedger(int maximumAllocations = 262144, int maximumReferences = 1048576)
    : IGpuAllocationLedgerUpdates
{
    // Per allocation: physical placements, ranges per placement and transferred sources. Per pending global: placements.
    private const int PlacementLimit = 4096;
    private const int None = -1;

    private interface IRow { int Next { get; set; } }

    private struct AllocationRow : IRow
    {
        public ulong Adapter;
        public ulong Global;
        public ulong Bytes;
        public ulong DedicatedBytes;     // resident bytes in this adapter's dedicated segments
        public int FirstReference;
        public int FirstResident;
        public int FirstTransferred;
        public ushort PlacementCount;
        public ushort TransferredCount;
        public bool HasBytes;
        public bool KernelMetadata;
        public int Next { get; set; }    // free list only
    }

    private struct ReferenceRow : IRow
    {
        public ulong Handle;
        public long ObservedFileTime;
        public int Allocation;
        public int Pid;
        public int Previous;
        public int Next { get; set; }
    }

    private struct ResidentRow : IRow
    {
        public ulong Placement;
        public ulong Start;
        public ulong End;
        public uint Segment;
        public int Next { get; set; }
    }

    private struct TransferredRow : IRow
    {
        public ulong Placement;
        public uint Segment;
        public int Next { get; set; }
    }

    private readonly record struct PendingReference(int Pid, uint Segment, ulong Bytes, ulong Placement);

    private struct AdapterTotals
    {
        public int Allocations;
        public int KernelAllocations;
        public ulong KernelBytes;
    }

    // One process's resident share on one adapter, over every allocation that names it among its owners.
    private sealed class ProcessShare(long observedAt)
    {
        internal int Allocations;
        // Never above the newest reference time of any counted allocation; a read whose process birth is later
        // recomputes this process exactly and tightens it.
        internal long OldestObservedAt = observedAt;
        internal ulong SoleBytes;
        private readonly List<(int Owners, int Allocations, ulong Bytes)> shared = [];

        internal void Add(int owners, ulong bytes, long referenceObservedAt)
        {
            Allocations++;
            OldestObservedAt = Math.Min(OldestObservedAt, referenceObservedAt);
            if (owners == 1) { SoleBytes += bytes; return; }
            for (var i = 0; i < shared.Count; i++)
            {
                if (shared[i].Owners != owners) continue;
                shared[i] = (owners, shared[i].Allocations + 1, shared[i].Bytes + bytes);
                return;
            }
            shared.Add((owners, 1, bytes));
        }

        internal void Remove(int owners, ulong bytes)
        {
            Allocations--;
            if (owners == 1) { SoleBytes -= bytes; return; }
            for (var i = 0; i < shared.Count; i++)
            {
                if (shared[i].Owners != owners) continue;
                if (shared[i].Allocations == 1) shared.RemoveAt(i);
                else shared[i] = (owners, shared[i].Allocations - 1, shared[i].Bytes - bytes);
                return;
            }
            throw new InvalidOperationException("GPU process share has no allocation with this owner count.");
        }

        internal double SharedBytes()
        {
            double total = 0;
            foreach (var (owners, _, bytes) in shared) total += (double)bytes / owners;
            return total;
        }
    }

    private sealed class RowTable<T> where T : struct, IRow
    {
        internal T[] Rows = new T[16];
        private int used;
        private int free = None;

        internal int Add(T row)
        {
            int index;
            if (free != None)
            {
                index = free;
                free = Rows[index].Next;
            }
            else
            {
                if (used == Rows.Length) Array.Resize(ref Rows, Rows.Length * 2);
                index = used++;
            }
            Rows[index] = row;
            return index;
        }

        internal void Remove(int index)
        {
            Rows[index] = default;
            Rows[index].Next = free;
            free = index;
        }
    }

    private readonly RowTable<AllocationRow> allocations = new();
    private readonly RowTable<ReferenceRow> references = new();
    private readonly RowTable<ResidentRow> resident = new();
    private readonly RowTable<TransferredRow> transferred = new();
    private readonly Dictionary<(ulong Adapter, ulong Global), int> allocationIndex = [];
    private readonly Dictionary<ulong, int> referenceIndex = [];
    // The most recently created allocation for a global handle; Delete of that handle on any adapter clears it.
    private readonly Dictionary<ulong, int> globalIndex = [];
    private readonly Dictionary<ulong, ulong> adapterLuids = [];
    private readonly Dictionary<(ulong Adapter, uint Segment), bool> dedicatedSegments = [];
    private readonly Dictionary<(ulong Adapter, uint Segment), int> segmentRanges = [];
    private readonly Dictionary<ulong, PendingReference> pendingReferences = [];
    private readonly Dictionary<ulong, HashSet<(uint Segment, ulong Placement)>> pendingGlobals = [];
    private int pendingApplicationReferences;
    private int allocationsWithoutSize;
    private readonly Dictionary<ulong, AdapterTotals> adapterTotals = [];
    private readonly Dictionary<(ulong Adapter, int Pid), ProcessShare> processShares = [];
    private bool totalsStale;
    private readonly Dictionary<int, long> ownersScratch = [];
    private readonly List<int> mergedScratch = [];
    private readonly HashSet<ulong> placementsScratch = [];
    private readonly Dictionary<(ulong Luid, int Pid), (double PrivateBytes, double SharedBytes)> amountsScratch = [];
    private readonly Dictionary<(ulong Adapter, int Pid), long> exactScratch = [];

    public void SetSegment(ulong adapter, uint segment, uint flags)
    {
        // Aperture, AGP and the implicit system-memory segment are not dedicated VRAM.
        var dedicated = segment != 0 && (flags & 0x1003u) == 0;
        ref var classification = ref CollectionsMarshal.GetValueRefOrAddDefault(dedicatedSegments, (adapter, segment), out var known);
        if (!known || classification != dedicated)
        {
            classification = dedicated;
            if (segmentRanges.GetValueOrDefault((adapter, segment)) > 0) totalsStale = true;
        }
        if ((flags & 0x20u) != 0)
            throw new InvalidDataException("Pitch-aligned GPU segments require an explicit resident charge.");
    }

    public void SetAdapter(ulong adapter, ulong luid) => adapterLuids[adapter] = luid;

    public void SetAllocation(ulong adapter, ulong global, ulong bytes, bool kernelMetadata = false)
    {
        var index = GetAllocation(adapter, global);
        ref var row = ref allocations.Rows[index];
        if (!row.HasBytes) allocationsWithoutSize--;
        row.HasBytes = true;
        row.Bytes = bytes;
        if (row.KernelMetadata != kernelMetadata)
        {
            Account(index, false);
            allocations.Rows[index].KernelMetadata = kernelMetadata;
            Account(index, true);
        }
        if (pendingGlobals.Remove(global, out var placements))
            foreach (var placement in placements) SetCommittedGlobal(global, placement.Segment, placement.Placement);
    }

    public void SetCommittedGlobal(ulong global, uint segment, ulong placement = 0)
    {
        if (!globalIndex.TryGetValue(global, out var index) || !allocations.Rows[index].HasBytes)
        {
            if (pendingGlobals.Count >= maximumAllocations) throw new InvalidDataException("GPU pending allocation limit exceeded.");
            if (!pendingGlobals.TryGetValue(global, out var placements)) pendingGlobals.Add(global, placements = []);
            if (placements.Count >= PlacementLimit) throw new InvalidDataException("GPU pending placement limit exceeded.");
            placements.Add((segment, placement));
            return;
        }
        var row = allocations.Rows[index];
        AddResidentRange(row.Adapter, global, segment, 0, row.Bytes, placement);
    }

    public void SetCommittedReference(ulong reference, int pid, uint segment, ulong bytes, ulong placement = 0)
    {
        if (!referenceIndex.TryGetValue(reference, out var index))
        {
            if (pendingReferences.Count >= maximumReferences) throw new InvalidDataException("GPU pending reference limit exceeded.");
            RemovePendingReference(reference);
            pendingReferences.Add(reference, new(pid, segment, bytes, placement));
            if (pid != 4) pendingApplicationReferences++;
            return;
        }
        var row = allocations.Rows[references.Rows[index].Allocation];
        AddResidentRange(row.Adapter, row.Global, segment, 0, bytes, placement);
    }

    public void AddResidentRange(ulong adapter, ulong global, uint segment, ulong offset, ulong bytes, ulong placement = 0)
    {
        if (segment == 0 || bytes == 0) return;
        var index = GetAllocation(adapter, global);
        var end = checked(offset + bytes);
        var start = offset;
        // Ranges at one physical placement never overlap or touch, so one pass that widens the new range
        // finds every range it absorbs, in any order.
        var placementKnown = false;
        var kept = 0;
        mergedScratch.Clear();
        for (var current = allocations.Rows[index].FirstResident; current != None; current = resident.Rows[current].Next)
        {
            ref var range = ref resident.Rows[current];
            if (range.Segment != segment || range.Placement != placement) continue;
            placementKnown = true;
            if (range.End < start || range.Start > end) { kept++; continue; }
            start = Math.Min(start, range.Start);
            end = Math.Max(end, range.End);
            mergedScratch.Add(current);
        }
        if (!placementKnown && allocations.Rows[index].PlacementCount >= PlacementLimit)
            throw new InvalidDataException("GPU physical placement limit exceeded.");
        if (kept >= PlacementLimit) throw new InvalidDataException("GPU physical range limit exceeded.");
        Account(index, false);
        ref var row = ref allocations.Rows[index];
        var dedicated = CountsDedicated(adapter, segment);
        foreach (var merged in mergedScratch)
        {
            if (dedicated) row.DedicatedBytes -= resident.Rows[merged].End - resident.Rows[merged].Start;
            UnlinkResident(ref row, merged);
        }
        row.FirstResident = resident.Add(new ResidentRow
        {
            Placement = placement, Start = start, End = end, Segment = segment, Next = row.FirstResident
        });
        CollectionsMarshal.GetValueRefOrAddDefault(segmentRanges, (adapter, segment), out _) += 1 - mergedScratch.Count;
        if (!placementKnown) row.PlacementCount++;
        if (dedicated) row.DedicatedBytes += end - start;
        mergedScratch.Clear();
        Account(index, true);
    }

    public void DiscardResident(ulong adapter, ulong global, uint segment, ulong? placement = null)
    {
        if (pendingGlobals.TryGetValue(global, out var pending))
        {
            pending.RemoveWhere(item => item.Segment == segment && (!placement.HasValue || item.Placement == placement));
            if (pending.Count == 0) pendingGlobals.Remove(global);
        }
        if (!allocationIndex.TryGetValue((adapter, global), out var index)) return;
        Account(index, false);
        ref var row = ref allocations.Rows[index];
        var dedicated = CountsDedicated(adapter, segment);
        var removed = 0;
        placementsScratch.Clear();
        var previous = None;
        for (var current = row.FirstResident; current != None;)
        {
            var range = resident.Rows[current];
            if (range.Segment != segment || placement.HasValue && range.Placement != placement.Value)
            {
                previous = current;
                current = range.Next;
                continue;
            }
            if (previous == None) row.FirstResident = range.Next;
            else resident.Rows[previous].Next = range.Next;
            if (dedicated) row.DedicatedBytes -= range.End - range.Start;
            placementsScratch.Add(range.Placement);
            resident.Remove(current);
            removed++;
            current = range.Next;
        }
        row.PlacementCount -= checked((ushort)placementsScratch.Count);
        placementsScratch.Clear();
        if (removed != 0) segmentRanges[(adapter, segment)] -= removed;
        previous = None;
        for (var current = row.FirstTransferred; current != None;)
        {
            var source = transferred.Rows[current];
            if (source.Segment != segment || placement.HasValue && source.Placement != placement.Value)
            {
                previous = current;
                current = source.Next;
                continue;
            }
            if (previous == None) row.FirstTransferred = source.Next;
            else transferred.Rows[previous].Next = source.Next;
            transferred.Remove(current);
            row.TransferredCount--;
            current = source.Next;
        }
        Account(index, true);
    }

    public void RecordTransferredSource(ulong adapter, ulong global, uint segment, ulong placement, ulong bytes)
    {
        if (segment == 0) return;
        var index = GetAllocation(adapter, global);
        ref var row = ref allocations.Rows[index];
        if (!row.HasBytes || row.Bytes != bytes)
            throw new NotSupportedException("Partial GPU transfers require range-specific eviction evidence.");
        for (var current = row.FirstTransferred; current != None; current = transferred.Rows[current].Next)
            if (transferred.Rows[current].Segment == segment && transferred.Rows[current].Placement == placement) return;
        if (row.TransferredCount >= PlacementLimit)
            throw new InvalidDataException("GPU transferred placement limit exceeded.");
        row.FirstTransferred = transferred.Add(new TransferredRow { Placement = placement, Segment = segment, Next = row.FirstTransferred });
        row.TransferredCount++;
    }

    public void CompleteEviction(ulong global)
    {
        if (!globalIndex.TryGetValue(global, out var index)) return;
        // Eviction alone can leave backing resident. A preceding completed whole
        // transfer identifies the old backing that the eviction actually retires.
        Account(index, false);
        ref var row = ref allocations.Rows[index];
        for (var source = row.FirstTransferred; source != None;)
        {
            var location = transferred.Rows[source];
            var dedicated = CountsDedicated(row.Adapter, location.Segment);
            var removed = 0;
            var previous = None;
            for (var current = row.FirstResident; current != None;)
            {
                var range = resident.Rows[current];
                if (range.Segment != location.Segment || range.Placement != location.Placement)
                {
                    previous = current;
                    current = range.Next;
                    continue;
                }
                if (previous == None) row.FirstResident = range.Next;
                else resident.Rows[previous].Next = range.Next;
                if (dedicated) row.DedicatedBytes -= range.End - range.Start;
                resident.Remove(current);
                removed++;
                current = range.Next;
            }
            if (removed != 0)
            {
                row.PlacementCount--;
                segmentRanges[(row.Adapter, location.Segment)] -= removed;
            }
            transferred.Remove(source);
            source = location.Next;
        }
        row.FirstTransferred = None;
        row.TransferredCount = 0;
        Account(index, true);
    }

    public void Open(ulong adapter, ulong global, ulong reference, int pid, long observedFileTime)
    {
        if (referenceIndex.TryGetValue(reference, out var prior))
        {
            var owner = allocations.Rows[references.Rows[prior].Allocation];
            if (owner.Adapter != adapter || owner.Global != global) Close(reference);
        }
        if (!referenceIndex.ContainsKey(reference) && referenceIndex.Count >= maximumReferences)
            throw new InvalidDataException("GPU allocation reference limit exceeded.");
        var index = GetAllocation(adapter, global);
        Account(index, false);
        if (referenceIndex.TryGetValue(reference, out var existing))
        {
            ref var row = ref references.Rows[existing];
            row.Pid = pid;
            row.ObservedFileTime = observedFileTime;
        }
        else
        {
            ref var allocation = ref allocations.Rows[index];
            var added = references.Add(new ReferenceRow
            {
                Handle = reference, ObservedFileTime = observedFileTime, Allocation = index, Pid = pid,
                Previous = None, Next = allocation.FirstReference
            });
            if (allocation.FirstReference != None) references.Rows[allocation.FirstReference].Previous = added;
            allocation.FirstReference = added;
            referenceIndex.Add(reference, added);
        }
        Account(index, true);
        if (pendingReferences.Remove(reference, out var pendingReference))
        {
            if (pendingReference.Pid != 4) pendingApplicationReferences--;
            if (pendingReference.Pid != pid) throw new InvalidDataException("GPU committed reference owner changed during rundown.");
            AddResidentRange(adapter, global, pendingReference.Segment, 0, pendingReference.Bytes, pendingReference.Placement);
        }
    }

    public void Close(ulong reference)
    {
        RemovePendingReference(reference);
        if (!referenceIndex.Remove(reference, out var index)) return;
        var allocation = references.Rows[index].Allocation;
        Account(allocation, false);
        UnlinkReference(index);
        Account(allocation, true);
    }

    public void Delete(ulong adapter, ulong global)
    {
        pendingGlobals.Remove(global);
        globalIndex.Remove(global);
        if (!allocationIndex.Remove((adapter, global), out var index)) return;
        Account(index, false);
        var row = allocations.Rows[index];
        for (var current = row.FirstReference; current != None;)
        {
            var next = references.Rows[current].Next;
            referenceIndex.Remove(references.Rows[current].Handle);
            references.Remove(current);
            current = next;
        }
        for (var current = row.FirstResident; current != None;)
        {
            var next = resident.Rows[current].Next;
            segmentRanges[(adapter, resident.Rows[current].Segment)]--;
            resident.Remove(current);
            current = next;
        }
        for (var current = row.FirstTransferred; current != None;)
        {
            var next = transferred.Rows[current].Next;
            transferred.Remove(current);
            current = next;
        }
        if (!row.HasBytes) allocationsWithoutSize--;
        allocations.Remove(index);
    }

    public GpuAllocationReading Read(IReadOnlyDictionary<int, long> currentProcesses)
    {
        if (pendingGlobals.Count != 0 || pendingApplicationReferences != 0)
            throw new InvalidDataException("GPU resident allocation ownership is incomplete.");
        if (totalsStale) RecomputeTotals();
        foreach (var adapter in adapterTotals.Keys)
            if (!adapterLuids.ContainsKey(adapter))
                throw new InvalidDataException("GPU allocation adapter has no LUID mapping.");
        if (allocationsWithoutSize != 0) throw new InvalidDataException("GPU reference has no allocation size.");
        foreach (var (segment, ranges) in segmentRanges)
            if (ranges > 0 && !dedicatedSegments.ContainsKey(segment))
                throw new InvalidDataException("GPU resident segment has no classification.");
        var output = new Dictionary<ulong, IReadOnlyDictionary<int, GpuAllocationAmount>>();
        var kernel = new Dictionary<ulong, double>();
        foreach (var (adapter, totals) in adapterTotals)
        {
            var luid = adapterLuids[adapter];
            if (!output.ContainsKey(luid)) output.Add(luid, new Dictionary<int, GpuAllocationAmount>());
            if (totals.KernelAllocations > 0) kernel[luid] = kernel.GetValueOrDefault(luid) + totals.KernelBytes;
        }
        try
        {
            foreach (var ((adapter, pid), share) in processShares)
            {
                // Hidden/unreadable users remain in the divisor; an old PID cannot be assigned to a new process.
                if (!currentProcesses.TryGetValue(pid, out var birth) || birth <= 0) continue;
                if (birth > share.OldestObservedAt)
                {
                    exactScratch[(adapter, pid)] = long.MaxValue;
                    continue;
                }
                AddAmount((adapterLuids[adapter], pid), share.SoleBytes, share.SharedBytes());
            }
            if (exactScratch.Count != 0) AddExactAmounts(currentProcesses);
            // Readers retain their snapshot across subsequent ETW updates and reads.
            // Only the numeric aggregation workspace is reused, never the result.
            foreach (var (key, amount) in amountsScratch)
                ((Dictionary<int, GpuAllocationAmount>)output[key.Luid]).Add(key.Pid,
                    new GpuAllocationAmount(amount.PrivateBytes, amount.SharedBytes));
            return new GpuAllocationReading(output) { KernelMetadataBytes = kernel };
        }
        finally
        {
            amountsScratch.Clear();
            exactScratch.Clear();
        }
    }

    // A process whose birth is later than the oldest counted reference time may still have allocations left by an
    // earlier process with the same PID. Count those processes allocation by allocation, as one walk.
    private void AddExactAmounts(IReadOnlyDictionary<int, long> currentProcesses)
    {
        foreach (var index in allocationIndex.Values)
        {
            var row = allocations.Rows[index];
            if (row.KernelMetadata) continue;
            var owners = CollectOwners(row.FirstReference);
            if (owners == 0) continue;
            var share = (double)row.DedicatedBytes / owners;
            foreach (var (pid, observedAt) in ownersScratch)
            {
                ref var oldest = ref CollectionsMarshal.GetValueRefOrNullRef(exactScratch, (row.Adapter, pid));
                if (System.Runtime.CompilerServices.Unsafe.IsNullRef(ref oldest)) continue;
                oldest = Math.Min(oldest, observedAt);
                if (currentProcesses[pid] > observedAt) continue;
                AddAmount((adapterLuids[row.Adapter], pid), owners == 1 ? share : 0, owners == 1 ? 0 : share);
            }
            ownersScratch.Clear();
        }
        foreach (var (key, oldest) in exactScratch) processShares[key].OldestObservedAt = oldest;
    }

    private void AddAmount((ulong Luid, int Pid) key, double privateBytes, double sharedBytes)
    {
        ref var amount = ref CollectionsMarshal.GetValueRefOrAddDefault(amountsScratch, key, out _);
        amount = (amount.PrivateBytes + privateBytes, amount.SharedBytes + sharedBytes);
    }

    private void RecomputeTotals()
    {
        adapterTotals.Clear();
        processShares.Clear();
        totalsStale = false;
        foreach (var index in allocationIndex.Values)
        {
            ref var row = ref allocations.Rows[index];
            row.DedicatedBytes = 0;
            for (var current = row.FirstResident; current != None; current = resident.Rows[current].Next)
                if (IsDedicated(row.Adapter, resident.Rows[current].Segment))
                    row.DedicatedBytes += resident.Rows[current].End - resident.Rows[current].Start;
            Account(index, true);
        }
    }

    // Adds (or withdraws) one allocation's part of the adapter and process totals. Every change to an allocation's
    // kernel flag, dedicated bytes or references is bracketed by a withdraw before and an add after it.
    private void Account(int index, bool add)
    {
        if (totalsStale) return;
        var row = allocations.Rows[index];
        ref var adapter = ref CollectionsMarshal.GetValueRefOrAddDefault(adapterTotals, row.Adapter, out _);
        adapter.Allocations += add ? 1 : -1;
        if (row.KernelMetadata)
        {
            adapter.KernelAllocations += add ? 1 : -1;
            adapter.KernelBytes = add ? adapter.KernelBytes + row.DedicatedBytes : adapter.KernelBytes - row.DedicatedBytes;
        }
        if (adapter.Allocations == 0) adapterTotals.Remove(row.Adapter);
        if (row.KernelMetadata) return;
        var owners = CollectOwners(row.FirstReference);
        foreach (var (pid, observedAt) in ownersScratch)
        {
            if (add)
            {
                if (!processShares.TryGetValue((row.Adapter, pid), out var share))
                    processShares.Add((row.Adapter, pid), share = new(observedAt));
                share.Add(owners, row.DedicatedBytes, observedAt);
            }
            else
            {
                var share = processShares[(row.Adapter, pid)];
                share.Remove(owners, row.DedicatedBytes);
                if (share.Allocations == 0) processShares.Remove((row.Adapter, pid));
            }
        }
        ownersScratch.Clear();
    }

    // Distinct owner PIDs of one allocation with each one's newest reference time, in ownersScratch.
    private int CollectOwners(int firstReference)
    {
        ownersScratch.Clear();
        for (var current = firstReference; current != None; current = references.Rows[current].Next)
        {
            var reference = references.Rows[current];
            ref var newest = ref CollectionsMarshal.GetValueRefOrAddDefault(ownersScratch, reference.Pid, out var seen);
            newest = seen ? Math.Max(newest, reference.ObservedFileTime) : reference.ObservedFileTime;
        }
        return ownersScratch.Count;
    }

    private bool IsDedicated(ulong adapter, uint segment)
        => dedicatedSegments.TryGetValue((adapter, segment), out var dedicated) && dedicated;

    // Stale totals recompute every allocation's dedicated bytes, so changes made meanwhile are not counted.
    private bool CountsDedicated(ulong adapter, uint segment) => !totalsStale && IsDedicated(adapter, segment);

    private void UnlinkResident(ref AllocationRow row, int target)
    {
        var previous = None;
        for (var current = row.FirstResident; current != None; previous = current, current = resident.Rows[current].Next)
        {
            if (current != target) continue;
            if (previous == None) row.FirstResident = resident.Rows[current].Next;
            else resident.Rows[previous].Next = resident.Rows[current].Next;
            resident.Remove(current);
            return;
        }
        throw new InvalidOperationException("GPU resident range is not linked to its allocation.");
    }

    private void UnlinkReference(int index)
    {
        var reference = references.Rows[index];
        if (reference.Previous == None) allocations.Rows[reference.Allocation].FirstReference = reference.Next;
        else references.Rows[reference.Previous].Next = reference.Next;
        if (reference.Next != None) references.Rows[reference.Next].Previous = reference.Previous;
        references.Remove(index);
    }

    private void RemovePendingReference(ulong reference)
    {
        if (pendingReferences.Remove(reference, out var pending) && pending.Pid != 4) pendingApplicationReferences--;
    }

    private int GetAllocation(ulong adapter, ulong global)
    {
        if (allocationIndex.TryGetValue((adapter, global), out var index)) return index;
        if (allocationIndex.Count >= maximumAllocations) throw new InvalidDataException("GPU allocation limit exceeded.");
        index = allocations.Add(new AllocationRow
        {
            Adapter = adapter, Global = global, FirstReference = None, FirstResident = None, FirstTransferred = None
        });
        allocationIndex.Add((adapter, global), index);
        globalIndex[global] = index;
        allocationsWithoutSize++;
        Account(index, true);
        return index;
    }
}
