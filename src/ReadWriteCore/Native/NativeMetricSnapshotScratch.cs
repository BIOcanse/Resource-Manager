using System.Buffers;
using System.Runtime.InteropServices;

namespace ResourceManager.ReadWrite.Native;

// Internal storage contract; independent public consumers explicitly use ordinary memory.
internal interface INativeMetricSnapshotScratch : IDisposable
{
    ulong Length { get; }
    NativeMetricSnapshotScratchLease Acquire();
}

internal abstract class NativeMetricSnapshotScratchLease : IDisposable
{
    internal abstract ulong Length { get; }
    // Pin covers the full contiguous resource, even when the backing memory chunk is short.
    internal abstract MemoryHandle Pin();
    internal abstract void Complete();
    public abstract void Dispose();
}

internal static unsafe class NativeMetricSnapshotScratch
{
    internal static INativeMetricSnapshotScratch CreateOrdinary(ulong length, uint alignment)
        => new Ordinary(length, alignment);

    private sealed class Ordinary(ulong length, uint alignment) : INativeMetricSnapshotScratch
    {
        private void* pointer;
        private bool active;
        private bool disposed;
        public ulong Length => length;

        public NativeMetricSnapshotScratchLease Acquire()
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (active) throw new InvalidOperationException("The metric scratch is already in use.");
            if (pointer == null)
            {
                pointer = NativeMemory.AlignedAlloc(checked((nuint)length), alignment);
                if (pointer == null) throw new OutOfMemoryException();
            }
            var lease = new OrdinaryLease(this);
            active = true;
            return lease;
        }

        public void Dispose()
        {
            if (active) throw new InvalidOperationException("The metric scratch is still in use.");
            NativeMemory.AlignedFree(pointer);
            pointer = null;
            disposed = true;
        }

        private sealed class OrdinaryLease(Ordinary owner) : NativeMetricSnapshotScratchLease
        {
            private bool disposed;
            internal override ulong Length => owner.Length;
            internal override MemoryHandle Pin()
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                return new MemoryHandle(owner.pointer);
            }
            internal override void Complete() => ObjectDisposedException.ThrowIf(disposed, this);
            public override void Dispose()
            {
                if (disposed) return;
                owner.active = false;
                disposed = true;
            }
        }
    }
}
