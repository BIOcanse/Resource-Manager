using ResourceManager.App.Infrastructure.Adaptation;

namespace Resource_Manager_APP.Tests;

// Explicit ordinary algorithm-test allocation, never a production fallback.
internal sealed class OperationScratchFixture(ulong length) : IComputeScratch
{
    private readonly byte[] bytes = new byte[checked((int)length)];
    internal int Acquires;
    internal int Returns;
    internal int Completed;
    internal bool FailAcquire;
    internal bool Disposed;
    public ulong Length => length;
    internal static IComputeScratch Create(ulong length) => new OperationScratchFixture(length);
    public ComputeScratchLease Acquire()
    {
        ObjectDisposedException.ThrowIf(Disposed, this);
        if (FailAcquire) throw new InvalidOperationException("recorded allocation unavailable");
        Array.Fill(bytes, (byte)0xA5);
        Acquires++;
        return new Reading(this);
    }
    public void Dispose() => Disposed = true;
    private sealed class Reading(OperationScratchFixture owner) : ComputeScratchLease
    {
        private bool disposed;
        internal override ulong Length => owner.Length;
        internal override ulong AllocationNumber => 0;
        internal override bool IsLoading => false;
        internal override Memory<byte> GetMemoryChunk(ulong offset, int count)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            return owner.bytes.AsMemory(checked((int)offset), count);
        }
        internal override void Complete()
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            owner.Completed++;
        }
        public override void Dispose()
        {
            if (disposed) return;
            disposed = true;
            owner.Returns++;
        }
    }
}
