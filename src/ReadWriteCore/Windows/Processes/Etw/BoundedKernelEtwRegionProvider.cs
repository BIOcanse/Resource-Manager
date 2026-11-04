using System.Runtime.InteropServices;

namespace ResourceManager.App.Infrastructure.Telemetry.Etw;

/// <summary>Explicit standalone allocation policy. Product hosts supply their ledger provider instead.</summary>
public sealed unsafe class BoundedKernelEtwRegionProvider
    : IKernelEtwRegionProvider, IKernelEtwAllocationProvider
{
    private readonly object gate = new();
    private readonly nuint byteCap;
    private nuint currentBytes, peakBytes;

    public BoundedKernelEtwRegionProvider(nuint byteCap)
    {
        if (byteCap == 0) throw new ArgumentOutOfRangeException(nameof(byteCap));
        this.byteCap = byteCap;
    }

    public KernelEtwRegionAllocationUsage GetUsage()
    {
        lock (gate) return new((ulong)currentBytes, (ulong)peakBytes, (ulong)byteCap);
    }

    public NativeKernelEtwHostAllocator CreateAllocator(string sessionName, ulong byteCap)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionName);
        return new Allocator(this, Math.Min(byteCap, (ulong)this.byteCap));
    }

    private sealed class Allocator(BoundedKernelEtwRegionProvider owner, ulong cap)
        : NativeKernelEtwHostAllocator("bounded-standalone", cap, checked((ulong)Environment.SystemPageSize))
    {
        protected override NativeKernelEtwMemoryBlock CreateBlock(uint tag, ulong pageRoundedLength)
            => new PageBlock(owner, pageRoundedLength);
    }

    private sealed class PageBlock : NativeKernelEtwMemoryBlock
    {
        private readonly BoundedKernelEtwRegionProvider owner;
        private nint pointer;
        private ulong length, committed;
        public override nint Pointer => pointer;
        public override ulong Length => length;
        public override ulong CommittedBytes => committed;

        internal PageBlock(BoundedKernelEtwRegionProvider owner, ulong length)
        {
            this.owner = owner;
            lock (owner.gate)
            {
                pointer = AllocatePages(owner, length);
                this.length = length;
                committed = MeasureCommitted(pointer, length);
            }
        }

        public override void Grow(ulong nextLength)
        {
            lock (owner.gate)
            {
                ObjectDisposedException.ThrowIf(pointer == 0, this);
                if (nextLength <= length) return;
                // The standalone policy includes replacement overlap in its cap.
                var next = AllocatePages(owner, nextLength);
                try
                {
                    ulong offset = 0;
                    while (offset < length)
                    {
                        var count = checked((int)Math.Min(length - offset, (ulong)int.MaxValue));
                        new ReadOnlySpan<byte>((byte*)pointer + checked((nuint)offset), count)
                            .CopyTo(new Span<byte>((byte*)next + checked((nuint)offset), count));
                        offset += checked((uint)count);
                    }
                    FreePages(pointer);
                }
                catch
                {
                    FreePages(next);
                    owner.currentBytes -= checked((nuint)nextLength);
                    throw;
                }
                owner.currentBytes -= checked((nuint)length);
                pointer = next;
                length = nextLength;
                committed = MeasureCommitted(pointer, length);
            }
        }

        public override void Dispose()
        {
            lock (owner.gate)
            {
                if (pointer == 0) return;
                FreePages(pointer);
                pointer = 0;
                owner.currentBytes -= checked((nuint)length);
                committed = 0;
            }
        }

        private static nint AllocatePages(BoundedKernelEtwRegionProvider owner, ulong length)
        {
            var count = checked((nuint)length);
            if (count > owner.byteCap - owner.currentBytes)
                throw new OutOfMemoryException("Kernel ETW standalone allocation capacity exceeded.");
            var pointer = OperatingSystem.IsWindows()
                ? VirtualAlloc(0, count, 0x3000, 0x04)
                : (nint)NativeMemory.AlignedAlloc(count, checked((nuint)Environment.SystemPageSize));
            if (pointer == 0) throw new OutOfMemoryException("Kernel ETW page allocation failed.");
            if (!OperatingSystem.IsWindows()) NativeMemory.Clear((void*)pointer, count);
            owner.currentBytes += count;
            owner.peakBytes = nuint.Max(owner.peakBytes, owner.currentBytes);
            return pointer;
        }

        private static ulong MeasureCommitted(nint pointer, ulong length)
            => NativeKernelEtwHostAllocator.MeasureCommittedBytes(pointer, length);

        private static void FreePages(nint pointer)
        {
            if (OperatingSystem.IsWindows())
            {
                if (!VirtualFree(pointer, 0, 0x8000)) throw new InvalidOperationException("Kernel ETW page release failed.");
            }
            else NativeMemory.AlignedFree((void*)pointer);
        }
    }

    [DllImport("kernel32", SetLastError = true)]
    private static extern nint VirtualAlloc(nint address, nuint size, uint allocationType, uint protection);
    [DllImport("kernel32", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool VirtualFree(nint address, nuint size, uint freeType);

    public IKernelEtwRegionLease Allocate(string owner, nuint minimumLength)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        if (minimumLength == 0) throw new ArgumentOutOfRangeException(nameof(minimumLength));
        lock (gate)
        {
            if (minimumLength > byteCap - currentBytes)
                throw new InvalidOperationException($"Kernel ETW region capacity exceeded for '{owner}'.");
            var memory = NativeMemory.AllocZeroed(minimumLength);
            if (memory == null) throw new OutOfMemoryException($"Kernel ETW region allocation failed for '{owner}'.");
            currentBytes += minimumLength;
            peakBytes = nuint.Max(peakBytes, currentBytes);
            return new Lease(this, (nint)memory, minimumLength);
        }
    }

    private sealed class Lease(BoundedKernelEtwRegionProvider owner, nint pointer, nuint length) : IKernelEtwRegionLease
    {
        private nint pointer = pointer;
        public nint Pointer => Volatile.Read(ref pointer);
        public nuint Length { get; } = length;
        public void Complete() => ObjectDisposedException.ThrowIf(Pointer == 0, this);
        public void Dispose()
        {
            lock (owner.gate)
            {
                var allocation = Interlocked.Exchange(ref pointer, 0);
                if (allocation == 0) return;
                NativeMemory.Free((void*)allocation);
                owner.currentBytes -= Length;
            }
        }
    }
}

public readonly record struct KernelEtwRegionAllocationUsage(ulong CurrentBytes, ulong PeakBytes, ulong ByteCap);
