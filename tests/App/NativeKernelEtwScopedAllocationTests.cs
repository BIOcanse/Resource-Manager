using System.Runtime.InteropServices;
using ResourceManager.App.Infrastructure.Adaptation;
using ResourceManager.App.Infrastructure.Telemetry.Etw;
using ResourceManager.Ledger;

namespace Resource_Manager_APP.Tests;

public sealed unsafe class NativeKernelEtwScopedAllocationTests
{
    [Fact]
    public void CallbackAbiAndEmptyScopeDoNotCommitTheCompiledCap()
    {
        using var ledger = new ResourceManagerLedgerOwner();
        using var allocator = new NativeKernelEtwLedgerRegionProvider(ledger).CreateAllocator("test", 64 * 1024 * 1024);
        var abi = allocator.Callbacks;
        Assert.Equal(48, sizeof(NativeKernelEtwHostAllocatorCallbacks));
        Assert.Equal(48u, abi.Size);
        Assert.Equal(1u, abi.Version);
        Assert.Equal((nuint)ledger.NativePageSize, abi.PageSize);
        Assert.Equal(0UL, allocator.GetUsage().CommittedBytes);
        Assert.Equal(0, allocator.GetUsage().Allocations);
        Assert.Equal(nint.Zero, abi.Allocate(abi.Context, 9, 128, 3)); // Unknown owner, caught at the ABI.
        Assert.IsType<ArgumentOutOfRangeException>(allocator.LastFailure);
        Assert.Equal(0UL, allocator.GetUsage().CommittedBytes);
    }

    [Fact]
    public void LedgerGrowthPreservesBytesAndIdentityAndRejectsAnotherLease()
    {
        using var ledger = new ResourceManagerLedgerOwner();
        using var allocator = (NativeKernelEtwLedgerRegionProvider.Allocator)
            new NativeKernelEtwLedgerRegionProvider(ledger).CreateAllocator("test", 8 * ledger.NativePageSize);
        var page = ledger.NativePageSize;
        const uint tag = 1 | (1u << 8); // CPU window, independently returned.
        const ulong originalLength = 100;
        var pointer = allocator.Allocate(tag, originalLength, 3);
        var currentLength = originalLength;
        try
        {
            Assert.NotEqual(nint.Zero, pointer);
            Marshal.WriteByte(pointer, 0, 7);
            Marshal.WriteByte(pointer, 99, 17);
            ulong allocationNumber;
            using (var other = allocator.AcquireReference(pointer))
            {
                allocationNumber = other.AllocationNumber;
                Assert.False(other.IsLoading);
                Assert.Equal(nint.Zero, allocator.Remap(tag, pointer, currentLength, page + 1, 3));
                Assert.Equal(page, other.Length);
                Assert.Equal(page, allocator.GetUsage().CommittedBytes);
                Assert.Equal(originalLength, allocator.GetUsage().RequestedBytes);
                Assert.Equal(LedgerStatus.InUse, Assert.IsType<LedgerException>(allocator.LastFailure).Status);
                Assert.Equal((byte)7, Marshal.ReadByte(pointer, 0));
            }
            var next = allocator.Remap(tag, pointer, currentLength, page + 1, 3);
            Assert.NotEqual(nint.Zero, next);
            pointer = next;
            currentLength = page + 1;
            Assert.Equal((byte)7, Marshal.ReadByte(pointer, 0));
            Assert.Equal((byte)17, Marshal.ReadByte(pointer, 99));
            Assert.Equal((byte)0, Marshal.ReadByte(pointer, 100));
            Assert.Equal((byte)0, Marshal.ReadByte(pointer, checked((int)page)));
            using (var sameIdentity = allocator.AcquireReference(pointer))
            {
                Assert.Equal(allocationNumber, sameIdentity.AllocationNumber);
                Assert.Equal(2 * page, sameIdentity.Length);
            }
            var usage = allocator.GetUsage();
            Assert.Equal(2 * page, usage.CommittedBytes);
            Assert.Equal("VirtualQuery", usage.CommitMeasurement);
            Assert.Equal(usage.CommittedBytes, ledger.ReadTable().SystemPageBytes);
            var row = Assert.Single(usage.Tags);
            Assert.Equal(1u, row.Owner);
            Assert.Equal(1u, row.Scope);
        }
        finally
        {
            if (pointer != 0) Assert.Equal(0u, allocator.Release(tag, pointer, currentLength, 3));
            allocator.Dispose();
        }
        Assert.Equal(0UL, ledger.ReadTable().RegisteredBytes);
    }

    [Fact]
    public void OwnerAndScopeCommitAreDemandSizedAndHardCapFailureIsUnchanged()
    {
        using var ledger = new ResourceManagerLedgerOwner();
        var page = ledger.NativePageSize;
        using var allocator = new NativeKernelEtwLedgerRegionProvider(ledger).CreateAllocator("test", 2 * page);
        var first = allocator.Allocate(2, 1, 3);
        var second = allocator.Allocate(4, 1, 3);
        try
        {
            Assert.NotEqual(nint.Zero, first);
            Assert.NotEqual(nint.Zero, second);
            Marshal.WriteByte(first, 0, 23);
            Assert.Equal(nint.Zero, allocator.Allocate(1 | (1u << 8), 1, 3));
            Assert.IsType<OutOfMemoryException>(allocator.LastFailure);
            Assert.Equal(new KernelEtwAllocationFailure(1 | (1u << 8), 1, 2, 0, 2 * page),
                allocator.GetUsage().AllocationFailure);
            Assert.Equal(nint.Zero, allocator.Remap(2, first, 1, page + 1, 3));
            Assert.Equal((byte)23, Marshal.ReadByte(first, 0));
            var usage = allocator.GetUsage();
            Assert.Equal(2UL, usage.RequestedBytes);
            Assert.Equal(2 * page, usage.CommittedBytes);
            Assert.Equal(2, usage.Allocations);
            Assert.Equal(new uint[] { 2, 4 }, usage.Tags.Select(row => row.Owner));
        }
        finally
        {
            if (first != 0) Assert.Equal(0u, allocator.Release(2, first, 1, 3));
            if (second != 0) Assert.Equal(0u, allocator.Release(4, second, 1, 3));
            allocator.Dispose();
        }
        Assert.Equal(0UL, allocator.GetUsage().CommittedBytes);
    }

    [Fact]
    public void RelocationOverlapIsAdmittedAgainstTheOwnerCapAndReportedSeparately()
    {
        using var ledger = new ResourceManagerLedgerOwner();
        var page = ledger.NativePageSize;
        using var allocator = new NativeKernelEtwLedgerRegionProvider(ledger).CreateAllocator("test", 8 * page);
        allocator.ConfigureOwnerByteCaps(new[] { 8 * page, 3 * page, 8 * page, 8 * page, 8 * page, 8 * page, 8 * page, 8 * page, 8 * page });
        var pointer = allocator.Allocate(1 | (1u << 8), 100, 3);
        ulong length = 100;
        try
        {
            Assert.NotEqual(nint.Zero, pointer);
            var next = allocator.Remap(1 | (1u << 8), pointer, length, page + 1, 3);
            Assert.NotEqual(nint.Zero, next);
            var oldPointer = pointer;
            pointer = next;
            length = page + 1;
            Assert.NotEqual(oldPointer, next); // SDK Windows resize is allocate-copy-release.
            var usage = allocator.GetUsage();
            Assert.Equal(2 * page, usage.PeakCommittedBytes); // Actual post-callback observation.
            Assert.Equal(3 * page, usage.DerivedPeakCommittedBytes); // Proven replacement overlap.
            var cpu = Assert.Single(usage.Owners.Where(row => row.Owner == 1));
            Assert.Equal(3 * page, cpu.ByteCap);
            Assert.Equal(3 * page, cpu.DerivedPeakCommittedBytes);
            // Global8-page room exists, but the CPU's 2+3 overlap does not fit3.
            Assert.Equal(nint.Zero, allocator.Remap(1 | (1u << 8), pointer, length, 2 * page + 1, 3));
            Assert.IsType<OutOfMemoryException>(allocator.LastFailure);
            Assert.Equal(2 * page, allocator.GetUsage().CommittedBytes);
        }
        finally
        {
            if (pointer != 0) Assert.Equal(0u, allocator.Release(1 | (1u << 8), pointer, length, 3));
            allocator.Dispose();
        }
    }

    [Fact]
    public void LogicalRootHandoffKeepsFailedRetirementAndCallbacksAliveForScopeRetry()
    {
        using var ledger = new ResourceManagerLedgerOwner();
        var page = ledger.NativePageSize;
        using var allocator = (NativeKernelEtwLedgerRegionProvider.Allocator)
            new NativeKernelEtwLedgerRegionProvider(ledger).CreateAllocator("test", 4 * page);
        var pointer = allocator.Allocate(0, 100, 3); // Shared native owner root.
        ComputeScratchLease? other = null;
        var returned = false;
        try
        {
            Assert.NotEqual(nint.Zero, pointer);
            Assert.Throws<InvalidOperationException>(() => allocator.Dispose()); // Native has not returned it.
            other = allocator.AcquireReference(pointer);
            // A root release failure transfers the retirement obligation to the
            // managed scope. Logical native destroy returns without reading or
            // retrying the native pointer after this callback, just like rawFree.
            Assert.Equal(1u, allocator.Release(0, pointer, 100, 3));
            returned = true;
            Assert.Equal(0, allocator.GetUsage().Allocations);
            Assert.Equal(1, allocator.GetUsage().PendingReleases);
            Assert.Equal(page, allocator.GetUsage().CommittedBytes);
            Assert.Throws<InvalidOperationException>(() => allocator.Dispose());
            Assert.NotEqual(nint.Zero, allocator.Callbacks.Context);
            other.Dispose();
            other = null;
            Assert.Equal(0UL, allocator.GetUsage().CommittedBytes);
            Assert.Equal(1, allocator.GetUsage().PendingReleases); // Obligation still needs its retry.
        }
        finally
        {
            other?.Dispose();
            if (!returned && pointer != 0) _ = allocator.Release(0, pointer, 100, 3);
            allocator.Dispose();
        }
        allocator.Dispose();
        Assert.Equal(0UL, allocator.GetUsage().CommittedBytes);
        Assert.Equal(0UL, ledger.ReadTable().RegisteredBytes);
        Assert.Throws<ObjectDisposedException>(() => { _ = allocator.Callbacks; });
        allocator.Dispose();
    }
}
