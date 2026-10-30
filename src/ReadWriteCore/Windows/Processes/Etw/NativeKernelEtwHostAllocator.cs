using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace ResourceManager.App.Infrastructure.Telemetry.Etw;

[StructLayout(LayoutKind.Sequential)]
public unsafe struct NativeKernelEtwHostAllocatorCallbacks
{
    public uint Size;
    public uint Version;
    public nuint PageSize;
    public nint Context;
    public delegate* unmanaged[Cdecl]<nint, uint, ulong, uint, nint> Allocate;
    public delegate* unmanaged[Cdecl]<nint, uint, nint, ulong, ulong, uint, nint> Remap;
    public delegate* unmanaged[Cdecl]<nint, uint, nint, ulong, uint, uint> Release;
}

public sealed record KernelEtwAllocationTagUsage(uint Tag, uint Owner, uint Scope,
    ulong RequestedBytes, ulong PeakRequestedBytes, ulong CommittedBytes, ulong PeakCommittedBytes,
    int Allocations, int PendingReleases)
{
    public ulong DerivedPeakCommittedBytes { get; init; }
}

public sealed record KernelEtwAllocationOwnerUsage(uint Owner,
    ulong RequestedBytes, ulong PeakRequestedBytes, ulong CommittedBytes, ulong PeakCommittedBytes,
    int Allocations, int PendingReleases)
{
    public ulong ByteCap { get; init; }
    public ulong DerivedPeakCommittedBytes { get; init; }
}

public sealed record KernelEtwAllocatorUsage(string Policy, string CommitMeasurement,
    ulong RequestedBytes, ulong PeakRequestedBytes, ulong CommittedBytes, ulong PeakCommittedBytes,
    ulong ByteCap, int Allocations, int PendingReleases,
    IReadOnlyList<KernelEtwAllocationTagUsage> Tags)
{
    public IReadOnlyList<KernelEtwAllocationOwnerUsage> Owners { get; init; } = [];
    public ulong DerivedPeakCommittedBytes { get; init; }
    public string PeakMeasurement { get; init; } = "PeakCommittedBytes: completed callback observations; DerivedPeakCommittedBytes: successful replacement overlap from allocate-copy-release contract";
    public string? LastFailure { get; init; }
    public KernelEtwAllocationFailure? AllocationFailure { get; init; }
}

/// <summary>Failure-site facts, captured before the native caller unwinds its temporary allocations.</summary>
public sealed record KernelEtwAllocationFailure(uint Tag, ulong RequestedBytes,
    int ActiveAllocations, int PendingReleases, ulong CommittedBytes);

/// <summary>One host allocation. Growth preserves identity and must fail unchanged when borrowed.</summary>
public abstract class NativeKernelEtwMemoryBlock : IDisposable
{
    public abstract nint Pointer { get; }
    public abstract ulong Length { get; }
    public abstract ulong CommittedBytes { get; }
    public abstract void Grow(ulong length);
    public abstract void Dispose();
}

/// <summary>
/// Owns the callback context until Native has returned every block. Native uses
/// owner-local pools for small objects; callbacks allocate page-backed datasets.
/// </summary>
public abstract unsafe class NativeKernelEtwHostAllocator : IDisposable
{
    private sealed class Entry(uint tag, ulong requested)
    {
        internal readonly uint Tag = tag;
        internal NativeKernelEtwMemoryBlock Block = null!;
        internal ulong Requested = requested;
        internal ulong Committed;
        internal bool Returned;
    }

    private sealed class TagUsage
    {
        internal ulong Requested, PeakRequested, Committed, PeakCommitted, DerivedPeakCommitted;
    }

    private readonly object gate = new();
    private readonly Dictionary<nint, Entry> entries = [];
    private readonly Dictionary<uint, TagUsage> tags = [];
    private readonly TagUsage[] owners = Enumerable.Range(0, 9).Select(_ => new TagUsage()).ToArray();
    private readonly string policy;
    private readonly ulong byteCap;
    private readonly ulong pageSize;
    private readonly ulong[] ownerCaps;
    private GCHandle callbackRoot;
    private bool disposed;
    private ulong requested, peakRequested, committed, peakCommitted, derivedPeakCommitted;
    private Exception? lastFailure;
    private KernelEtwAllocationFailure? allocationFailure;

    protected NativeKernelEtwHostAllocator(string policy, ulong byteCap, ulong pageSize)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(policy);
        if (byteCap == 0 || pageSize == 0 || (pageSize & (pageSize - 1)) != 0)
            throw new ArgumentOutOfRangeException(nameof(byteCap));
        this.policy = policy;
        this.byteCap = byteCap;
        this.pageSize = pageSize;
        ownerCaps = Enumerable.Repeat(byteCap, 9).ToArray();
        callbackRoot = GCHandle.Alloc(this);
    }

    public Exception? LastFailure { get { lock (gate) return lastFailure; } }

    public NativeKernelEtwHostAllocatorCallbacks Callbacks
    {
        get
        {
            lock (gate)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                return new() { Size = checked((uint)sizeof(NativeKernelEtwHostAllocatorCallbacks)), Version = 1,
                    PageSize = checked((nuint)pageSize), Context = GCHandle.ToIntPtr(callbackRoot),
                    Allocate = &AllocateCallback, Remap = &RemapCallback, Release = &ReleaseCallback };
            }
        }
    }

    protected abstract NativeKernelEtwMemoryBlock CreateBlock(uint tag, ulong pageRoundedLength);

    /// <summary>Sets the compiled owner caps before the first Native allocation.</summary>
    public void ConfigureOwnerByteCaps(IReadOnlyList<ulong> caps)
    {
        ArgumentNullException.ThrowIfNull(caps);
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (entries.Count != 0 || tags.Count != 0)
                throw new InvalidOperationException("Configure native owner caps before allocation begins.");
            if (caps.Count != ownerCaps.Length || caps.Any(cap => cap == 0 || cap > byteCap))
                throw new ArgumentOutOfRangeException(nameof(caps));
            for (var index = 0; index < ownerCaps.Length; index++) ownerCaps[index] = caps[index];
        }
    }

    protected TResult WithAllocation<TResult>(nint pointer, Func<NativeKernelEtwMemoryBlock, TResult> read)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (!entries.TryGetValue(pointer, out var entry) || entry.Returned)
                throw new InvalidOperationException("Kernel ETW allocation is not active.");
            return read(entry.Block);
        }
    }

    public nint Allocate(uint tag, ulong bytes, uint alignmentLog2)
    {
        lock (gate)
        {
            Entry? added = null;
            nint addedPointer = 0;
            try
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                Validate(tag, bytes, alignmentLog2);
                var length = Round(bytes);
                if (length > byteCap - committed) throw new OutOfMemoryException("Kernel ETW committed-byte cap exceeded.");
                if (length > ownerCaps[tag & 255] - owners[tag & 255].Committed)
                    throw new OutOfMemoryException("Kernel ETW owner committed-byte cap exceeded.");
                // Reserve managed bookkeeping before creating OS/Ledger pages.
                // An exhausted managed heap must not orphan an untracked block.
                entries.EnsureCapacity(checked(entries.Count + 1));
                if (!tags.ContainsKey(tag)) tags.Add(tag, new());
                var entry = new Entry(tag, bytes);
                var block = CreateBlock(tag, length);
                entry.Block = block;
                entry.Committed = block.CommittedBytes;
                entries.Add(block.Pointer, entry);
                added = entry;
                addedPointer = block.Pointer;
                Account(tag, 0, bytes, 0, entry.Committed);
                if (block.Pointer == 0 || block.Length < length || ((ulong)block.Pointer & ((1UL << checked((int)alignmentLog2)) - 1)) != 0)
                    throw new InvalidOperationException("Kernel ETW allocation returned an invalid or misaligned block.");
                return block.Pointer;
            }
            catch (Exception error)
            {
                allocationFailure = new(tag, bytes, entries.Values.Count(entry => !entry.Returned),
                    entries.Values.Count(entry => entry.Returned), committed);
                if (added is not null)
                {
                    added.Returned = true;
                    Account(tag, added.Requested, 0, 0, 0);
                    added.Requested = 0;
                    _ = TryRetire(addedPointer, added);
                }
                lastFailure = error;
                return 0;
            }
        }
    }

    public nint Remap(uint tag, nint pointer, ulong oldBytes, ulong newBytes, uint alignmentLog2)
    {
        lock (gate)
        {
            try
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                Validate(tag, newBytes, alignmentLog2);
                var entry = Find(tag, pointer, oldBytes);
                var nextLength = Round(newBytes);
                var replaces = nextLength > entry.Block.Length;
                // Ledger region.resize allocates the replacement before copying
                // and releasing old pages. Admit the full overlap, not the delta.
                if (replaces && (nextLength > byteCap - committed ||
                    nextLength > ownerCaps[tag & 255] - owners[tag & 255].Committed))
                    throw new OutOfMemoryException("Kernel ETW replacement commit would exceed its global or owner cap.");
                var beforeCommitted = committed;
                var beforeTagCommitted = tags[tag].Committed;
                var beforeOwnerCommitted = owners[tag & 255].Committed;
                // A shrink keeps its occupied pages. The Ledger only supports
                // non-forced growth; the whole block returns on window/output end.
                if (nextLength > entry.Block.Length) entry.Block.Grow(nextLength);
                var next = entry.Block.Pointer;
                if (next == 0 || ((ulong)next & ((1UL << checked((int)alignmentLog2)) - 1)) != 0)
                    throw new InvalidOperationException("Kernel ETW growth returned an invalid address.");
                if (newBytes > oldBytes) Clear(next, oldBytes, newBytes - oldBytes);
                var newCommit = entry.Block.CommittedBytes;
                if (replaces && next != pointer)
                {
                    derivedPeakCommitted = Math.Max(derivedPeakCommitted, checked(beforeCommitted + newCommit));
                    tags[tag].DerivedPeakCommitted = Math.Max(tags[tag].DerivedPeakCommitted, checked(beforeTagCommitted + newCommit));
                    owners[tag & 255].DerivedPeakCommitted = Math.Max(owners[tag & 255].DerivedPeakCommitted, checked(beforeOwnerCommitted + newCommit));
                }
                entries.Remove(pointer);
                entries.Add(next, entry);
                Account(tag, entry.Requested, newBytes, entry.Committed, newCommit);
                entry.Requested = newBytes;
                entry.Committed = newCommit;
                return next;
            }
            catch (Exception error) { lastFailure = error; return 0; }
        }
    }

    public uint Release(uint tag, nint pointer, ulong bytes, uint alignmentLog2)
    {
        lock (gate)
        {
            try
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                Validate(tag, bytes, alignmentLog2);
                var entry = Find(tag, pointer, bytes);
                entry.Returned = true;
                Account(tag, entry.Requested, 0, 0, 0);
                entry.Requested = 0;
                return TryRetire(pointer, entry) ? 0u : 1u;
            }
            catch (Exception error) { lastFailure = error; return 1; }
        }
    }

    public KernelEtwAllocatorUsage GetUsage()
    {
        lock (gate)
        {
            // A returned Ledger block can finish retiring when its last sibling
            // lease ends, before our next cleanup retry reaches the obligation.
            foreach (var entry in entries.Values)
            {
                var actual = entry.Block.CommittedBytes;
                if (actual == entry.Committed) continue;
                Account(entry.Tag, 0, 0, entry.Committed, actual);
                entry.Committed = actual;
            }
            var rows = tags.OrderBy(pair => pair.Key).Select(pair => new KernelEtwAllocationTagUsage(
                pair.Key, pair.Key & 255, pair.Key >> 8, pair.Value.Requested, pair.Value.PeakRequested,
                pair.Value.Committed, pair.Value.PeakCommitted,
                entries.Values.Count(entry => entry.Tag == pair.Key && !entry.Returned),
                entries.Values.Count(entry => entry.Tag == pair.Key && entry.Returned))
                { DerivedPeakCommittedBytes = pair.Value.DerivedPeakCommitted }).ToArray();
            var ownerRows = owners.Select((row, owner) => new KernelEtwAllocationOwnerUsage(
                checked((uint)owner), row.Requested, row.PeakRequested, row.Committed, row.PeakCommitted,
                entries.Values.Count(entry => (entry.Tag & 255) == owner && !entry.Returned),
                entries.Values.Count(entry => (entry.Tag & 255) == owner && entry.Returned))
                { ByteCap = ownerCaps[owner], DerivedPeakCommittedBytes = row.DerivedPeakCommitted }).ToArray();
            return new(policy, OperatingSystem.IsWindows() ? "VirtualQuery" : "bounded-allocation-length",
                requested, peakRequested, committed, peakCommitted, byteCap,
                entries.Values.Count(entry => !entry.Returned), entries.Values.Count(entry => entry.Returned), rows)
                { Owners = ownerRows, DerivedPeakCommittedBytes = derivedPeakCommitted,
                    LastFailure = lastFailure?.ToString(), AllocationFailure = allocationFailure };
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            if (entries.Values.Any(entry => !entry.Returned))
                throw new InvalidOperationException("Native kernel ETW still owns allocations; destroy/drain it before disposing callbacks.");
            foreach (var pair in entries.ToArray()) _ = TryRetire(pair.Key, pair.Value);
            if (entries.Count != 0) throw new InvalidOperationException("Kernel ETW allocation retirement remains pending.", lastFailure);
            callbackRoot.Free();
            disposed = true;
        }
    }

    private Entry Find(uint tag, nint pointer, ulong bytes)
    {
        if (pointer == 0 || !entries.TryGetValue(pointer, out var entry) || entry.Tag != tag || entry.Returned || entry.Requested != bytes)
            throw new InvalidOperationException("Kernel ETW allocation identity, tag or length does not match.");
        return entry;
    }

    private bool TryRetire(nint pointer, Entry entry)
    {
        try
        {
            entry.Block.Dispose();
            entries.Remove(pointer);
            Account(entry.Tag, 0, 0, entry.Committed, 0);
            return true;
        }
        catch (Exception error) { lastFailure = error; return false; }
    }

    private void Validate(uint tag, ulong bytes, uint alignmentLog2)
    {
        if ((tag & 255) > 8 || (tag >> 8) > 9 || bytes == 0 || alignmentLog2 >= 64 || (1UL << checked((int)alignmentLog2)) > pageSize)
            throw new ArgumentOutOfRangeException(nameof(bytes), "Unknown output owner, zero allocation or unsupported alignment.");
    }

    private ulong Round(ulong bytes) => checked((bytes + pageSize - 1) & ~(pageSize - 1));

    private void Account(uint tag, ulong oldRequested, ulong nextRequested, ulong oldCommitted, ulong nextCommitted)
    {
        requested = checked(requested - oldRequested + nextRequested);
        committed = checked(committed - oldCommitted + nextCommitted);
        peakRequested = Math.Max(peakRequested, requested);
        peakCommitted = Math.Max(peakCommitted, committed);
        derivedPeakCommitted = Math.Max(derivedPeakCommitted, committed);
        if (!tags.TryGetValue(tag, out var row)) tags.Add(tag, row = new());
        row.Requested = checked(row.Requested - oldRequested + nextRequested);
        row.Committed = checked(row.Committed - oldCommitted + nextCommitted);
        row.PeakRequested = Math.Max(row.PeakRequested, row.Requested);
        row.PeakCommitted = Math.Max(row.PeakCommitted, row.Committed);
        row.DerivedPeakCommitted = Math.Max(row.DerivedPeakCommitted, row.Committed);
        var owner = owners[tag & 255];
        owner.Requested = checked(owner.Requested - oldRequested + nextRequested);
        owner.Committed = checked(owner.Committed - oldCommitted + nextCommitted);
        owner.PeakRequested = Math.Max(owner.PeakRequested, owner.Requested);
        owner.PeakCommitted = Math.Max(owner.PeakCommitted, owner.Committed);
        owner.DerivedPeakCommitted = Math.Max(owner.DerivedPeakCommitted, owner.Committed);
    }

    private static void Clear(nint pointer, ulong offset, ulong count)
    {
        while (count != 0)
        {
            var chunk = checked((int)Math.Min(count, (ulong)int.MaxValue));
            new Span<byte>((byte*)pointer + checked((nuint)offset), chunk).Clear();
            offset += checked((uint)chunk);
            count -= checked((uint)chunk);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static nint AllocateCallback(nint context, uint tag, ulong bytes, uint alignment)
    { try { return ((NativeKernelEtwHostAllocator)GCHandle.FromIntPtr(context).Target!).Allocate(tag, bytes, alignment); } catch { return 0; } }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static nint RemapCallback(nint context, uint tag, nint pointer, ulong oldBytes, ulong newBytes, uint alignment)
    { try { return ((NativeKernelEtwHostAllocator)GCHandle.FromIntPtr(context).Target!).Remap(tag, pointer, oldBytes, newBytes, alignment); } catch { return 0; } }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static uint ReleaseCallback(nint context, uint tag, nint pointer, ulong bytes, uint alignment)
    { try { return ((NativeKernelEtwHostAllocator)GCHandle.FromIntPtr(context).Target!).Release(tag, pointer, bytes, alignment); } catch { return 1; } }

    /// <summary>Checks actual committed pages of the allocation's declared extent.</summary>
    public static ulong MeasureCommittedBytes(nint pointer, ulong length)
    {
        if (!OperatingSystem.IsWindows()) return length;
        var page = checked((ulong)Environment.SystemPageSize);
        var rounded = checked((length + page - 1) & ~(page - 1));
        var end = checked((nuint)pointer + checked((nuint)rounded));
        var cursor = (nuint)pointer;
        ulong committedBytes = 0;
        while (cursor < end)
        {
            if (VirtualQuery((nint)cursor, out var information, checked((nuint)sizeof(MemoryBasicInformation))) == 0)
                throw new InvalidOperationException("Cannot query kernel ETW allocation commit.");
            var regionEnd = checked((nuint)information.BaseAddress + information.RegionSize);
            var through = nuint.Min(regionEnd, end);
            if (through <= cursor || information.State != 0x1000)
                throw new InvalidOperationException("Kernel ETW allocation is not fully committed.");
            committedBytes += checked((ulong)(through - cursor));
            cursor = through;
        }
        return committedBytes;
    }

    [StructLayout(LayoutKind.Sequential)] private struct MemoryBasicInformation
    {
        public nint BaseAddress, AllocationBase;
        public uint AllocationProtect;
        public ushort PartitionId;
        public nuint RegionSize;
        public uint State, Protect, Type;
    }
    [DllImport("kernel32", SetLastError = true)]
    private static extern nuint VirtualQuery(nint address, out MemoryBasicInformation information, nuint length);
}
