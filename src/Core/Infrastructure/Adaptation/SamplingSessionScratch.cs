using System.Buffers;
using ResourceManager.Subscription.Native;

namespace ResourceManager.App.Infrastructure.Adaptation;

// One identity per seven-role workspace. Sessions borrow disjoint slices and
// never close the shared identity themselves.
internal sealed class SamplingSessionScratch(IComputeScratch resource) : IDisposable
{
    private readonly object sync = new();
    private Lease? loading;
    private int readers;
    private bool retiring;

    internal NativeSamplingSessionScratchLease Acquire(ulong offset, ulong length)
    {
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(retiring, this);
            while (loading is not null)
            {
                if (loading.ReturnFailed)
                    throw new InvalidOperationException("The sampling scratch load must finish its explicit return before another operation.");
                Monitor.Wait(sync);
                ObjectDisposedException.ThrowIf(retiring, this);
            }
            // Allocate wrapper metadata before the actual SDK acquisition.
            var lease = new Lease(this, offset, length);
            var use = resource.Acquire();
            lease.Attach(use);
            if (use.IsLoading) loading = lease;
            readers++;
            return lease;
        }
    }

    public void Dispose()
    {
        lock (sync)
        {
            retiring = true;
            Monitor.PulseAll(sync);
            if (readers != 0) throw new InvalidOperationException("Sampling scratch is still in use.");
            resource.Dispose();
        }
    }

    private void Release(Lease lease)
    {
        lock (sync)
        {
            if (lease.Returned) return;
            try { lease.EndUse(); }
            catch { LoadReturnFailed(lease); throw; }
            lease.Returned = true;
            readers--;
            if (ReferenceEquals(loading, lease)) loading = null;
            Monitor.PulseAll(sync);
        }
    }

    private void LoadReturnFailed(Lease lease)
    {
        lock (sync)
        {
            if (ReferenceEquals(loading, lease)) lease.ReturnFailed = true;
            Monitor.PulseAll(sync);
        }
    }

    private sealed class Lease(SamplingSessionScratch owner, ulong offset, ulong length)
        : NativeSamplingSessionScratchLease
    {
        private MemoryHandle pin;
        private bool pinned;
        private ComputeScratchLease use = null!;
        internal bool Returned, ReturnFailed;
        internal void Attach(ComputeScratchLease actual) => use = actual;
        internal void EndUse() => use.Dispose();
        internal override ulong Length => length;
        internal override unsafe MemoryHandle Pin()
        {
            ObjectDisposedException.ThrowIf(Returned, this);
            if (pinned) throw new InvalidOperationException("Sampling slice is already pinned.");
            pin = use.GetMemoryChunk(0, 1).Pin();
            pinned = true;
            try
            {
                if (pin.Pointer == null) throw new InvalidOperationException("Sampling scratch has no address.");
                var first = (nuint)pin.Pointer;
                var aligned = checked(first + 7) & ~(nuint)7;
                var start = checked((ulong)(aligned - first) + offset);
                if (start > use.Length || length > use.Length - start)
                    throw new InvalidOperationException("Sampling slice exceeds its allocation.");
                var address = checked(first + checked((nuint)start));
                _ = checked(address + checked((nuint)length));
                return new MemoryHandle((void*)address);
            }
            catch { Unpin(); throw; }
        }
        internal override void Complete()
        {
            ObjectDisposedException.ThrowIf(Returned, this);
            try { use.Complete(); }
            catch { owner.LoadReturnFailed(this); throw; }
        }
        public override void Dispose()
        {
            Unpin();
            owner.Release(this);
        }
        private void Unpin()
        {
            if (!pinned) return;
            pin.Dispose();
            pin = default;
            pinned = false;
        }
    }
}
