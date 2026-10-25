using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Text.Json;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.Information;
using ResourceManager.ReadWrite.Windows.Files.Layout;
using Xunit;

namespace Resource_Manager_APP.Tests;

public sealed class WindowsFileLayoutInformationQueryTests
{
    [Fact]
    public async Task ExplicitFileCaptureCacheAndBorrowShareOneCurrentAndOnlyExplicitCaptureReads()
    {
        var identity = "windows-file-layout|file|0000000000000012|00112233445566778899AABBCCDDEEFF";
        var reader = new RecordedReader(new(WindowsFileLayoutTargetKind.File, identity, 3, null));
        using var query = new WindowsFileLayoutInformationQuery(
            WindowsFileLayoutQueryScope.ForFile(@"C:\recorded\item.bin"), reader);

        Assert.Equal(0, reader.Reads);
        var catalog = await query.Backend.DiscoverAsync(CancellationToken.None);
        var capability = Assert.Single(catalog.Fields);
        var field = capability.Field;
        Assert.Equal("file_all_extent_count_actual", field.FieldName);
        Assert.Equal("count", capability.Definition.Unit);
        Assert.Equal("windows.fsctl.file-layout", capability.ReadSource);
        Assert.Equal(identity, field.DeviceIdentity);
        Assert.Equal("file", Assert.Single(query.ReadInformation().Devices).ObjectType);
        Assert.Equal(MetricInteger.Parse("3"), query.Backend.ReadCached(field).Value?.Integer);
        Assert.Equal(1, reader.Reads);

        using var borrow = query.BorrowSubscriptionCapture();
        _ = borrow.Bind([field]);
        var observed = await borrow.CollectAsync([field], new Permit(), CancellationToken.None);
        Assert.True(observed.Committed);
        Assert.Equal(query.Backend.ReadCached(field), Assert.Single(observed.Readings));
        Assert.Equal(1, reader.Reads); // A subscription borrow observes current; it never starts an extent query.

        reader.Value = new(WindowsFileLayoutTargetKind.File, identity, 5, null);
        var direct = Assert.Single(await query.Backend.CollectAsync([field], CancellationToken.None));
        Assert.Equal(MetricInteger.Parse("5"), direct.Value?.Integer);
        Assert.Equal(direct, query.Backend.ReadCached(field));
        Assert.Equal(2, reader.Reads);
    }

    [Fact]
    public void CallerCanRaiseFormerPolicyCapsWithinRepresentableBounds()
    {
        var limits = new WindowsFileLayoutQueryLimits
        {
            InitialPageBytes = 1_048_577,
            MaximumPageBytes = 1_048_577,
            MaximumPageCount = 4_097,
            MaximumReturnedBytes = 67_108_865,
            MaximumExtentCount = 1_000_001,
            MaximumRetriesPerPage = 6
        };
        var scope = WindowsFileLayoutQueryScope.ForFile(@"C:\recorded\large.bin", limits);
        var identity = "windows-file-layout|file|0000000000000012|00112233445566778899AABBCCDDEEFF";
        var reader = new RecordedReader(new(WindowsFileLayoutTargetKind.File, identity, 0, null));
        using var query = new WindowsFileLayoutInformationQuery(scope, reader);

        Assert.Same(limits, scope.Limits);
        Assert.Equal(1_048_577, scope.Limits.MaximumPageBytes);
        Assert.Equal(4_097, scope.Limits.MaximumPageCount);
        Assert.Equal(67_108_865, scope.Limits.MaximumReturnedBytes);
        Assert.Equal(1_000_001, scope.Limits.MaximumExtentCount);
        Assert.Equal(6, scope.Limits.MaximumRetriesPerPage);
        Assert.Equal(0, reader.Reads); // Larger bounds do not make construction perform a read.

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            WindowsFileLayoutQueryScope.ForFile(@"C:\recorded\invalid.bin", new WindowsFileLayoutQueryLimits
            {
                InitialPageBytes = 32,
                MaximumPageBytes = int.MaxValue
            }));
    }

    [Fact]
    public async Task ExplicitVolumeGuidScopeIsValidatedPassiveAndBypassesMountPointLookup()
    {
        const string suppliedPath = @"\\?\Volume{ABCDEFAB-CDEF-ABCD-EFAB-CDEFABCDEFAB}\";
        const string normalizedPath = @"\\?\Volume{abcdefab-cdef-abcd-efab-cdefabcdefab}\";
        Assert.True(WindowsFileLayoutQueryScope.TryNormalizeVolumeGuidPath(suppliedPath, out var parsedPath));
        Assert.Equal(normalizedPath, parsedPath);
        Assert.False(WindowsFileLayoutQueryScope.TryNormalizeVolumeGuidPath(@"\\?\Volume{not-a-guid}\", out _));
        var scope = WindowsFileLayoutQueryScope.ForVolumeGuidPath(suppliedPath);
        var identity = $"windows-file-layout|volume|{normalizedPath.TrimEnd('\\')}|0000000000000013";
        var observedAt = DateTimeOffset.UnixEpoch;
        var map = new WindowsVolumeAllocationMap(normalizedPath, 19, 0, 4096, observedAt, observedAt, []);
        var reader = new RecordedReader(new(WindowsFileLayoutTargetKind.VolumeGuidPath, identity, null, map));
        using var query = new WindowsFileLayoutInformationQuery(scope, reader);

        Assert.Equal(WindowsFileLayoutTargetKind.VolumeGuidPath, scope.TargetKind);
        Assert.Equal(normalizedPath, scope.TargetPath);
        Assert.Equal(0, reader.Reads);
        var resolverCalls = 0;
        Assert.Equal(normalizedPath, WindowsFileLayoutInformationReader.ResolveVolumeGuidPath(scope, _ =>
        {
            resolverCalls++;
            throw new InvalidOperationException("A GUID scope must not resolve through a drive root.");
        }));
        Assert.Equal(0, resolverCalls);

        var capability = Assert.Single((await query.Backend.DiscoverAsync(CancellationToken.None)).Fields);
        Assert.Same(scope, reader.LastScope);
        Assert.Equal(WindowsFileLayoutTargetKind.VolumeGuidPath, reader.LastScope?.TargetKind);
        Assert.Equal(normalizedPath, reader.LastScope?.TargetPath);
        Assert.Equal(identity, capability.Field.DeviceIdentity);
        Assert.Equal("volume", Assert.Single(query.ReadInformation().Devices).ObjectType);
        Assert.Equal(1, reader.Reads);

        Assert.Throws<ArgumentException>(() => WindowsFileLayoutQueryScope.ForVolumeGuidPath(
            @"\\?\Volume{not-a-guid}\"));
        Assert.Throws<ArgumentException>(() => WindowsFileLayoutQueryScope.ForVolumeGuidPath(
            @"\\?\Volume{abcdefab-cdef-abcd-efab-cdefabcdefab}"));
        Assert.Throws<ArgumentException>(() => WindowsFileLayoutQueryScope.ForVolumeMountPoint(
            @"C:\mounted-folder\"));
        Assert.Equal(1, reader.Reads);
    }

    [Fact]
    public async Task CompleteVolumeMapCarriesExactClusterUnitsAndPageCoverage()
    {
        var observedAt = DateTimeOffset.UnixEpoch;
        var page = new WindowsVolumeBitmapPage(0, 13, 13, Convert.ToBase64String([0b0000_1101, 0b0001_1111]), observedAt);
        var map = new WindowsVolumeAllocationMap(@"\\?\Volume{11111111-2222-3333-4444-555555555555}\",
            19, 13, 4096, observedAt, observedAt, [page]);
        var reader = new RecordedReader(new(WindowsFileLayoutTargetKind.VolumeMountPoint,
            @"windows-file-layout|volume|\\?\Volume{11111111-2222-3333-4444-555555555555}|0000000000000013", null, map));
        using var query = new WindowsFileLayoutInformationQuery(
            WindowsFileLayoutQueryScope.ForVolumeMountPoint(@"C:\"), reader);

        var catalog = await query.Backend.DiscoverAsync(CancellationToken.None);
        var capability = Assert.Single(catalog.Fields);
        Assert.Equal("volume_all_fragmentation_map_actual", capability.Field.FieldName);
        Assert.Equal("map", capability.Definition.Unit);
        Assert.Equal("windows.fsctl.file-layout", capability.ReadSource);
        Assert.Equal("volume", Assert.Single(query.ReadInformation().Devices).ObjectType);
        var structure = Assert.IsType<JsonElement>(query.Backend.ReadCached(capability.Field).Value!.Structure);
        Assert.Equal("13", structure.GetProperty("totalClusters").GetString());
        Assert.True(structure.GetProperty("coverageComplete").GetBoolean());
        Assert.Equal("0", structure.GetProperty("coverageStartingLcn").GetString());
        Assert.Equal("13", structure.GetProperty("coverageClusterCount").GetString());
        Assert.Equal(4096U, structure.GetProperty("bytesPerCluster").GetUInt32());
        Assert.Equal("least-significant-bit-first", structure.GetProperty("bitOrder").GetString());
        var projectedPage = Assert.Single(structure.GetProperty("pages").EnumerateArray());
        Assert.Equal("0", projectedPage.GetProperty("returnedStartingLcn").GetString());
        Assert.Equal("13", projectedPage.GetProperty("returnedClusterCount").GetString());
        Assert.Equal(1, reader.Reads);
    }

    [Fact]
    public async Task FailedSelectedReadClearsKnownValueAndCancellationBeforeCommitDoesNotPublish()
    {
        var identity = "windows-file-layout|file|0000000000000012|00112233445566778899AABBCCDDEEFF";
        var reader = new RecordedReader(new(WindowsFileLayoutTargetKind.File, identity, 2, null));
        using var query = new WindowsFileLayoutInformationQuery(
            WindowsFileLayoutQueryScope.ForFile(@"C:\recorded\item.bin"), reader);
        var field = Assert.Single((await query.Backend.DiscoverAsync(CancellationToken.None)).Fields).Field;
        var beforeFailure = query.ReadInformation();

        reader.Failure = new IOException("recorded selected-query failure");
        await Assert.ThrowsAsync<IOException>(() => query.Backend.CollectAsync([field], CancellationToken.None).AsTask());
        Assert.Equal(identity, Assert.Single(query.ReadInformation().Devices).DeviceIdentity);
        Assert.Null(query.ReadInformation().FindField(field)?.Value);
        Assert.NotSame(beforeFailure, query.ReadInformation());

        reader.Failure = null;
        reader.Value = new(WindowsFileLayoutTargetKind.File, identity, 8, null);
        var beforeCancellation = query.ReadInformation();
        using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => query.CaptureAsync(cancellation.Token, commit =>
        {
            cancellation.Cancel();
            commit(); // Cancellation lands after projection but before the owner's atomic write.
            return true;
        }).AsTask());
        Assert.Same(beforeCancellation, query.ReadInformation());
    }

    [Fact]
    public void NativeByteParsersRespectBytesReturnedAndPreserveSparseRunAndValidZero()
    {
        var oneRun = new byte[32];
        BinaryPrimitives.WriteUInt32LittleEndian(oneRun, 1);
        BinaryPrimitives.WriteInt64LittleEndian(oneRun.AsSpan(8), 0);
        BinaryPrimitives.WriteInt64LittleEndian(oneRun.AsSpan(16), 12);
        BinaryPrimitives.WriteInt64LittleEndian(oneRun.AsSpan(24), -1);
        Assert.True(WindowsFileLayoutInformationReader.TryParseRetrievalPointerPage(oneRun, 32, out var parsed));
        var sparse = Assert.Single(parsed.Extents);
        Assert.Equal(12, sparse.NextVcn);
        Assert.Equal(-1, sparse.Lcn);
        Assert.False(WindowsFileLayoutInformationReader.TryParseRetrievalPointerPage(oneRun, 31, out _));

        var empty = new byte[16];
        Assert.True(WindowsFileLayoutInformationReader.TryParseRetrievalPointerPage(empty, 16, out var zeroPage));
        Assert.Empty(zeroPage.Extents);
        Assert.False(WindowsFileLayoutInformationReader.TryParseRetrievalPointerPage(empty, 15, out _));

        var bitmap = new byte[18];
        BinaryPrimitives.WriteInt64LittleEndian(bitmap, 0);
        BinaryPrimitives.WriteInt64LittleEndian(bitmap.AsSpan(8), 13);
        bitmap[16] = 0xFF;
        bitmap[17] = 0xFF;
        Assert.True(WindowsFileLayoutInformationReader.TryParseVolumeBitmapPage(bitmap, 18, out var parsedBitmap));
        Assert.Equal((ulong)13, parsedBitmap.ReturnedClusterCount);
        Assert.Equal(new byte[] { 0xFF, 0x1F }, parsedBitmap.Bits);
        Assert.False(WindowsFileLayoutInformationReader.TryParseVolumeBitmapPage(bitmap, 15, out _));

        var volumeData = new byte[96];
        BinaryPrimitives.WriteUInt64LittleEndian(volumeData, 19);
        BinaryPrimitives.WriteInt64LittleEndian(volumeData.AsSpan(16), 13);
        BinaryPrimitives.WriteUInt32LittleEndian(volumeData.AsSpan(44), 4096);
        Assert.True(WindowsFileLayoutInformationReader.TryParseNtfsVolumeData(volumeData, 96, out var parsedVolume));
        Assert.Equal((ulong)19, parsedVolume.VolumeSerialNumber);
        Assert.Equal(13, parsedVolume.TotalClusters);
        Assert.Equal((uint)4096, parsedVolume.BytesPerCluster);
        Assert.False(WindowsFileLayoutInformationReader.TryParseNtfsVolumeData(volumeData, 95, out _));
    }

    [Fact]
    public void RetrievalPointerEndOfFileCompletesOnlyTheAlreadyProvenRunCount()
    {
        // ERROR_HANDLE_EOF on the first StartingVcn=0 request proves zero returned extent locations.
        Assert.True(WindowsFileLayoutInformationReader.TryCompleteRetrievalPointerQueryOnEof(
            false, WindowsFileLayoutInformationReader.ErrorHandleEof, 0, 0, out var emptyCount));
        Assert.Equal((ulong)0, emptyCount);

        // The same status at a continuation point ends paging while preserving earlier parsed runs.
        Assert.True(WindowsFileLayoutInformationReader.TryCompleteRetrievalPointerQueryOnEof(
            false, WindowsFileLayoutInformationReader.ErrorHandleEof, 0, 4, out var priorCount));
        Assert.Equal((ulong)4, priorCount);

        // Other Win32 failures and successful responses do not become counts via the EOF path.
        Assert.False(WindowsFileLayoutInformationReader.TryCompleteRetrievalPointerQueryOnEof(
            false, 5, 0, 0, out _));
        Assert.False(WindowsFileLayoutInformationReader.TryCompleteRetrievalPointerQueryOnEof(
            true, 0, 0, 4, out _));
        Assert.Throws<InvalidDataException>(() =>
            WindowsFileLayoutInformationReader.TryCompleteRetrievalPointerQueryOnEof(
                false, WindowsFileLayoutInformationReader.ErrorHandleEof, 1, 4, out _));
    }

    [Fact]
    public async Task ZeroExtentIsCurrentAndDisposeRetiresTheQuery()
    {
        var identity = "windows-file-layout|file|0000000000000012|00112233445566778899AABBCCDDEEFF";
        var reader = new RecordedReader(new(WindowsFileLayoutTargetKind.File, identity, 0, null));
        var query = new WindowsFileLayoutInformationQuery(
            WindowsFileLayoutQueryScope.ForFile(@"C:\recorded\empty.bin"), reader);
        var field = Assert.Single((await query.Backend.DiscoverAsync(CancellationToken.None)).Fields).Field;
        var reading = query.Backend.ReadCached(field);
        Assert.Equal(FieldObservationStatus.Current, reading.Status);
        Assert.Equal(MetricInteger.Parse("0"), reading.Value?.Integer);

        query.Dispose();
        Assert.Empty(query.ReadInformation().Devices);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => query.CaptureAsync().AsTask());
    }

    [Fact]
    public async Task PartialVolumeWindowFailsDiscoveryInsteadOfBecomingAFullMap()
    {
        var now = DateTimeOffset.UnixEpoch;
        var partial = new WindowsVolumeAllocationMap(@"\\?\Volume{11111111-2222-3333-4444-555555555555}\",
            19, 13, 4096, now, now,
            [new WindowsVolumeBitmapPage(0, 13, 5, Convert.ToBase64String([0x0D]), now)]);
        var reader = new RecordedReader(new(WindowsFileLayoutTargetKind.VolumeMountPoint,
            @"windows-file-layout|volume|\\?\Volume{11111111-2222-3333-4444-555555555555}|0000000000000013", null, partial));
        using var query = new WindowsFileLayoutInformationQuery(
            WindowsFileLayoutQueryScope.ForVolumeMountPoint(@"C:\"), reader);

        await Assert.ThrowsAsync<InvalidDataException>(() => query.Backend.DiscoverAsync(CancellationToken.None).AsTask());
        Assert.Empty(query.ReadInformation().Devices);
        Assert.Equal(1, reader.Reads);
    }

    private sealed class RecordedReader(WindowsFileLayoutReadResult value) : IWindowsFileLayoutInformationReader
    {
        private WindowsFileLayoutReadResult current = value;
        private WindowsFileLayoutQueryScope? lastScope;
        private int reads;
        internal int Reads => Volatile.Read(ref reads);
        internal WindowsFileLayoutQueryScope? LastScope => Volatile.Read(ref lastScope);
        internal Exception? Failure { get; set; }
        internal WindowsFileLayoutReadResult Value { set => Volatile.Write(ref current, value); }

        public WindowsFileLayoutReadResult Read(WindowsFileLayoutQueryScope scope,
            WindowsFileLayoutInformationFields selected, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Volatile.Write(ref lastScope, scope);
            Interlocked.Increment(ref reads);
            if (Failure is { } failure) throw failure;
            return Volatile.Read(ref current);
        }
    }

    private sealed class Permit : IMetricPublicationPermit
    {
        public ulong WorkspaceIncarnation => 1;
        public ulong ConfigurationGeneration => 1;
        public bool IsActive => true;
        public bool TryPublish(Action commit) { commit(); return true; }
    }
}
