using ResourceManager.App.Application.DiskUsage;
using ResourceManager.App.Domain.DiskUsage;
using ResourceManager.App.Infrastructure.DiskUsage;
using ResourceManager.ReadWrite.Windows.Files.DiskUsage;

namespace Resource_Manager_APP.Tests;

public sealed class DiskUsageTreeRegionTests
{
    [Fact]
    public void CancellationDuringAColumnWriteStopsTheUnpublishedLayout()
    {
        var source = Result().Tree; var layout = DiskUsageTreeRegionLayout.Create(source);
        var region = new TestRegion(layout.Length);
        using var cancellation = new CancellationTokenSource();
        var chunks = 0;
        region.BeforeChunk = (_, _) => { if (++chunks == 5) cancellation.Cancel(); };
        Assert.Throws<OperationCanceledException>(() => layout.Write(source, region, cancellation.Token));
        Assert.Equal(5, chunks);
        Assert.Throws<OperationCanceledException>(() => DiskUsageTreeRegionLayout.Create(source, cancellation.Token));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CancellationAfterImportOrPublicationUseRetiresTheNewPayloadAndKeepsCurrent(bool cancelDuringUse)
    {
        var allocator = new Allocator(); var store = new DiskUsageTreeStore(allocator);
        var first = Result(); var owner = new DiskUsageResultOwner("sid", 2); store.Replace(first, owner);
        var old = allocator.Payloads[0];
        using var cancellation = new CancellationTokenSource();
        allocator.BeforeImport = () => Assert.True(old.Readers > 0);
        allocator.AfterImport = payload =>
        {
            if (cancelDuringUse) payload.OnAcquire = cancellation.Cancel;
            else cancellation.Cancel();
        };
        Assert.Throws<OperationCanceledException>(() => store.Replace(Result(42), owner, cancellation.Token));
        Assert.Same(first.Summary, store.SummaryFor(owner));
        Assert.False(old.Retired); Assert.Equal(0, old.Readers);
        Assert.True(allocator.Payloads[1].ReleasedCompletely);
        using var reading = store.TryAcquire(owner);
        Assert.NotNull(reading); Assert.Equal(13, reading.Result.Tree.SizeOf(1));
    }

    [Fact]
    public void LargeColumnsAreWrittenInBoundedChunksAndKeepTheLastValues()
    {
        var builder = new DiskUsageTreeBuilder();
        var root = builder.Add(-1, "C:\\", true, 0, 0);
        for (var node = 0; node < 20_000; node++) builder.Add(root, $"entry-{node}", false, node, node + 1);
        var source = builder.Build(); var layout = DiskUsageTreeRegionLayout.Create(source);
        var region = new TestRegion(layout.Length);
        layout.Write(source, region);
        Assert.True(region.Length > 65_536);
        Assert.InRange(region.MaximumChunk, 1, 65_536);
        var tree = layout.Open(region);
        Assert.Equal(19_999, tree.SizeOf(20_000));
        Assert.Equal(20_000, tree.AllocatedOf(20_000));
        Assert.Equal("entry-19999", tree.NameOf(20_000).ToString());
        Assert.Equal(source.SizeOf(0), tree.SizeOf(0));
    }

    [Fact]
    public void PublishedTreeReadsEveryColumnAndNamesFromTheRegion()
    {
        var source = Result().Tree;
        var layout = DiskUsageTreeRegionLayout.Create(source);
        var region = new TestRegion(layout.Length);
        layout.Write(source, region);
        var tree = layout.Open(region);
        Assert.Equal(0UL, layout.Length % 8);
        Assert.True(layout.Length < (ulong)source.ApproximateByteSize);
        Assert.Equal(source.Roots, tree.Roots);
        for (var node = 0; node < source.Count; node++)
        {
            Assert.Equal(source.ParentOf(node), tree.ParentOf(node));
            Assert.Equal(source.FirstChildOf(node), tree.FirstChildOf(node));
            Assert.Equal(source.NextSiblingOf(node), tree.NextSiblingOf(node));
            Assert.Equal(source.SizeOf(node), tree.SizeOf(node));
            Assert.Equal(source.AllocatedOf(node), tree.AllocatedOf(node));
            Assert.Equal(source.FileCountOf(node), tree.FileCountOf(node));
            Assert.Equal(source.IdentityOf(node), tree.IdentityOf(node));
            Assert.Equal(source.LogicalSizeKnownOf(node), tree.LogicalSizeKnownOf(node));
            Assert.Equal(source.AllocationKnownOf(node), tree.AllocationKnownOf(node));
            Assert.Equal(source.CountKnownOf(node), tree.CountKnownOf(node));
            Assert.Equal(source.IsDirectory(node), tree.IsDirectory(node));
            Assert.Equal(source.NameOf(node).ToString(), tree.NameOf(node).ToString());
            Assert.Equal(source.PathOf(node), tree.PathOf(node));
            Assert.Equal(source.ChildrenBySizeDescending(node), tree.ChildrenBySizeDescending(node));
        }
        // Changing only the region's UTF-16 payload changes the real reader.
        var name = tree.NameOf(1).ToString();
        var bytes = System.Text.Encoding.Unicode.GetBytes(name);
        var offset = region.Bytes.AsSpan().IndexOf(bytes);
        Assert.True(offset >= 0);
        region.Bytes[offset] = (byte)'Q'; region.Bytes[offset + 1] = 0;
        Assert.StartsWith("Q", tree.NameOf(1).ToString());
        Assert.Equal(name, source.NameOf(1).ToString());
        Assert.Throws<ArgumentOutOfRangeException>(() => tree.SizeOf(-1));
        region.Bytes[0] = 99;
        Assert.Throws<InvalidDataException>(() => layout.Open(region));
        Assert.Throws<ArgumentException>(() => layout.Open(new TestRegion(layout.Length - 1)));
    }

    [Fact]
    public void FailedImportAndFailedPublicationKeepThePreviousPayloadProtected()
    {
        var allocator = new Allocator(); var store = new DiskUsageTreeStore(allocator);
        var first = Result(); store.Replace(first);
        var old = allocator.Payloads.Single();
        allocator.BeforeImport = () => Assert.True(old.Readers > 0);
        allocator.FailImport = true;
        Assert.Throws<IOException>(() => store.Replace(Result(21)));
        Assert.Same(first.Summary, store.CurrentSummary);
        Assert.Equal(0, old.Readers); Assert.False(old.Retired);
        allocator.FailImport = false; allocator.FailPublication = true;
        Assert.Throws<InvalidOperationException>(() => store.Replace(Result(22)));
        Assert.Same(first.Summary, store.CurrentSummary);
        Assert.True(allocator.Payloads[1].ReleasedCompletely);
        using var read = store.TryAcquire(); Assert.NotNull(read);
        Assert.Equal(first.Tree.SizeOf(0), read.Result.Tree.SizeOf(0));
    }

    [Fact]
    public void ReplacementAndClearRetireSafelyAndAnOldReaderContinuesUntilReturn()
    {
        var allocator = new Allocator(); var store = new DiskUsageTreeStore(allocator);
        store.Replace(Result());
        var old = allocator.Payloads.Single();
        var staleNotification = old.CaptureNotification();
        var oldRead = store.TryAcquire()!;
        store.Replace(Result(27));
        Assert.True(old.Retired); Assert.False(old.ReleasedCompletely);
        Assert.Equal(13, oldRead.Result.Tree.SizeOf(1));
        staleNotification();
        Assert.NotNull(store.CurrentSummary);
        var next = allocator.Payloads[1];
        var currentRead = store.TryAcquire()!;
        store.Clear();
        Assert.Null(store.CurrentSummary); Assert.Null(store.TryAcquire());
        Assert.False(next.ReleasedCompletely);
        Assert.Equal(27, currentRead.Result.Tree.SizeOf(1));
        currentRead.Dispose(); Assert.True(next.ReleasedCompletely);
        oldRead.Dispose(); Assert.True(old.ReleasedCompletely);
        Assert.Throws<ObjectDisposedException>(() => oldRead.Result);
    }

    [Fact]
    public void AcquireRechecksTheCurrentOwnerAfterTheAllocatorUse()
    {
        var allocator = new Allocator(); var store = new DiskUsageTreeStore(allocator);
        var owner = new DiskUsageResultOwner("sid-first", 1);
        var other = new DiskUsageResultOwner("sid-other", 2);
        store.Replace(Result(), owner);
        var first = allocator.Payloads[0];
        first.OnAcquire = () => store.Replace(Result(29), other);
        Assert.Null(store.TryAcquire(owner));
        Assert.Equal(0, first.Readers); Assert.True(first.ReleasedCompletely);
        using var read = store.TryAcquire(other);
        Assert.NotNull(read); Assert.Equal(29, read.Result.Tree.SizeOf(1));
    }

    [Fact]
    public void ActualReleaseRetiresMatchingMetadataAndSelectedInformation()
    {
        var allocator = new Allocator(); var store = new DiskUsageTreeStore(allocator);
        store.Replace(Result());
        using var view = store.CreateInformationView([1]);
        Assert.Single(view.ReadInformation().Devices);
        allocator.Payloads[0].ReleaseUnleased();
        Assert.Null(store.CurrentSummary); Assert.Empty(store.ReadInformation().Devices);
        Assert.Empty(view.ReadInformation().Devices); Assert.Null(store.TryAcquire());
    }

    [Fact]
    public async Task ScanImportFailureKeepsCurrentWhileScannerFailureClearsValues()
    {
        var allocator = new Allocator(); var store = new DiskUsageTreeStore(allocator);
        var first = Result(); store.Replace(first);
        var scanner = new Scanner();
        var resource = new WindowsMftDiskUsageResource(scanner,
            new(DiskUsageScanScopes.Volume, DiskUsageScanModes.Fast, "C:"), store);
        allocator.FailImport = true;
        await Assert.ThrowsAsync<IOException>(() => resource.ScanAsync());
        Assert.Same(first.Summary, store.CurrentSummary);
        Assert.NotEmpty(store.ReadInformation().Devices);
        scanner.Fail = true;
        await Assert.ThrowsAsync<IOException>(() => resource.ScanAsync());
        Assert.Null(store.CurrentSummary);
        Assert.All(store.ReadInformation().Devices.SelectMany(device => device.Fields), field => Assert.Null(field.Value));
    }

    private static DiskUsageScanResult Result(long size = 13)
    {
        var builder = new DiskUsageTreeBuilder();
        var root = builder.Add(-1, "C:\\", true, 0, 0, new(ulong.MaxValue, long.MaxValue, ushort.MaxValue));
        builder.Add(root, "中文-🙂.bin", false, size, 0, new(17, 19, 23), allocationKnown: false);
        var directory = builder.Add(root, "empty", true, 0, 0);
        builder.SetIncomplete(directory);
        builder.Add(-1, "D:\\", true, 0, 0, new(11, 31, 5));
        return new(builder.Build(), new(DiskUsageScanScopes.AllVolumes, DiskUsageScanModes.Fast, "", ["C:\\", "D:\\"],
            DiskUsageScanKinds.MasterFileTable, [], DateTimeOffset.UtcNow, 0, 0, 0, 1, 3, 0));
    }
    private sealed class TestRegion(ulong length) : IDiskUsageTreeRegion
    {
        internal readonly byte[] Bytes = new byte[checked((int)length)];
        internal int MaximumChunk;
        internal Action<ulong, int>? BeforeChunk;
        public ulong Length => (ulong)Bytes.Length;
        public Span<byte> GetSpanChunk(ulong offset, int count)
        { BeforeChunk?.Invoke(offset, count); MaximumChunk = Math.Max(MaximumChunk, count); return Bytes.AsSpan(checked((int)offset), count); }
    }
    private sealed class Allocator : IDiskUsageTreePayloadAllocator
    {
        internal readonly List<Payload> Payloads = [];
        internal bool FailImport, FailPublication;
        internal Action? BeforeImport;
        internal Action<Payload>? AfterImport;
        public IDiskUsageTreePayload Import(DiskUsageTree source, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            BeforeImport?.Invoke();
            if (FailImport) throw new IOException("Allocation failed.");
            var payload = new Payload(source, cancellationToken) { FailAcquire = FailPublication };
            Payloads.Add(payload); AfterImport?.Invoke(payload); return payload;
        }
    }
    private sealed class Payload : IDiskUsageTreePayload
    {
        private readonly DiskUsageTreeRegionLayout layout;
        private readonly TestRegion region;
        internal int Readers;
        internal bool Retired, ReleasedCompletely, FailAcquire;
        internal Action? OnAcquire;
        public event Action? Released;
        internal Payload(DiskUsageTree source, CancellationToken cancellationToken = default)
        { layout = DiskUsageTreeRegionLayout.Create(source, cancellationToken); region = new(layout.Length); layout.Write(source, region, cancellationToken); }
        public IDiskUsageTreePayloadReadLease? TryAcquire()
        {
            if (Retired || ReleasedCompletely || FailAcquire) return null;
            Readers++;
            var action = OnAcquire; OnAcquire = null; action?.Invoke();
            return new ReadLease(this, layout.Open(region));
        }
        public void Retire() { Retired = true; if (Readers == 0) ReleaseUnleased(); }
        internal Action CaptureNotification() => Released ?? (() => { });
        internal void ReleaseUnleased()
        {
            Assert.Equal(0, Readers);
            if (ReleasedCompletely) return;
            ReleasedCompletely = true; Released?.Invoke();
        }
        private sealed class ReadLease(Payload owner, DiskUsageTree tree) : IDiskUsageTreePayloadReadLease
        {
            private bool disposed;
            public DiskUsageTree Tree => !disposed ? tree : throw new ObjectDisposedException(nameof(ReadLease));
            public void Dispose()
            { if (disposed) return; disposed = true; owner.Readers--; if (owner.Retired && owner.Readers == 0) owner.ReleaseUnleased(); }
        }
    }
    private sealed class Scanner : IDiskUsageScanner
    {
        internal bool Fail;
        public Task<DiskUsageScanResult> ScanAsync(DiskUsageScanRequest request,
            IProgress<DiskUsageScanProgress>? progress, CancellationToken cancellationToken)
            => Fail ? Task.FromException<DiskUsageScanResult>(new IOException("Scan failed.")) : Task.FromResult(Result(31));
    }
}
