using ResourceManager.App.Infrastructure.Adaptation;

namespace ResourceManager.App.Infrastructure.NativeCore;

// The native session holds the only stored copy of applied ownership; promote, transition,
// remove, get, plan and encode all read it. Nothing is held here between operations: a
// snapshot is copied straight into the array its reader keeps, and a mutation leases an
// image region sized for this operation's rows only to encode and persist, then closes it.
internal sealed class NativeAppliedOwnershipWorkspace : IDisposable
{
    private const string WorkArea = "ownership";
    private readonly Func<ulong, IComputeScratch> createScratch;
    private readonly uint maximumRecords;
    private readonly IDisposable? nativeCapacityRegistration;
    private readonly INativeWorkspaceCapacitySource? observedNativeCapacity;
    private Operation? active;
    private bool endUsePending;
    private bool retiring;
    private bool disposed;

    public NativeAppliedOwnershipWorkspace(in NativeAppliedOwnershipCapacity capacity,
        Func<ulong, IComputeScratch> createScratch,
        INativeWorkspaceCapacitySource? nativeCapacity = null)
    {
        ArgumentNullException.ThrowIfNull(createScratch);
        if (capacity.RecordCapacity == 0 ||
            capacity.RecordSize != NativeAppliedOwnershipAbi.RecordSize ||
            capacity.MaximumImageBytes < NativeAppliedOwnershipAbi.ImageHeaderSize ||
            capacity.MaximumImageBytes > int.MaxValue ||
            capacity.ResidentBytes == 0)
        {
            throw new InvalidOperationException(
                "Applied ownership workspace capacity is invalid.");
        }

        this.createScratch = createScratch;
        maximumRecords = capacity.RecordCapacity;
        if (nativeCapacity is not null && createScratch.Target is IWorkspaceCapacityRegistry registry)
        {
            nativeCapacityRegistration = registry.RegisterNativeWorkspace(WorkArea + "-native",
                nativeCapacity.ReadWorkspaceCapacity);
            try { nativeCapacity.SetGrowthObserver(registry.LogNativeGrowth); }
            catch
            {
                nativeCapacityRegistration.Dispose();
                throw;
            }
            observedNativeCapacity = nativeCapacity;
        }
    }

    // The current operation's region; zero between operations.
    internal ulong Length => active?.Length ?? 0;

    internal static int ImageLengthForRows(uint rows)
        => checked((int)((ulong)NativeAppliedOwnershipAbi.ImageHeaderSize +
            (ulong)rows * NativeAppliedOwnershipAbi.RecordSize));

    internal NativeAppliedOwnershipSnapshot ReadSnapshot(INativeAppliedOwnershipSession session)
    {
        RequireIdle();
        var records = new NativeAppliedOwnershipRecord[CountRecords(session)];
        var header = default(NativeAppliedOwnershipSnapshotHeader);
        var status = session.GetSnapshot(ref header, records);
        if (status != NativeAppliedOwnershipStatus.Ok || header.EntryCount > records.Length)
            throw new InvalidOperationException($"Native applied ownership snapshot failed with {status}.");
        return new(header, header.EntryCount == records.Length ? records : records[..checked((int)header.EntryCount)]);
    }

    // Sized before the mutation runs, so an allocation refusal leaves the authority unchanged.
    internal Operation BeginMutation(INativeAppliedOwnershipSession session, uint maximumAdditions)
    {
        RequireIdle();
        return Acquire(checked((uint)Math.Min((ulong)maximumRecords,
            (ulong)CountRecords(session) + maximumAdditions)));
    }

    internal Operation Acquire(uint rows)
    {
        RequireIdle();
        var length = (ulong)ImageLengthForRows(rows);
        var scratch = createScratch(length)
            ?? throw new InvalidOperationException($"{WorkArea} scratch factory returned no resource.");
        ComputeScratchLease lease;
        try
        {
            if (scratch.Length != length)
                throw new InvalidOperationException($"{WorkArea} scratch factory returned the wrong length.");
            if (scratch is ResourceManagerLedgerOwner.ComputeScratch registered)
            {
                registered.WorkArea = WorkArea;
                registered.Rows = $"{rows} records";
            }
            lease = scratch.Acquire();
        }
        catch (Exception failure)
        {
            try { scratch.Dispose(); }
            catch (Exception cleanup) { throw new AggregateException(failure, cleanup); }
            throw;
        }
        active = new(this, scratch, lease);
        return active;
    }

    private uint CountRecords(INativeAppliedOwnershipSession session)
    {
        var header = default(NativeAppliedOwnershipSnapshotHeader);
        var status = session.GetSnapshot(ref header, Span<NativeAppliedOwnershipRecord>.Empty);
        if ((status != NativeAppliedOwnershipStatus.Ok &&
             status != NativeAppliedOwnershipStatus.BufferTooSmall) || header.EntryCount > maximumRecords)
            throw new InvalidOperationException($"Native ownership count query failed with {status}.");
        return header.EntryCount;
    }

    private void RequireIdle()
    {
        ObjectDisposedException.ThrowIf(disposed || retiring, this);
        if (active is not null)
            throw new InvalidOperationException("The previous ownership scratch use has not ended.");
    }

    internal sealed class Operation(NativeAppliedOwnershipWorkspace owner, IComputeScratch scratch,
        ComputeScratchLease lease) : IDisposable
    {
        private bool complete;
        private bool ended;
        private bool leaseEnded;

        internal ulong Length => scratch.Length;

        internal Memory<byte> Image
        {
            get
            {
                RequireActive();
                return lease.GetMemoryChunk(0, checked((int)scratch.Length));
            }
        }

        internal void Complete()
        {
            RequireActive();
            complete = true;
        }

        private void RequireActive()
        {
            if (ended || owner.endUsePending || !ReferenceEquals(owner.active, this))
                throw new InvalidOperationException("Ownership buffers require their active operation lease.");
        }

        public void Dispose()
        {
            if (ended) return;
            ended = true;
            owner.endUsePending = true;
            if (complete) lease.Complete();
            End(); // A failed end or close remains reachable for explicit workspace close.
            owner.active = null;
            owner.endUsePending = false;
        }

        // Ends this operation's use, then closes its region; each step is retried until it succeeds.
        internal void End()
        {
            if (!leaseEnded)
            {
                lease.Dispose();
                leaseEnded = true;
            }
            scratch.Dispose();
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        retiring = true;
        if (active is not null)
        {
            if (!endUsePending)
                throw new InvalidOperationException("The ownership workspace is still in use.");
            active.End();
            active = null;
            endUsePending = false;
        }
        observedNativeCapacity?.SetGrowthObserver(null);
        nativeCapacityRegistration?.Dispose();
        disposed = true;
    }
}
