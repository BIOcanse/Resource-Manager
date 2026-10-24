using System.Runtime.CompilerServices;
using ResourceManager.App.Infrastructure.Adaptation;

namespace ResourceManager.App.Infrastructure.NativeCore;

internal sealed class NativeAppliedOwnershipWorkspace : IDisposable
{
    private readonly RowSizedComputeScratch scratch;
    private readonly uint maximumRecords;
    private int recordCount;
    private ulong imageOffset;
    private int imageLength;
    private Operation? active;
    private bool endUsePending;
    private bool retiring;
    private bool disposed;

    public NativeAppliedOwnershipWorkspace(in NativeAppliedOwnershipCapacity capacity,
        Func<ulong, IComputeScratch> createScratch)
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

        maximumRecords = capacity.RecordCapacity;
        scratch = new RowSizedComputeScratch(createScratch, "ownership");
        EnsureRows(0);
    }

    internal ulong Length => scratch.Length;

    internal static int ImageLengthForRows(uint rows)
        => checked((int)((ulong)NativeAppliedOwnershipAbi.ImageHeaderSize +
            (ulong)rows * NativeAppliedOwnershipAbi.RecordSize));

    internal void Ensure(INativeAppliedOwnershipSession session, uint maximumAdditions = 0)
    {
        ObjectDisposedException.ThrowIf(disposed || retiring, this);
        if (active is not null)
            throw new InvalidOperationException("The previous ownership scratch use has not ended.");
        var header = default(NativeAppliedOwnershipSnapshotHeader);
        var status = session.GetSnapshot(ref header, Span<NativeAppliedOwnershipRecord>.Empty);
        if ((status != NativeAppliedOwnershipStatus.Ok &&
             status != NativeAppliedOwnershipStatus.BufferTooSmall) || header.EntryCount > maximumRecords)
            throw new InvalidOperationException($"Native ownership count query failed with {status}.");
        var rows = checked((uint)Math.Min((ulong)maximumRecords,
            (ulong)header.EntryCount + maximumAdditions));
        EnsureRows(rows);
    }

    private void EnsureRows(uint rows)
    {
        if (rows <= recordCount && scratch.Length != 0) return;
        var nextImageOffset = checked((ulong)rows * (ulong)Unsafe.SizeOf<NativeAppliedOwnershipRecord>());
        var nextImageLength = ImageLengthForRows(rows);
        var length = checked(nextImageOffset + (ulong)nextImageLength);
        scratch.Ensure(length, $"{rows} records");
        recordCount = checked((int)rows);
        imageOffset = nextImageOffset;
        imageLength = nextImageLength;
    }

    internal Operation Acquire()
    {
        ObjectDisposedException.ThrowIf(disposed || retiring, this);
        if (active is not null)
            throw new InvalidOperationException("The previous ownership scratch use has not ended.");
        active = new(this, scratch.Acquire());
        return active;
    }

    internal sealed class Operation(NativeAppliedOwnershipWorkspace owner, ComputeScratchLease lease) : IDisposable
    {
        private bool complete;
        private bool ended;

        internal Memory<byte> Image
        {
            get
            {
                RequireActive();
                return lease.GetMemoryChunk(owner.imageOffset, owner.imageLength);
            }
        }

        internal NativeAppliedOwnershipSnapshot ReadSnapshot(INativeAppliedOwnershipSession session)
        {
            RequireActive();
            using var view = new ComputeScratchView(lease);
            var records = view.Column<NativeAppliedOwnershipRecord>(0, owner.recordCount);
            records.Clear();
            var header = default(NativeAppliedOwnershipSnapshotHeader);
            var status = session.GetSnapshot(ref header, records);
            if (status != NativeAppliedOwnershipStatus.Ok || header.EntryCount > records.Length)
                throw new InvalidOperationException($"Native applied ownership snapshot failed with {status}.");
            return new(header, records[..checked((int)header.EntryCount)].ToArray());
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
            lease.Dispose(); // A failed end remains reachable for explicit workspace close.
            owner.active = null;
            owner.endUsePending = false;
        }

        internal void RetryEndForClose() => lease.Dispose();
    }

    public void Dispose()
    {
        if (disposed) return;
        retiring = true;
        if (active is not null)
        {
            if (!endUsePending)
                throw new InvalidOperationException("The ownership workspace is still in use.");
            active.RetryEndForClose();
            active = null;
            endUsePending = false;
        }
        scratch.Dispose();
        disposed = true;
    }
}
