using System.Runtime.CompilerServices;
using ResourceManager.App.Infrastructure.Adaptation;

namespace ResourceManager.App.Infrastructure.NativeCore;

internal sealed class NativeTransactionJournalWorkspace : IDisposable
{
    private readonly IComputeScratch scratch;
    private readonly int recordCount;
    private readonly ulong imageOffset;
    private readonly int imageLength;
    private Operation? active;
    private bool endUsePending;
    private bool retiring;
    private bool disposed;

    public NativeTransactionJournalWorkspace(in NativeTransactionJournalCapacity capacity,
        Func<ulong, IComputeScratch> createScratch)
    {
        ArgumentNullException.ThrowIfNull(createScratch);
        if (capacity.RecordCapacity == 0 ||
            capacity.MaximumImageLength < NativeTransactionJournalAbi.ImageHeaderSize ||
            capacity.MaximumImageLength > int.MaxValue ||
            capacity.ResidentBytes == 0)
        {
            throw new InvalidOperationException(
                "Transaction journal workspace capacity is invalid.");
        }

        recordCount = checked((int)capacity.RecordCapacity);
        imageLength = checked((int)capacity.MaximumImageLength);
        imageOffset = checked((ulong)recordCount * (ulong)Unsafe.SizeOf<NativeTransactionJournalRecord>());
        Length = checked(imageOffset + (ulong)imageLength);
        scratch = createScratch(Length)
            ?? throw new InvalidOperationException("The journal scratch factory returned no resource.");
        if (scratch.Length != Length)
        {
            scratch.Dispose();
            throw new InvalidOperationException("The journal scratch layout length is incorrect.");
        }
    }

    internal ulong Length { get; }

    internal Operation Acquire()
    {
        ObjectDisposedException.ThrowIf(disposed || retiring, this);
        if (active is not null)
            throw new InvalidOperationException("The previous journal scratch use has not ended.");
        active = new(this, scratch.Acquire());
        return active;
    }

    internal sealed class Operation(NativeTransactionJournalWorkspace owner, ComputeScratchLease lease) : IDisposable
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

        internal NativeTransactionJournalSnapshot ReadSnapshot(NativeTransactionJournalSession session)
        {
            RequireActive();
            using var view = new ComputeScratchView(lease);
            var records = view.Column<NativeTransactionJournalRecord>(0, owner.recordCount);
            records.Clear();
            var header = new NativeTransactionJournalSnapshotHeader
            {
                AbiVersion = NativeTransactionJournalAbi.Version,
                StructSize = NativeTransactionJournalSession.SizeOf<NativeTransactionJournalSnapshotHeader>()
            };
            var status = session.GetSnapshot(ref header, records);
            if (status != NativeTransactionJournalStatus.Ok || header.EntryCount > records.Length)
                throw new InvalidOperationException($"Native transaction journal snapshot failed with {status}.");
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
                throw new InvalidOperationException("Journal buffers require their active operation lease.");
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
                throw new InvalidOperationException("The journal workspace is still in use.");
            active.RetryEndForClose();
            active = null;
            endUsePending = false;
        }
        scratch.Dispose();
        disposed = true;
    }
}
