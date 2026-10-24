using System.Buffers;
using System.Runtime.InteropServices;

namespace ResourceManager.ReadWrite.Native;

// Internal storage contract; independent public consumers explicitly use ordinary memory.
internal interface INativeFileQueryScratch : IDisposable
{
    ulong Length { get; }
    NativeFileQueryScratchLease Acquire();
}

internal abstract class NativeFileQueryScratchLease : IDisposable
{
    internal abstract ulong Length { get; }
    // Pin covers the full contiguous resource, even when the backing memory chunk is short.
    internal abstract MemoryHandle Pin();
    internal abstract void Complete();
    public abstract void Dispose();
}

internal static unsafe class NativeFileQueryScratch
{
    internal static INativeFileQueryScratch CreateOrdinary(ulong length, uint alignment)
        => new Ordinary(length, alignment);

    private sealed class Ordinary(ulong length, uint alignment) : INativeFileQueryScratch
    {
        private void* pointer;
        private bool active;
        private bool disposed;
        public ulong Length => length;

        public NativeFileQueryScratchLease Acquire()
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (active) throw new InvalidOperationException("The file-query scratch is already in use.");
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
            if (active) throw new InvalidOperationException("The file-query scratch is still in use.");
            NativeMemory.AlignedFree(pointer);
            pointer = null;
            disposed = true;
        }

        private sealed class OrdinaryLease(Ordinary owner) : NativeFileQueryScratchLease
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
