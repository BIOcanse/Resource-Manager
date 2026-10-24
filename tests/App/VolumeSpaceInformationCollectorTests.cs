using System.Globalization;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using System.Text.Json;
using ResourceManager.App.Domain.DeviceTopology;
using ResourceManager.App.Infrastructure.DeviceTopology;
using ResourceManager.App.Infrastructure.DeviceTopology.Snapshots;

namespace Resource_Manager_APP.Tests;

public sealed class VolumeSpaceInformationCollectorTests
{
    internal const string RootA = @"\\?\Volume{AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE}\";
    internal const string RootB = @"\\?\Volume{11111111-2222-3333-4444-555555555555}\";
    internal const string IdA = "windows-volume|AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE";
    internal const string IdB = "windows-volume|11111111-2222-3333-4444-555555555555";

    [Fact]
    public void ActualCallerPoolReserveAndUsedSpaceKeepTheirOwnReportedQuantities()
    {
        var api = new Api(); var result = new WindowsVolumeSpaceInformationReader(api).Read()!;
        Assert.Equal(new[] { RootA, RootB }, api.Requested);
        var volume = Assert.Single(result, item => item.DeviceIdentity == IdA);
        Assert.Equal((UInt128)4096000, volume.TotalBytes); Assert.Equal((UInt128)1024000, volume.AvailableBytes);
        Assert.Equal((UInt128)204800, volume.PoolUnavailableBytes); Assert.Equal((UInt128)3276800, volume.CallerTotalBytes);
        Assert.Equal((UInt128)204800, volume.CallerAvailableBytes); Assert.Equal((UInt128)204800, volume.CallerPoolUnavailableBytes);
        Assert.Equal((UInt128)2457600, volume.UsedBytes); Assert.NotEqual(volume.TotalBytes - volume.AvailableBytes, volume.UsedBytes);
        Assert.Equal((UInt128)409600, volume.ReservedBytes); Assert.Equal((UInt128)122880, volume.StorageReserveBytes);
        Assert.Equal((UInt128)307200, volume.AvailableCommittedBytes); Assert.Equal((UInt128)409600, volume.PoolAvailableBytes);
        Assert.Equal("S-1-5-18", volume.QueryCallerSid); Assert.Equal(8U, volume.SectorsPerAllocationUnit);
        Assert.Equal(512U, volume.BytesPerSector); Assert.Equal(4096UL, volume.AllocationUnitBytes);
    }

    [Fact]
    public void LargeCountsAndGeometryNeverPassThroughDoubleOrOverflowUInt64()
    {
        var data = Sample(); data.ActualTotalAllocationUnits = ulong.MaxValue;
        data.ActualAvailableAllocationUnits = 9007199254740993UL;
        var volume = WindowsVolumeSpaceInformationReader.Create(IdA, data, "S-1-5-18");
        Assert.Equal(UInt128.Parse("75557863725914323415040", CultureInfo.InvariantCulture), volume.TotalBytes);
        Assert.Equal(UInt128.Parse("36893488147419107328", CultureInfo.InvariantCulture), volume.AvailableBytes);
        data.SectorsPerAllocationUnit = uint.MaxValue; data.BytesPerSector = uint.MaxValue;
        volume = WindowsVolumeSpaceInformationReader.Create(IdA, data, "S-1-5-18");
        Assert.Equal(18446744065119617025UL, volume.AllocationUnitBytes);
        Assert.Equal((UInt128)ulong.MaxValue * 18446744065119617025UL, volume.TotalBytes);
        var fields = Project(volume); Assert.Equal(volume.TotalBytes!.Value.ToString(CultureInfo.InvariantCulture),
            Field(fields, IdA, "volume_all_size_actual").Value!.Value.GetString());
    }

    [Theory]
    [InlineData(null)] [InlineData("")] [InlineData(" ")]
    public void MissingCallerIdentityOnlyEmptiesQuotaDependentValues(string? sid)
    {
        var data = Sample(); var volume = WindowsVolumeSpaceInformationReader.Create(IdA, data, sid);
        Assert.Null(volume.QueryCallerSid); Assert.Null(volume.CallerTotalBytes); Assert.Null(volume.CallerAvailableBytes);
        Assert.Null(volume.CallerPoolUnavailableBytes); Assert.Equal((UInt128)4096000, volume.TotalBytes);
        Assert.Equal((UInt128)2457600, volume.UsedBytes);
    }

    [Theory]
    [InlineData(0U, 512U)] [InlineData(8U, 0U)] [InlineData(0U, 0U)]
    public void MissingGeometryEmptiesOnlyDependentByteValues(uint sectors, uint bytes)
    {
        var data = Sample(); data.SectorsPerAllocationUnit = sectors; data.BytesPerSector = bytes;
        var volume = WindowsVolumeSpaceInformationReader.Create(IdA, data, "S-1-5-18");
        Assert.Null(volume.AllocationUnitBytes); Assert.Null(volume.TotalBytes); Assert.Null(volume.CallerAvailableBytes);
        Assert.Equal("S-1-5-18", volume.QueryCallerSid);
        Assert.Equal(sectors == 0 ? null : (uint?)sectors, volume.SectorsPerAllocationUnit);
        Assert.Equal(bytes == 0 ? null : (uint?)bytes, volume.BytesPerSector);
    }

    [Fact]
    public void SuccessfulZeroIsAValueForActualCallerAndPoolSpace()
    {
        var data = new WindowsVolumeSpaceNativeData { SectorsPerAllocationUnit = 1, BytesPerSector = 512 };
        var fields = Project(WindowsVolumeSpaceInformationReader.Create(IdA, data, "S-1-5-18"));
        foreach (var name in new[] { "volume_all_size_actual", "volume_all_free_bytes_actual", "volume_all_used_bytes_actual",
            "volume_all_user_available_bytes_actual", "volume_all_pool_available_bytes_actual", "volume_all_storage_reserve_bytes_actual" })
            Assert.Equal("0", Field(fields, IdA, name).Value!.Value.GetString());
    }

    [Theory]
    [InlineData(1)] [InlineData(-2147467259)]
    public void FailedHresultDiscardsDirtyValuesOnlyForItsVolume(int status)
    {
        var api = new Api { FirstStatus = status }; var result = new WindowsVolumeSpaceInformationReader(api).Read()!;
        Assert.Equal(new DeviceTopologyVolumeSpaceInformation(IdA), result[0]);
        Assert.Equal((UInt128)4096000, result[1].TotalBytes); Assert.Equal(2, api.Requested.Count);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void SingleVolumeExceptionsDoNotLoseOtherIdentitiesAndReads(int failure)
    {
        var api = new Api { FirstError = failure switch { 0 => new COMException("failed"), 1 => new IOException("failed"),
            2 => new EntryPointNotFoundException("missing"), _ => new UnauthorizedAccessException("denied") } };
        var result = new WindowsVolumeSpaceInformationReader(api).Read()!;
        Assert.Equal(new DeviceTopologyVolumeSpaceInformation(IdA), result[0]); Assert.Equal((UInt128)4096000, result[1].TotalBytes);
    }

    [Fact]
    public void DirectoryFailureIsDifferentFromCompleteEmptyDirectory()
    {
        var api = new Api { Roots = null }; Assert.Null(new WindowsVolumeSpaceInformationReader(api).Read()); Assert.Empty(api.Requested);
        api.Roots = []; Assert.Empty(new WindowsVolumeSpaceInformationReader(api).Read()!);
        api.DirectoryError = new IOException("failed"); Assert.Null(new WindowsVolumeSpaceInformationReader(api).Read());
    }

    [Theory]
    [InlineData(@"C:\")] [InlineData(@"\\server\share\")] [InlineData(@"\\?\Volume{bad}\")]
    [InlineData(@"\\?\Volume{AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE}")]
    [InlineData(@"\\?\Volume{AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE}\directory\")]
    public void InvalidVolumeRootsRejectTheDirectoryBeforeAnySpaceQuery(string root)
    {
        var api = new Api { Roots = [RootA, root] }; Assert.Null(new WindowsVolumeSpaceInformationReader(api).Read()); Assert.Empty(api.Requested);
    }

    [Fact]
    public void CanonicalGuidIdentityIsCaseIndependentAndDuplicateOrOversizedDirectoriesAreRejected()
    {
        var api = new Api { Roots = [RootA.ToLowerInvariant()] };
        Assert.Equal(IdA, Assert.Single(new WindowsVolumeSpaceInformationReader(api).Read()!).DeviceIdentity);
        Assert.Equal(RootA, Assert.Single(api.Requested)); api.Requested.Clear();
        api.Roots = [RootA, RootA.ToLowerInvariant()]; Assert.Null(new WindowsVolumeSpaceInformationReader(api).Read()); Assert.Empty(api.Requested);
        api.Roots = Enumerable.Repeat(RootA, WindowsVolumeSpaceInformationReader.MaximumVolumes + 1).ToArray();
        Assert.Null(new WindowsVolumeSpaceInformationReader(api).Read()); Assert.Empty(api.Requested);
    }

    [Fact]
    public void NativeDirectoryUsesOneHandleAndClosesBeforeTheFirstSpaceQuery()
    {
        var calls = new Calls(); var api = new WindowsVolumeSpaceInformationNativeApi(calls);
        var volumes = new WindowsVolumeSpaceInformationReader(api).Read()!;
        Assert.Equal(2, volumes.Count); Assert.Equal(1, calls.Opens); Assert.Equal(1, calls.Closes);
        Assert.Equal(2, calls.SpaceReads); Assert.True(calls.ClosedBeforeSpace); Assert.All(volumes, volume => Assert.Equal("S-1-5-18", volume.QueryCallerSid));
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public void NativeDirectoryFailureOrExceptionStillClosesItsOpenedHandle(int failure)
    {
        var calls = new Calls { NextError = failure == 0 ? 5 : 18, CloseSucceeds = failure != 1,
            NextException = failure == 2 ? new IOException("failed") : null };
        Assert.Null(new WindowsVolumeSpaceInformationReader(new WindowsVolumeSpaceInformationNativeApi(calls)).Read());
        Assert.Equal(1, calls.Closes); Assert.Equal(0, calls.SpaceReads);
    }

    [Theory]
    [InlineData(18, true)] [InlineData(5, false)]
    public void NativeOpenFailureNeverClosesAnInvalidHandleAndEndMeansEmpty(int error, bool empty)
    {
        var calls = new Calls { Roots = [], NextError = error };
        var result = new WindowsVolumeSpaceInformationReader(new WindowsVolumeSpaceInformationNativeApi(calls)).Read();
        if (empty) Assert.Empty(result!); else Assert.Null(result); Assert.Equal(0, calls.Closes); Assert.Equal(0, calls.SpaceReads);
    }

    [Fact]
    public void InfiniteNativeDirectoryIsBoundedAndClosedWithoutSpaceReads()
    {
        var calls = new Calls { Infinite = true }; Assert.Null(new WindowsVolumeSpaceInformationReader(new WindowsVolumeSpaceInformationNativeApi(calls)).Read());
        Assert.Equal(WindowsVolumeSpaceInformationReader.MaximumVolumes, calls.NextReads); Assert.Equal(1, calls.Closes); Assert.Equal(0, calls.SpaceReads);
    }

    [Fact]
    public void ExactNativeDirectoryLimitCompletesAndStillClosesOnce()
    {
        var calls = new Calls { Roots = Enumerable.Range(0, WindowsVolumeSpaceInformationReader.MaximumVolumes)
            .Select(index => @"\\?\Volume{" + new Guid(index, 0, 0, new byte[8]).ToString("D") + "}\\").ToArray() };
        var result = new WindowsVolumeSpaceInformationNativeApi(calls).EnumerateVolumeRoots();
        Assert.Equal(WindowsVolumeSpaceInformationReader.MaximumVolumes, result!.Count); Assert.Equal(1, calls.Closes);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)]
    public void UnreadableCallerTokenDoesNotDiscardActualNativeSpace(int failure)
    {
        var calls = new Calls { CallerError = failure == 0 ? new SecurityException("denied") : new COMException("token unavailable") };
        var result = new WindowsVolumeSpaceInformationReader(new WindowsVolumeSpaceInformationNativeApi(calls)).Read()!;
        Assert.All(result, volume => { Assert.Null(volume.QueryCallerSid); Assert.Null(volume.CallerAvailableBytes); Assert.Equal((UInt128)4096000, volume.TotalBytes); });
    }

    [Fact]
    public void VolumeProjectionCarriesFifteenExplicitDefinitionsExactStringsAndSemanticChanges()
    {
        var volume = WindowsVolumeSpaceInformationReader.Create(IdA, Sample(), "S-1-5-18"); var fields = Project(volume);
        var device = Assert.Single(fields.Devices, item => item.DeviceIdentity == IdA); Assert.Equal("volume", device.ObjectType); Assert.Equal(22, device.Fields.Count);
        Assert.All(device.Fields, field => { Assert.Equal("volume", field.Definition.Resource); Assert.Equal("all", field.Definition.Vendor); });
        var spaceFields = device.Fields.Where(field => field.Definition.SourceId == "windows.volume-space").ToArray();
        Assert.Equal(15, spaceFields.Length);
        Assert.Equal(7, device.Fields.Count(field => field.Definition.SourceId == "windows.volume-metadata"));
        Assert.Equal("4096000", Field(fields, IdA, "volume_all_size_actual").Value!.Value.GetString());
        Assert.Equal("204800", Field(fields, IdA, "volume_all_user_available_bytes_actual").Value!.Value.GetString());
        Assert.Equal("S-1-5-18", Field(fields, IdA, "volume_all_space_query_caller_sid").Value!.Value.GetString());
        var raw = DeviceInformationTestData.Snapshot() with { VolumeSpaceInformation = [volume] };
        var comparer = new DeviceTopologySemanticComparer(); Assert.Equal(comparer.ComputeHash(raw), comparer.ComputeHash(raw with { CapturedAt = raw.CapturedAt.AddSeconds(1) }));
        Assert.Equal(comparer.ComputeHash(raw), comparer.ComputeHash(raw with { VolumeSpaceInformation = [volume with { AvailableBytes = 0 }] }));
        Assert.Equal(comparer.ComputeHash(raw), comparer.ComputeHash(raw with { VolumeSpaceInformation = [volume with { QueryCallerSid = "S-1-5-19" }] }));
        Assert.NotEqual(comparer.ComputeHash(raw), comparer.ComputeHash(raw with { VolumeSpaceInformation = [volume with { TotalBytes = 8192 }] }));
        var other = volume with { DeviceIdentity = IdB };
        Assert.Equal(comparer.ComputeHash(raw with { VolumeSpaceInformation = [volume, other] }), comparer.ComputeHash(raw with { VolumeSpaceInformation = [other, volume] }));
        var restored = JsonSerializer.Deserialize<DeviceTopologySnapshot>(JsonSerializer.Serialize(raw))!;
        Assert.Equal(volume, Assert.Single(restored.VolumeSpaceInformation!));
        var persisted = DeviceTopologyInformationProjection.Create(raw, null);
        Assert.All(Assert.Single(persisted.Devices, item => item.DeviceIdentity == IdA).Fields, field => Assert.Null(field.Value));
    }

    [Fact]
    public void OfficialSdkCLayoutAndBinarySampleMatchTheManagedApiData()
    {
        var assembly = typeof(VolumeSpaceInformationCollectorTests).Assembly;
        var resource = Assert.Single(assembly.GetManifestResourceNames(), name => name.EndsWith(".Fixtures.Storage.windows-sdk-volume-space.json", StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(resource)!; using var sdk = JsonDocument.Parse(stream);
        Assert.Equal(sdk.RootElement.GetProperty("size").GetInt32(), Marshal.SizeOf<WindowsVolumeSpaceNativeData>());
        string[] names = ["ActualTotalAllocationUnits", "ActualAvailableAllocationUnits", "ActualPoolUnavailableAllocationUnits", "CallerTotalAllocationUnits", "CallerAvailableAllocationUnits",
            "CallerPoolUnavailableAllocationUnits", "UsedAllocationUnits", "TotalReservedAllocationUnits", "VolumeStorageReserveAllocationUnits", "AvailableCommittedAllocationUnits", "PoolAvailableAllocationUnits", "SectorsPerAllocationUnit", "BytesPerSector"];
        var offsets = sdk.RootElement.GetProperty("offsets").EnumerateArray().Select(value => value.GetInt32()).ToArray();
        for (var index = 0; index < names.Length; index++) Assert.Equal(offsets[index], Marshal.OffsetOf<WindowsVolumeSpaceNativeData>(names[index]).ToInt32());
        var bytes = Convert.FromHexString(sdk.RootElement.GetProperty("sampleHex").GetString()!);
        var pointer = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, pointer, bytes.Length); var data = Marshal.PtrToStructure<WindowsVolumeSpaceNativeData>(pointer);
            Assert.Equal(ulong.MaxValue - 17, data.ActualTotalAllocationUnits); Assert.Equal(9007199254740993UL, data.ActualAvailableAllocationUnits);
            Assert.Equal(19UL, data.ActualPoolUnavailableAllocationUnits); Assert.Equal(100UL, data.CallerTotalAllocationUnits);
            Assert.Equal(20UL, data.CallerAvailableAllocationUnits); Assert.Equal(7UL, data.CallerPoolUnavailableAllocationUnits);
            Assert.Equal(31UL, data.UsedAllocationUnits); Assert.Equal(11UL, data.TotalReservedAllocationUnits);
            Assert.Equal(3UL, data.VolumeStorageReserveAllocationUnits); Assert.Equal(2UL, data.AvailableCommittedAllocationUnits);
            Assert.Equal(0UL, data.PoolAvailableAllocationUnits); Assert.Equal(8U, data.SectorsPerAllocationUnit); Assert.Equal(512U, data.BytesPerSector);
        }
        finally { Marshal.FreeHGlobal(pointer); }
    }

    internal static WindowsVolumeSpaceNativeData Sample() => new()
    {
        ActualTotalAllocationUnits = 1000, ActualAvailableAllocationUnits = 250, ActualPoolUnavailableAllocationUnits = 50,
        CallerTotalAllocationUnits = 800, CallerAvailableAllocationUnits = 50, CallerPoolUnavailableAllocationUnits = 50,
        UsedAllocationUnits = 600, TotalReservedAllocationUnits = 100, VolumeStorageReserveAllocationUnits = 30,
        AvailableCommittedAllocationUnits = 75, PoolAvailableAllocationUnits = 100, SectorsPerAllocationUnit = 8, BytesPerSector = 512
    };
    private static DeviceInformationSnapshot Project(DeviceTopologyVolumeSpaceInformation volume)
        => DeviceTopologyInformationProjection.Create(DeviceInformationTestData.Snapshot() with { VolumeSpaceInformation = [volume] }, DateTimeOffset.UtcNow);
    private static DeviceInformationField Field(DeviceInformationSnapshot snapshot, string identity, string name)
        => Assert.Single(Assert.Single(snapshot.Devices, item => item.DeviceIdentity == identity).Fields, field => field.Definition.Name == name);

    internal sealed class Api : IWindowsVolumeSpaceInformationApi
    {
        internal IReadOnlyList<string>? Roots = [RootA, RootB]; internal readonly List<string> Requested = [];
        internal int FirstStatus, Directories; internal Exception? FirstError, DirectoryError;
        internal WindowsVolumeSpaceNativeData Data = Sample(); internal string? Sid = "S-1-5-18";
        public IReadOnlyList<string>? EnumerateVolumeRoots() { Directories++; if (DirectoryError is not null) throw DirectoryError; return Roots; }
        public int ReadSpace(string root, out WindowsVolumeSpaceNativeData space, out string? callerSid)
        { Requested.Add(root); space = Data; callerSid = Sid; if (root == RootA && FirstError is not null) throw FirstError; return root == RootA ? FirstStatus : 0; }
    }
    private sealed class Calls : IWindowsVolumeSpaceNativeCalls
    {
        internal IReadOnlyList<string> Roots = [RootA, RootB]; internal int Opens, Closes, NextReads, SpaceReads, NextError = 18;
        internal bool Infinite, CloseSucceeds = true, ClosedBeforeSpace = true; internal Exception? NextException, CallerError;
        private int index;
        public IntPtr First(StringBuilder root, uint capacity) { Opens++; if (Roots.Count == 0) return new(-1); root.Append(Roots[0]); return new(17); }
        public bool Next(IntPtr handle, StringBuilder root, uint capacity)
        { Assert.Equal(new IntPtr(17), handle); NextReads++; if (NextException is not null) throw NextException; if (NextError != 18) return false; index++; if (!Infinite && index >= Roots.Count) return false; root.Append(Infinite ? RootA : Roots[index]); return true; }
        public bool Close(IntPtr handle) { Assert.Equal(new IntPtr(17), handle); Closes++; return CloseSucceeds; }
        public int LastError() => NextError;
        public int Space(string root, out WindowsVolumeSpaceNativeData value) { SpaceReads++; ClosedBeforeSpace &= Closes == 1; value = Sample(); return 0; }
        public string? CallerSid() { if (CallerError is not null) throw CallerError; return "S-1-5-18"; }
    }
}
