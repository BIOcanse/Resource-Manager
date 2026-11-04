using System.Buffers;
using ResourceManager.App.Infrastructure.Adaptation;

namespace ResourceManager.App.Infrastructure.Telemetry.Etw;

// Native datasets have stable identity only while native code uses their pointers.
// Native output/window/bank teardown returns them; the last runtime hold drains
// every reader and destroys the root before disposing its callback context.
internal sealed class NativeKernelEtwLedgerRegionProvider(ResourceManagerLedgerOwner ledger)
    : IKernelEtwRegionProvider, IKernelEtwAllocationProvider
{
    public NativeKernelEtwHostAllocator CreateAllocator(string sessionName, ulong byteCap)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionName);
        return new Allocator(ledger, byteCap);
    }

    public IKernelEtwRegionLease Allocate(string owner, nuint minimumLength)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        if (minimumLength == 0) throw new ArgumentOutOfRangeException(nameof(minimumLength));
        var resource = ledger.CreateScratch(checked((ulong)minimumLength));
        resource.DescribeGrowth(owner, "none", "one native session region", 0);
        try { return new Region(resource); }
        catch { resource.Dispose(); throw; }
    }

    internal sealed class Allocator(ResourceManagerLedgerOwner ledger, ulong cap)
        : NativeKernelEtwHostAllocator("ledger", cap, ledger.NativePageSize)
    {
        protected override NativeKernelEtwMemoryBlock CreateBlock(uint tag, ulong pageRoundedLength)
        {
            var resource = ledger.CreateScratch(pageRoundedLength);
            resource.WorkArea = $"kernel-etw/{tag & 255}/scope{tag >> 8}";
            resource.Rows = "native dataset";
            try { return new Block(resource); }
            catch { resource.Dispose(); throw; }
        }

        // A real counted second lease for allocation/reference contract tests.
        internal ComputeScratchLease AcquireReference(nint pointer)
            => WithAllocation(pointer, block => ((Block)block).AcquireReference());
    }

    private sealed unsafe class Block : NativeKernelEtwMemoryBlock
    {
        private readonly ResourceManagerLedgerOwner.ComputeScratch resource;
        // Internal callback-to-Ledger lifetime credential, not a business cache:
        // native uses this block continuously until its matching Release callback.
        // Dispose returns the pin and lease together; Grow repins only relocation.
        private ComputeScratchLease? access;
        private Memory<byte> oneByte;
        private MemoryHandle pin;
        private bool pinned;
        private ulong committed;
        private nint pointer;
        public override nint Pointer => pointer;
        public override ulong Length => resource.Length;
        public override ulong CommittedBytes => Volatile.Read(ref resource.Closed) ? 0 : committed;

        internal Block(ResourceManagerLedgerOwner.ComputeScratch resource)
        {
            this.resource = resource;
            access = resource.Acquire();
            try
            {
                // The storage starts as a complete zeroed allocator dataset.
                // Native business publications have their own immutable barrier.
                access.PublishAndRetain();
                oneByte = access.GetMemoryChunk(0, 1);
                Pin();
                committed = NativeKernelEtwHostAllocator.MeasureCommittedBytes(pointer, Length);
            }
            catch
            {
                Unpin();
                access.Dispose();
                access = null;
                throw;
            }
        }

        internal ComputeScratchLease AcquireReference() => resource.Acquire();

        public override void Grow(ulong length)
        {
            ObjectDisposedException.ThrowIf(access is null, this);
            if (length <= Length) return;
            // A manager prepared before growth follows the same SDK lease; its
            // Pin path allocates no new manager after a successful relocation.
            Unpin();
            try { access.Grow(length); }
            finally { Pin(); }
            committed = NativeKernelEtwHostAllocator.MeasureCommittedBytes(pointer, Length);
        }

        private void Pin()
        {
            pin = oneByte.Pin();
            pinned = true;
            pointer = (nint)pin.Pointer;
            if (pointer == 0) throw new InvalidOperationException("Ledger native dataset has no address.");
        }

        private void Unpin()
        {
            if (pinned) { pin.Dispose(); pinned = false; }
            pointer = 0;
        }

        public override void Dispose()
        {
            Unpin();
            access?.Dispose();
            access = null;
            resource.Dispose(); // Preserve a failed retirement for the next call.
            committed = 0;
        }
    }

    private sealed unsafe class Region : IKernelEtwRegionLease
    {
        private readonly IComputeScratch resource;
        // Legacy explicit-region ABI only. Production uses output-scoped Block
        // callbacks. The region's caller must dispose it after native ref-drain.
        private ComputeScratchLease? access;
        private MemoryHandle pin;
        private bool pinned;
        private bool complete;
        public nint Pointer { get; private set; }
        public nuint Length { get; }

        internal Region(IComputeScratch resource)
        {
            this.resource = resource;
            Length = checked((nuint)resource.Length);
            access = resource.Acquire();
            try { Pin(); }
            catch
            {
                if (pinned) { pin.Dispose(); pinned = false; }
                Pointer = 0;
                access.Dispose(); access = null;
                throw;
            }
        }

        private void Pin()
        {
            pin = access!.GetMemoryChunk(0, 1).Pin();
            pinned = true;
            Pointer = (nint)pin.Pointer;
            if (Pointer == 0) throw new InvalidOperationException("The kernel ETW region has no address.");
        }

        public void Complete()
        {
            ObjectDisposedException.ThrowIf(access is null, this);
            if (complete) return;
            var address = Pointer;
            if (pinned) { pin.Dispose(); pinned = false; }
            Pointer = 0;
            access.PublishAndRetain();
            Pin();
            if (Pointer != address)
                throw new InvalidOperationException("Publishing the retained kernel ETW region changed its address.");
            complete = true;
        }

        public void Dispose()
        {
            if (pinned) { pin.Dispose(); pinned = false; }
            Pointer = 0;
            // Keep the obligation if returning the lease or retiring the region
            // fails; repeated cleanup must still reach the same resource.
            access?.Dispose();
            access = null;
            resource.Dispose();
        }
    }
}
