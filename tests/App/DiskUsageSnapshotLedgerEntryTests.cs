using ResourceManager.App.Application.DiskUsage;
using ResourceManager.App.Domain.DiskUsage;
using ResourceManager.App.Infrastructure.Adaptation;
using ResourceManager.App.Infrastructure.DiskUsage;
using ResourceManager.Ledger;

namespace Resource_Manager_APP.Tests;

/// <summary>Actual product Native instance and SDK-owned published payloads.</summary>
public sealed class DiskUsageSnapshotLedgerEntryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TinyPublishedTreesUseCompactOrdinaryMemoryAndOutliveTheLedger(bool empty)
    {
        using var owner = new ResourceManagerLedgerOwner();
        var store = new DiskUsageTreeStore(owner);
        var source = TinyResult();
        if (empty) source = source with { Tree = new DiskUsageTreeBuilder().Build() };
        var length = DiskUsageTreeRegionLayout.Create(source.Tree).Length;
        Assert.True(source.Tree.ApproximateByteSize > 4096);
        Assert.True(length < 4096);
        store.Replace(source);
        var reading = store.TryAcquire();
        Assert.NotNull(reading);
        Assert.NotSame(source.Tree, reading.Result.Tree);
        Assert.Equal(checked((long)length), reading.Result.Tree.ApproximateByteSize);
        Assert.Equal(0UL, owner.ReadTable().RegisteredBytes);
        owner.SetMode(LedgerTableMode.Optimize);
        Assert.Equal(0UL, owner.Tick().Released);
        Assert.Same(source.Summary, store.CurrentSummary);
        store.Clear();
        Assert.Null(store.TryAcquire());
        Assert.Equal(LedgerStatus.Ok, owner.TryClose());
        Assert.Equal(source.Tree.Count, reading.Result.Tree.Count);
        if (!empty) Assert.Equal(7, reading.Result.Tree.SizeOf(1));
        reading.Dispose();
    }

    [Theory]
    [InlineData(27, 4088UL, false)]
    [InlineData(31, 4096UL, true)]
    [InlineData(35, 4104UL, true)]
    public void ExactPayloadPageBoundarySelectsOrdinaryOrLedgerStorage(int nameLength, ulong length, bool registered)
    {
        using var owner = new ResourceManagerLedgerOwner();
        owner.SetMode(LedgerTableMode.Unrestricted);
        var builder = new DiskUsageTreeBuilder();
        var root = builder.Add(-1, "C:\\", true, 0, 0);
        builder.Add(root, new string('f', nameLength), false, 7, 7);
        for (var node = 0; node < 58; node++) builder.Add(root, "file", false, 0, 0);
        var source = TinyResult() with { Tree = builder.Build() };
        Assert.Equal(length, DiskUsageTreeRegionLayout.Create(source.Tree).Length);
        var store = new DiskUsageTreeStore(owner);
        store.Replace(source);
        Assert.Equal(registered ? length : 0UL, owner.ReadTable().RegisteredBytes);
        using (var reading = store.TryAcquire())
        {
            Assert.NotNull(reading);
            Assert.Equal(7, reading.Result.Tree.SizeOf(1));
        }
        store.Clear();
        Assert.Equal(0UL, owner.ReadTable().RegisteredBytes);
    }

    [Fact]
    public void TinyReplacementKeepsOldReadersWithoutConsumingLedgerSlots()
    {
        using var owner = new ResourceManagerLedgerOwner();
        var store = new DiskUsageTreeStore(owner);
        store.Replace(TinyResult());
        using var first = store.TryAcquire();
        Assert.NotNull(first);
        // More replacements than the real Native table capacity of eight.
        for (var index = 0; index < 12; index++) store.Replace(TinyResult());
        Assert.Equal(0UL, owner.ReadTable().RegisteredBytes);
        store.Clear();
        Assert.Equal(7, first.Result.Tree.SizeOf(1));
    }

    [Fact]
    public void TinyImportHonorsCancellationAndCannotBypassDisposedOwner()
    {
        using var owner = new ResourceManagerLedgerOwner();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => owner.Import(TinyResult().Tree, cancellation.Token));
        Assert.Equal(0UL, owner.ReadTable().RegisteredBytes);
        owner.Dispose();
        Assert.Throws<ObjectDisposedException>(() => owner.Import(TinyResult().Tree));
    }

    [Fact]
    public void FailedTinyCopyKeepsCurrentWithoutRegisteringEitherTree()
    {
        using var owner = new ResourceManagerLedgerOwner();
        var store = new DiskUsageTreeStore(owner);
        var current = TinyResult(); store.Replace(current);
        var layout = DiskUsageTreeRegionLayout.Create(current.Tree);
        var region = new FaultingSourceRegion(layout.Length);
        layout.Write(current.Tree, region);
        var view = layout.Open(region);
        region.RemainingReads = view.Count * 3;
        Assert.Throws<IOException>(() => store.Replace(current with { Tree = view }));
        Assert.Same(current.Summary, store.CurrentSummary);
        Assert.Equal(0UL, owner.ReadTable().RegisteredBytes);
        using var reading = store.TryAcquire();
        Assert.NotNull(reading);
        Assert.Equal(7, reading.Result.Tree.SizeOf(1));
        store.Clear();
    }

    [Fact]
    public void FailedLayoutCopyDiscardsTheRealUnpublishedAllocationAndKeepsCurrent()
    {
        using var owner = new ResourceManagerLedgerOwner();
        owner.SetMode(LedgerTableMode.Unrestricted);
        var store = new DiskUsageTreeStore(owner);
        var current = Result("current.bin", 7); store.Replace(current);
        var registered = owner.ReadTable().RegisteredBytes;
        var source = Result("copy-fails.bin", 19);
        var layout = DiskUsageTreeRegionLayout.Create(source.Tree);
        var region = new FaultingSourceRegion(layout.Length);
        layout.Write(source.Tree, region);
        var sourceView = layout.Open(region);
        // Layout sizing reads the two name coordinates and the UTF-16 span
        // per node. The first column copy then fails after Push has allocated.
        region.RemainingReads = sourceView.Count * 3;
        Assert.Throws<IOException>(() => store.Replace(source with { Tree = sourceView }));
        Assert.Same(current.Summary, store.CurrentSummary);
        Assert.Equal(registered, owner.ReadTable().RegisteredBytes);
        using (var reading = store.TryAcquire())
        {
            Assert.NotNull(reading); Assert.Equal(7, reading.Result.Tree.SizeOf(1));
        }
        store.Replace(Result("after-failure.bin", 23));
        store.Clear();
        Assert.Equal(0UL, owner.ReadTable().RegisteredBytes);
    }

    [Fact]
    public void PublishedTreeReadsTheRealAllocationAndClearWaitsForItsReader()
    {
        using var owner = new ResourceManagerLedgerOwner();
        owner.SetMode(LedgerTableMode.Unrestricted);
        var store = new DiskUsageTreeStore(owner);
        var source = Result("实际-文件😀.bin", 4096);
        var length = DiskUsageTreeRegionLayout.Create(source.Tree).Length;
        Assert.Equal(0UL, owner.ReadTable().RegisteredBytes);
        store.Replace(source);
        Assert.Equal(length, owner.ReadTable().RegisteredBytes);
        var reading = store.TryAcquire();
        Assert.NotNull(reading);
        var tree = reading.Result.Tree;
        Assert.NotSame(source.Tree, tree);
        Assert.Equal("实际-文件😀.bin", tree.NameOf(1).ToString());
        Assert.Equal(new DiskUsageObjectIdentity(123, 41, 7), tree.IdentityOf(0));
        Assert.Null(tree.IdentityOf(1));
        Assert.Equal(4096, tree.SizeOf(1));
        store.Clear();
        Assert.Null(store.CurrentSummary);
        Assert.Null(store.TryAcquire());
        Assert.Equal(length, owner.ReadTable().RegisteredBytes);
        Assert.Equal(4096, tree.SizeOf(1));
        reading.Dispose();
        Assert.Equal(0UL, owner.ReadTable().RegisteredBytes);
        Assert.Throws<ObjectDisposedException>(() => tree.SizeOf(1));
    }

    [Fact]
    public void ReplacementRetainsOldReaderAndItsReleaseCannotClearTheNewResult()
    {
        using var owner = new ResourceManagerLedgerOwner();
        owner.SetMode(LedgerTableMode.Unrestricted);
        var store = new DiskUsageTreeStore(owner);
        var old = Result("old.bin", 17);
        var next = Result("next.bin", 31);
        store.Replace(old);
        var oldReading = store.TryAcquire();
        Assert.NotNull(oldReading);
        store.Replace(next);
        using (var nextReading = store.TryAcquire())
        {
            Assert.NotNull(nextReading);
            Assert.Equal(17, oldReading.Result.Tree.SizeOf(1));
            Assert.Equal(31, nextReading.Result.Tree.SizeOf(1));
            Assert.Equal(DiskUsageTreeRegionLayout.Create(old.Tree).Length +
                DiskUsageTreeRegionLayout.Create(next.Tree).Length, owner.ReadTable().RegisteredBytes);
        }
        oldReading.Dispose();
        Assert.Same(next.Summary, store.CurrentSummary);
        Assert.Equal(DiskUsageTreeRegionLayout.Create(next.Tree).Length, owner.ReadTable().RegisteredBytes);
        store.Clear();
        Assert.Equal(0UL, owner.ReadTable().RegisteredBytes);
    }

    [Fact]
    public void FullTableImportFailureKeepsTheCurrentRealAllocation()
    {
        using var owner = new ResourceManagerLedgerOwner();
        owner.SetMode(LedgerTableMode.Unrestricted);
        var store = new DiskUsageTreeStore(owner);
        var readers = new List<DiskUsageTreeReadLease>();
        try
        {
            for (var index = 0; index < 8; index++)
            {
                store.Replace(Result($"held-{index}.bin", index + 1));
                readers.Add(store.TryAcquire()!);
            }
            var summary = store.CurrentSummary;
            var bytes = owner.ReadTable().RegisteredBytes;
            Assert.Throws<LedgerException>(() => store.Replace(Result("cannot-fit.bin", 99)));
            Assert.Same(summary, store.CurrentSummary);
            Assert.Equal(bytes, owner.ReadTable().RegisteredBytes);
            using var current = store.TryAcquire();
            Assert.NotNull(current);
            Assert.Equal(8, current.Result.Tree.SizeOf(1));
            for (var index = 0; index < readers.Count; index++)
                Assert.Equal(index + 1, readers[index].Result.Tree.SizeOf(1));
        }
        finally
        {
            store.Clear();
            foreach (var reading in readers) reading.Dispose();
        }
        Assert.Equal(0UL, owner.ReadTable().RegisteredBytes);
    }

    [Fact]
    public void FullOwnerKeepsEightTreeReadersAndTwentyEightScratchUsesWithoutOverlap()
    {
        using var owner = new ResourceManagerLedgerOwner();
        owner.SetMode(LedgerTableMode.Unrestricted);
        var store = new DiskUsageTreeStore(owner);
        var readers = new List<DiskUsageTreeReadLease>();
        var resources = new List<ResourceManagerLedgerOwner.ComputeScratch>();
        var uses = new List<ComputeScratchLease>();
        try
        {
            for (var index = 0; index < 8; index++)
            {
                store.Replace(Result($"full-owner-{index}.bin", index + 1));
                readers.Add(store.TryAcquire()!);
            }
            var treeBytes = owner.ReadTable().RegisteredBytes;
            for (var index = 0; index < 28; index++)
            {
                var resource = owner.CreateScratch(8192);
                resources.Add(resource);
                var use = resource.Acquire();
                uses.Add(use);
                use.GetMemoryChunk(0, 1).Span[0] = checked((byte)(index + 20));
                use.Complete();
            }
            Assert.Equal(treeBytes + 28UL * 8192, owner.ReadTable().RegisteredBytes);
            using var excess = owner.CreateScratch(8192);
            var blocked = Assert.Throws<LedgerException>(() => { using var unexpected = excess.Acquire(); });
            Assert.Equal(ResourceManager.Ledger.LedgerStatus.Blocked, blocked.Status);
            Assert.Throws<LedgerException>(() => store.Replace(Result("full-owner-rejected.bin", 99)));
            for (var index = 0; index < readers.Count; index++)
                Assert.Equal(index + 1, readers[index].Result.Tree.SizeOf(1));
            for (var index = 0; index < uses.Count; index++)
                Assert.Equal(index + 20, uses[index].GetMemoryChunk(0, 1).Span[0]);
            Assert.Equal(0UL, owner.Tick().Released);
        }
        finally
        {
            store.Clear();
            foreach (var reader in readers) reader.Dispose();
            foreach (var use in uses) use.Dispose();
            foreach (var resource in resources) resource.Dispose();
        }
        Assert.Equal(0UL, owner.ReadTable().RegisteredBytes);
    }

    [Fact]
    public void NormalCloseWithLiveReaderKeepsOwnerAndRegionValidUntilDrain()
    {
        using var owner = new ResourceManagerLedgerOwner();
        var store = new DiskUsageTreeStore(owner);
        store.Replace(Result("still-used.bin", 81));
        var reading = store.TryAcquire();
        Assert.NotNull(reading);
        store.Clear();
        Assert.Equal(LedgerStatus.InUse, owner.TryClose());
        var failedDispose = Assert.Throws<LedgerException>(() => owner.Dispose());
        Assert.Equal(LedgerStatus.InUse, failedDispose.Status);
        Assert.True(owner.ReadTable().RegisteredBytes > 0);
        Assert.Equal(81, reading.Result.Tree.SizeOf(1));
        reading.Dispose();
        Assert.Equal(LedgerStatus.Ok, owner.TryClose());
        Assert.Equal(LedgerStatus.Ok, owner.TryClose());
    }

    [Fact]
    public async Task ConcurrentReadersSeeCompleteAllocationsWhileResultsAreReplaced()
    {
        using var owner = new ResourceManagerLedgerOwner();
        owner.SetMode(LedgerTableMode.Unrestricted);
        var store = new DiskUsageTreeStore(owner);
        store.Replace(Result("first.bin", 1));
        using var start = new ManualResetEventSlim();
        Task Read() => Task.Run(() =>
        {
            start.Wait();
            for (var iteration = 0; iteration < 400; iteration++)
            {
                using var reading = store.TryAcquire();
                if (reading is null) continue; // A bounded acquire can lose a replacement race.
                var result = reading.Result;
                Assert.Equal(66, result.Tree.Count);
                Assert.Equal(checked((long)result.Summary.TotalBytes), result.Tree.SizeOf(1));
                Assert.Equal(-1, result.Tree.ParentOf(result.Tree.Roots[0]));
                Thread.Yield();
                Assert.Equal(checked((long)result.Summary.TotalBytes), result.Tree.SizeOf(1));
            }
        });
        var readers = new[] { Read(), Read() };
        var writer = Task.Run(() =>
        {
            start.Wait();
            for (var value = 2; value <= 40; value++) store.Replace(Result($"next-{value}.bin", value));
        });
        start.Set();
        await Task.WhenAll(readers.Append(writer)).WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(40UL, store.CurrentSummary!.TotalBytes);
        store.Clear();
        Assert.Equal(0UL, owner.ReadTable().RegisteredBytes);
    }

    [Fact]
    public async Task SdkTickRetiresCurrentOnlyAfterItsRealReaderReturns()
    {
        using var owner = new ResourceManagerLedgerOwner();
        var store = new DiskUsageTreeStore(owner);
        store.Replace(Result("tick-owned.bin", 73));
        using var changed = store.Publications.Subscribe([DiskUsageTreeStore.InformationItemId]);
        var reading = store.TryAcquire();
        Assert.NotNull(reading);
        owner.SetMode(LedgerTableMode.Optimize);
        Assert.Equal(0UL, owner.Tick().Released);
        Assert.NotNull(store.CurrentSummary);
        Assert.Equal(73, reading.Result.Tree.SizeOf(1));
        reading.Dispose();
        Assert.Equal(1UL, owner.Tick().Released);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await changed.WaitAsync(deadline.Token);
        Assert.Null(store.CurrentSummary);
        Assert.Null(store.TryAcquire());
        Assert.Empty(store.ReadInformation().Devices);
        Assert.Equal(0UL, owner.ReadTable().RegisteredBytes);
    }

    private static DiskUsageScanResult Result(string name, long size)
    {
        var builder = new DiskUsageTreeBuilder();
        var root = builder.Add(-1, "C:\\", true, 0, 0, new DiskUsageObjectIdentity(123, 41, 7));
        builder.Add(root, name, false, size, size);
        // These are Native lifecycle fixtures, so retain an actual cache-sized
        // payload rather than admitting a two-node tree through a test override.
        for (var index = 0; index < 64; index++)
            builder.Add(root, $"cache-entry-{index:00}", false, 0, 0);
        return new(builder.Build(), new DiskUsageScanSummary(DiskUsageScanScopes.Volume,
            DiskUsageScanModes.Fast, "C:", ["C:\\"], DiskUsageScanKinds.MasterFileTable, [],
            DateTimeOffset.UtcNow, 0.1, checked((ulong)size), checked((ulong)size), 65, 1, 0));
    }

    private static DiskUsageScanResult TinyResult()
    {
        var builder = new DiskUsageTreeBuilder();
        var root = builder.Add(-1, "C:\\", true, 0, 0);
        builder.Add(root, "tiny.bin", false, 7, 7);
        return new(builder.Build(), new DiskUsageScanSummary(DiskUsageScanScopes.Volume,
            DiskUsageScanModes.Fast, "C:", ["C:\\"], DiskUsageScanKinds.MasterFileTable, [],
            DateTimeOffset.UtcNow, 0.1, 7, 7, 1, 1, 0));
    }

    private sealed class FaultingSourceRegion(ulong length) : IDiskUsageTreeRegion
    {
        private readonly byte[] bytes = new byte[checked((int)length)];
        internal int? RemainingReads;
        public ulong Length => (ulong)bytes.Length;
        public Span<byte> GetSpanChunk(ulong offset, int count)
        {
            if (RemainingReads == 0) throw new IOException("The source became unreadable during the real loading copy.");
            if (RemainingReads.HasValue) RemainingReads--;
            return bytes.AsSpan(checked((int)offset), count);
        }
    }
}
