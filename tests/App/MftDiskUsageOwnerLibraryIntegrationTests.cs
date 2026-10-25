using System.Buffers.Binary;
using System.Text;
using ResourceManager.App.Application.DiskUsage;
using ResourceManager.App.Domain.DiskUsage;
using ResourceManager.App.Infrastructure.DiskUsage;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.Dependencies;
using ResourceManager.ReadWrite.Information;
using ResourceManager.ReadWrite.Windows.Files.DiskUsage;
using SdkScanner = ResourceManager.ReadWrite.Windows.Files.DiskUsage.WindowsMftDiskUsageScanner;
using Xunit;

namespace ResourceManager.App.Tests;

public sealed class MftDiskUsageOwnerLibraryIntegrationTests
{
    [Theory]
    [InlineData(3, 1_000_000)]
    [InlineData(100, 1_200)]
    public async Task RetainedInputLimitsIncludeExtensionNamesAndMarkPartialCoverage(int countLimit, long byteLimit)
    {
        var scanner = new SdkScanner(new Catalog(), openReader: _ => new Reader(Image()),
            retainedInputBudgetBytes: byteLimit, maximumRetainedRecords: countLimit);
        var result = await scanner.ScanAsync(new(DiskUsageScanScopes.Volume, DiskUsageScanModes.Fast, "C:"), null, default);
        Assert.False(result.Summary.IsComplete);
        Assert.True(result.Summary.SkippedCount > 0);
        var root = Assert.Single(result.Tree.Roots);
        Assert.False(result.Tree.CountKnownOf(root));
        Assert.False(result.Tree.LogicalSizeKnownOf(root));
        Assert.True(result.Tree.Count <= countLimit);
    }

    [Fact]
    public void ParserKeepsResidentZeroAndExactSparseMetadata()
    {
        var resident = Parse(Record(false, 0, Name("resident"), Resident(0x80, new byte[9])));
        Check(resident.SizeBytes == 9 && resident.AllocatedBytes == 0 && resident.AllocationKnown, "resident has no separate extent");
        var sparse = Parse(Record(false, 0, Name("sparse"), Sparse(true, 64)));
        Check(sparse.SizeBytes == 8192 && sparse.AllocatedBytes == 64 && sparse.AllocationKnown, "TotalAllocated differs from logical and ordinary allocation");
        Check(Parse(Record(false, 0, Name("sparse-zero"), Sparse(true, 0))).AllocatedBytes == 0, "sparse zero stays zero");
        Check(!Parse(Record(false, 0, Name("missing-slot"), Sparse(false, 0))).AllocationKnown, "missing TotalAllocated is unknown");
        var missing = Parse(Record(false));
        Check(missing.Names.Count == 0 && !missing.HasPrimaryData && !missing.LogicalSizeKnown && !missing.AllocationKnown, "legal unnamed base remains pending extension");
        var links = Parse(Record(false, 0, Name("a"), Name("A~1", 10, 2), Name("b", 20, 2)));
        Check(links.Names.Count == 2 && links.Names.All(link => link.Name != "A~1"), "DOS fallback is per exact parent reference");
        var otherIncarnation = Name("A~1", 10, 2); U64(otherIncarnation, 24, (2UL << 48) | 10);
        Check(Parse(Record(false, 0, Name("a"), otherIncarnation)).Names.Count == 2, "different parent sequence cannot suppress DOS evidence");
        var malformed = Record(false, 0, Name("bad")); U32(malformed, 60, 1024);
        Check(NtfsFileRecordParser.Classify(malformed, out _, out _) == NtfsFileRecordParser.RecordKind.Unreadable, "attribute bounds fail closed");
    }
    [Fact]
    public void HardLinksUseNamespaceLogicalAndUniqueAllocationWithinScope()
    {
        var tree = Build(Links()); var root = tree.Roots.Single();
        Check(tree.FileCountOf(root) == 4 && tree.SizeOf(root) == 307 && tree.AllocatedOf(root) == 64, "two same-parent names and cross-parent hard link count separately but allocate once");
        var scoped = Build(Links(), @"C:\Work"); root = scoped.Roots.Single();
        Check(scoped.FileCountOf(root) == 3 && scoped.SizeOf(root) == 207 && scoped.AllocatedOf(root) == 64, "folder scope excludes outside alias");
        var incomplete = Build(Links(), complete: false); root = incomplete.Roots.Single();
        Check(!incomplete.CountKnownOf(root) && !incomplete.LogicalSizeKnownOf(root) && !incomplete.AllocationKnownOf(root), "incomplete scope never presents complete totals");
        var unknown = Links(); unknown[12] = unknown[12] with { AllocationKnown = false };
        var unknownTree = Build(unknown); root = unknownTree.Roots.Single();
        Check(!unknownTree.AllocationKnownOf(root) && unknownTree.LogicalSizeKnownOf(root), "unknown allocation does not fabricate known zero");
    }
    [Fact]
    public async Task RecordedScannerPublishesSameStoreAndBorrowNeverScans()
    {
        var catalog = new Catalog(); var opens = 0;
        var scanner = new SdkScanner(catalog, openReader: _ => { opens++; return new Reader(Image()); });
        var resource = new WindowsMftDiskUsageResource(scanner, new(DiskUsageScanScopes.Volume, DiskUsageScanModes.Fast, "C:"));
        Check(resource.Backend.Catalog.Fields.IsEmpty && opens == 0 && catalog.Reads == 0, "construction and passive catalog do not scan");
        await resource.Backend.DiscoverAsync(default);
        int nodeCount;
        var summary = resource.Store.CurrentSummary;
        using (var read = resource.Store.TryAcquire()!)
        {
            var tree = read.Result.Tree; var root = tree.Roots.Single(); nodeCount = tree.Count;
            Check(opens == 1 && tree.FileCountOf(root) == 1 && tree.SizeOf(root) == 9, "DOS-only extension cannot duplicate base Win32 name");
        }
        Check(resource.Store.ReadInformation().Devices.Count == 1 && resource.Store.Backend.Catalog.Fields.Length == 3,
            "default host publication constructs roots only");
        using var details = resource.CreateInformationView(Enumerable.Range(0, nodeCount));
        using var http = new HttpClient(); var core = new ReadWriteCore(details.Backend, new DependencyActions(http, []));
        var field = core.Catalog.Fields.Single(c => c.Field.FieldName == "file_all_logical_size_actual").Field;
        Check(core.Query(field).Read.Supported && core.ReadCached(field).Value?.Integer == MetricInteger.Parse("9") && opens == 1, "canonical query/cache read committed original owner");
        var expected = new Dictionary<string, string>
        {
            ["directory_all_file_count_aggregated"] = "1", ["directory_all_logical_size_aggregated"] = "9",
            ["directory_all_unique_allocated_size_aggregated"] = "0", ["file_all_logical_size_actual"] = "9",
            ["file_all_allocated_size_actual"] = "0"
        };
        Check(core.Catalog.Fields.Select(capability => capability.Field.FieldName).Distinct().Count() == 5, "all five existing declarations are bound");
        foreach (var capability in core.Catalog.Fields)
        {
            var reading = core.ReadCached(capability.Field);
            Check(reading.Status == FieldObservationStatus.Current && reading.Value?.Integer == MetricInteger.Parse(expected[capability.Field.FieldName]),
                "canonical projection preserves exact count/bytes and legal zero");
        }
        Check(opens == 1, "canonical reads never open another volume");
        using (var borrow = details.BorrowSubscriptionCapture())
        {
            Check(ReferenceEquals(borrow.Backend, details.Backend), "borrow uses the selected view over the same result owner");
            borrow.Bind([field]); borrow.SynchronizeDemand([field]); await borrow.CollectAsync([field], new Permit());
            Check(ReferenceEquals(summary, resource.Store.CurrentSummary) && opens == 1, "borrow collection only observes existing scan");
            resource.Store.ClearCurrentValues(); await borrow.CollectAsync([field], new Permit());
            Check(resource.Store.CurrentSummary is null && core.ReadCached(field).Value is null && opens == 1, "failed scan clears old values without scan");
            resource.Store.Clear();
            var retired = await borrow.CollectAsync([field], new Permit());
            Check(resource.Store.Backend.Catalog.Fields.IsEmpty && retired.Readings.Single().Status == FieldObservationStatus.Unavailable && opens == 1, "retirement drops metadata while old borrowed selection becomes unavailable");
        }
        var merged = await new SdkScanner(new Catalog(), openReader: _ => new Reader(Image(true))).ScanAsync(
            new(DiskUsageScanScopes.Volume, DiskUsageScanModes.Fast, "C:"), null, default);
        Check(merged.Tree.FileCountOf(merged.Tree.Roots.Single()) == 1 && merged.Tree.SizeOf(merged.Tree.Roots.Single()) == 9, "matching extension supplies missing base name and primary DATA");
        var stale = await new SdkScanner(new Catalog(), openReader: _ => new Reader(Image(true, 2))).ScanAsync(
            new(DiskUsageScanScopes.Volume, DiskUsageScanModes.Fast, "C:"), null, default);
        Check(stale.Tree.FileCountOf(stale.Tree.Roots.Single()) == 0 && !stale.Tree.CountKnownOf(stale.Tree.Roots.Single()), "stale extension cannot bind replacement base and scope is incomplete");
    }

    [Fact]
    public async Task RootPublicationIsBoundedAndDetailsRequireExactIdentityAfterCommit()
    {
        DiskUsageScanResult Result(DiskUsageTree tree, DateTimeOffset time) => new(tree,
            new(DiskUsageScanScopes.Volume, DiskUsageScanModes.Fast, "C:", ["C:\\"], DiskUsageScanKinds.MasterFileTable,
                [], time, 0, 0, 0, 0, 0, 0));
        DiskUsageTree Tree(int count, ushort fileSequence = 1, bool insertFirst = false)
        {
            var builder = new DiskUsageTreeBuilder(count + 2);
            var root = builder.Add(-1, "C:\\", true, 0, 0, new(0x1234, 5, 1));
            if (insertFirst) builder.Add(root, "new-first", false, 0, 0, new(0x1234, 999999, 1));
            for (var i = 0; i < count; i++) builder.Add(root, $"file-{i}", false, 3, 0, new(0x1234, i + 10, fileSequence));
            return builder.Build();
        }
        var observed = new DateTimeOffset(2026, 10, 8, 0, 0, 0, TimeSpan.Zero);
        var store = new DiskUsageTreeStore();
        store.Replace(Result(Tree(1), observed)); // Warm the same JSON projection before allocation measurement.
        var large = Result(Tree(20_000), observed);
        var before = GC.GetAllocatedBytesForCurrentThread();
        store.Replace(large);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Check(store.ReadInformation().Devices.Count == 1 && store.Backend.Catalog.Fields.Length == 3 && allocated < 1_000_000,
            "unrequested detailed fields never materialize across the large compact tree");
        using var view = store.CreateInformationView([1]);
        var field = view.Backend.Catalog.Fields.Single(item => item.Field.FieldName == "file_all_logical_size_actual").Field;
        Check(view.Backend.ReadCached(field).Value?.Integer == MetricInteger.Parse("3"), "explicit bounded file selection exposes detailed fields");
        using (var temporaryView = store.CreateInformationView([1]))
        {
            temporaryView.Dispose();
            Check(ReferenceEquals(large.Summary, store.CurrentSummary) && store.ReadInformation().Devices.Count == 1,
                "disposing a view never retires the borrowed original tree");
        }
        store.Replace(Result(Tree(2, insertFirst: true), observed.AddMinutes(1)));
        Check(view.Backend.ReadCached(field).Value?.Integer == MetricInteger.Parse("3"), "same identity/path survives changed tree node number");
        store.Replace(Result(Tree(2, fileSequence: 2), observed.AddMinutes(2)));
        Check(view.Backend.ReadCached(field).Status == FieldObservationStatus.Unavailable
            && view.Backend.ReadCached(field).ObservedAt is null, "replacement record cannot inherit selected value or fresh timestamp");
        store.ClearCurrentValues();
        Check(view.Backend.ReadCached(field).Status == FieldObservationStatus.Unavailable
            && view.Backend.ReadCached(field).ObservedAt is null, "failure is unavailable without observation timestamp");
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => view.Backend.DiscoverAsync(canceled.Token).AsTask());
        using (var streamCancellation = new CancellationTokenSource())
        {
            await using var watcher = view.SubscribeInformationAsync(streamCancellation.Token).GetAsyncEnumerator();
            Check(await watcher.MoveNextAsync(), "view starts from original committed publication");
            var waiting = watcher.MoveNextAsync().AsTask();
            streamCancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        }
        Assert.Throws<ArgumentOutOfRangeException>(() => store.CreateInformationView(Enumerable.Range(0, 257)));
        var oldSelection = view.Backend.Select([field]);
        store.Clear();
        Check(view.Backend.Catalog.Fields.IsEmpty && InformationReadWriteBackend.Read(oldSelection, view.ReadInformation())[0].Status == FieldObservationStatus.Unavailable,
            "clear retires the original result and view declarations");
        view.Dispose();
        Check(view.ReadInformation().Devices.Count == 0 && view.Backend.Catalog.Fields.IsEmpty, "view disposal releases selected metadata only");
    }

    private static void Check(bool condition, string reason)
    { if (!condition) throw new InvalidOperationException(reason); }
    private static void U16(byte[] b, int o, ushort v) => BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(o), v);
    private static void U32(byte[] b, int o, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(o), v);
    private static void U64(byte[] b, int o, ulong v) => BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(o), v);
    private static byte[] Name(string name, long parent = 10, byte ns = 1)
    {
        var value = new byte[66 + name.Length * 2]; U64(value, 0, (1UL << 48) | (ulong)parent);
        value[64] = (byte)name.Length; value[65] = ns;
        Encoding.Unicode.GetBytes(name).CopyTo(value, 66); return Resident(0x30, value);
    }
    private static byte[] Resident(uint type, byte[] value)
    {
        var a = new byte[(24 + value.Length + 7) & ~7]; U32(a, 0, type); U32(a, 4, (uint)a.Length);
        U32(a, 16, (uint)value.Length); U16(a, 20, 24); value.CopyTo(a, 24); return a;
    }
    private static byte[] Sparse(bool fullHeader, ulong actualAllocated)
    {
        var a = new byte[80]; U32(a, 0, 0x80); U32(a, 4, 80); a[8] = 1;
        U16(a, 12, 0x8000); U16(a, 32, (ushort)(fullHeader ? 72 : 64));
        U64(a, 40, 4096); U64(a, 48, 8192); U64(a, 64, actualAllocated); return a;
    }
    private static byte[] Record(bool directory, ulong baseReference = 0, params byte[][] attributes)
    {
        var b = new byte[512]; "FILE"u8.CopyTo(b); U16(b, 4, 48); U16(b, 6, 2);
        U16(b, 16, 1); U16(b, 20, 56); U16(b, 22, (ushort)(directory ? 3 : 1));
        U64(b, 32, baseReference); U16(b, 48, 0xA55A); U16(b, 510, 0xA55A);
        var cursor = 56; foreach (var a in attributes) { a.CopyTo(b, cursor); cursor += a.Length; }
        U32(b, cursor, uint.MaxValue); return b;
    }
    private static NtfsFileRecord Parse(byte[] bytes)
    {
        Check(NtfsFileRecordParser.Classify(bytes, out var f, out _) == NtfsFileRecordParser.RecordKind.File, "valid base record");
        return f;
    }
    private static Dictionary<long, NtfsFileRecord> Links() => new()
    {
        [5] = new(true, true, 5, ".", 0, 0) { SequenceNumber = 1 },
        [10] = new(true, true, 5, "Work", 0, 0) { SequenceNumber = 1, Names = [new(5, "Work", 1)] },
        [20] = new(true, true, 5, "Other", 0, 0) { SequenceNumber = 1, Names = [new(5, "Other", 1)] },
        [11] = new(true, false, 10, "a", 100, 64)
            { SequenceNumber = 2, Names = [new(10, "a", 1), new(10, "alias", 1), new(20, "b", 1)] },
        [12] = new(true, false, 10, "resident", 7, 0)
            { SequenceNumber = 1, Names = [new(10, "resident", 1)] }
    };
    private static DiskUsageTree Build(Dictionary<long, NtfsFileRecord> records, string? scope = null, bool complete = true)
    {
        var builder = new DiskUsageTreeBuilder();
        SdkScanner.BuildSubtree("C:", scope, records, builder, new SdkScanner.ScanTotals(), default, 0x1234, complete);
        return builder.Build();
    }
    private static byte[] Image(bool namesInExtension = false, ushort baseSequence = 1)
    {
        var image = new byte[13 * 512];
        Record(true, 0, Name(".", 5)).CopyTo(image, 5 * 512);
        Record(true, 0, Name("Work", 5)).CopyTo(image, 10 * 512);
        (namesInExtension ? Record(false) : Record(false, 0, Name("long.bin"), Resident(0x80, new byte[9])))
            .CopyTo(image, 11 * 512);
        (namesInExtension ? Record(false, ((ulong)baseSequence << 48) | 11, Name("merged.bin"), Resident(0x80, new byte[9]))
            : Record(false, (1UL << 48) | 11, Name("LONG~1.BIN", 10, 2)))
            .CopyTo(image, 12 * 512);
        return image;
    }
    private sealed class Catalog : IDiskUsageVolumeCatalog
    {
        public int Reads;
        public IReadOnlyList<DiskUsageVolume> ReadVolumes()
        { Reads++; return [new("C:", "recorded", "NTFS", DiskUsageVolumeKinds.Physical, 8192, 0, true, true)]; }
    }
    private sealed class Reader(byte[] image) : INtfsDiskUsageVolumeReader
    {
        public NtfsVolumeLayout Layout => new(512, 1, 1, 512) { VolumeSerialNumber = 0x1234 };
        public IReadOnlyList<NtfsDataRun> ReadMasterFileTableRuns() => [new(1, 13)];
        public int Read(long offset, Span<byte> destination)
        {
            var start = checked((int)(offset - 512));
            if (start < 0 || start >= image.Length) return 0;
            var length = Math.Min(destination.Length, image.Length - start);
            image.AsSpan(start, length).CopyTo(destination); return length;
        }
        public void Dispose() { }
    }
    private sealed class Permit : IMetricPublicationPermit
    {
        public ulong WorkspaceIncarnation => 1; public ulong ConfigurationGeneration => 1;
        public bool IsActive => true; public bool TryPublish(Action commit) { commit(); return true; }
    }
}
