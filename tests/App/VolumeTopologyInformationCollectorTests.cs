using System.Buffers.Binary;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using ResourceManager.App.Domain.DeviceTopology;
using ResourceManager.App.Infrastructure.DeviceTopology;
using ResourceManager.App.Infrastructure.DeviceTopology.Snapshots;

namespace Resource_Manager_APP.Tests;

public sealed class VolumeTopologyInformationCollectorTests
{
    internal static DeviceTopologyVolumeDiskExtent[] SampleExtents() => [new(2, 9007199254740993, 4096), new(5, 1048576, 8192), new(2, 4096, 4096)];
    internal static DeviceTopologyVolumeFileSystemInformation SampleFileSystem() => new("NTFS", "  数据  ", 0x89abcdef, 255, 0x80000000);

    [Fact]
    public void OneGuidDirectoryFeedsIndependentMetadataWithoutGuessingPhysicalDiskIdentity()
    {
        var space = new VolumeSpaceInformationCollectorTests.Api(); var metadata = new Api();
        var values = new WindowsVolumeSpaceInformationReader(space, metadata).Read()!;
        Assert.Equal(1, space.Directories); Assert.Equal(2, space.Requested.Count);
        Assert.Equal(6, metadata.Requests.Count); Assert.All(metadata.Requests, root => Assert.Contains(root, space.Requested));
        Assert.All(values, value => { Assert.Equal((UInt128)4096000, value.TotalBytes); Assert.Equal(SampleExtents(), value.DiskExtents); Assert.Equal(SampleFileSystem(), value.FileSystem); });
        space.FirstStatus = 1; var failedSpace = new WindowsVolumeSpaceInformationReader(space, metadata).Read()![0];
        Assert.Null(failedSpace.TotalBytes); Assert.Equal(SampleExtents(), failedSpace.DiskExtents); Assert.Equal(SampleFileSystem(), failedSpace.FileSystem);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public void FailedSourceIsEmptyWhileOtherGroupsAndNextVolumeContinue(int group)
    {
        var metadata = new Api { FailureGroup = group };
        var values = new WindowsVolumeSpaceInformationReader(new VolumeSpaceInformationCollectorTests.Api(), metadata).Read()!;
        Assert.Equal(6, metadata.Requests.Count);
        var first = values[0]; var second = values[1];
        Assert.Equal(group == 0, first.DiskExtents is null); Assert.Equal(group == 1, first.MountPaths is null); Assert.Equal(group == 2, first.FileSystem is null);
        Assert.Equal((UInt128)4096000, first.TotalBytes); Assert.NotNull(second.DiskExtents); Assert.NotNull(second.MountPaths); Assert.NotNull(second.FileSystem);
    }

    [Fact]
    public void CompleteDirectoryValidationPrecedesEveryMetadataCall()
    {
        var space = new VolumeSpaceInformationCollectorTests.Api { Roots = [VolumeSpaceInformationCollectorTests.RootA, @"C:\"] };
        var metadata = new Api(); Assert.Null(new WindowsVolumeSpaceInformationReader(space, metadata).Read()); Assert.Empty(metadata.Requests);
        space.Roots = null; Assert.Null(new WindowsVolumeSpaceInformationReader(space, metadata).Read()); Assert.Empty(metadata.Requests);
        space.Roots = []; Assert.Empty(new WindowsVolumeSpaceInformationReader(space, metadata).Read()!); Assert.Empty(metadata.Requests);
    }

    [Fact]
    public void ExtentGrowthReadsOnlyCompleteSuccessfulFrameAndClosesHandle()
    {
        var calls = new Calls { GrowExtents = true }; var api = new WindowsVolumeMetadataInformationNativeApi(calls);
        var result = api.ReadDiskExtents(VolumeSpaceInformationCollectorTests.RootA)!;
        Assert.Equal(SampleExtents().OrderBy(item => item.DiskNumberReported).ThenBy(item => item.StartingOffsetBytes), result);
        Assert.Equal(new[] { 32, 80 }, calls.ExtentCapacities); Assert.True(calls.Handle!.IsClosed);
        Assert.Equal(VolumeSpaceInformationCollectorTests.RootA[..^1], calls.OpenPath); Assert.Equal(1, calls.Opens);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)]
    public void DirtyFailedOrMalformedExtentFramesNeverEscapeAndHandleCloses(int failure)
    {
        var calls = new Calls { ExtentFailure = failure };
        Assert.Null(new WindowsVolumeMetadataInformationNativeApi(calls).ReadDiskExtents(VolumeSpaceInformationCollectorTests.RootA));
        Assert.True(calls.Handle!.IsClosed); Assert.Single(calls.ExtentCapacities);
    }

    [Fact]
    public void ThrowingExtentCallStillClosesAndInvalidOpenDoesNotQuery()
    {
        var calls = new Calls { ExtentError = new IOException("failed") };
        Assert.Throws<IOException>(() => new WindowsVolumeMetadataInformationNativeApi(calls).ReadDiskExtents(VolumeSpaceInformationCollectorTests.RootA));
        Assert.True(calls.Handle!.IsClosed);
        calls = new Calls { InvalidHandle = true };
        Assert.Null(new WindowsVolumeMetadataInformationNativeApi(calls).ReadDiskExtents(VolumeSpaceInformationCollectorTests.RootA));
        Assert.Empty(calls.ExtentCapacities); Assert.True(calls.Handle!.IsClosed);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void InvalidSignedRangesDiskNumberAndTruncatedArraysAreRejected(int failure)
    {
        var bytes = ExtentFrame([new(2, 4096, 4096)]);
        if (failure == 0) BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(16), -1);
        if (failure == 1) BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(24), 0);
        if (failure == 2) BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), uint.MaxValue);
        if (failure == 3) bytes = bytes[..^1];
        Assert.Null(WindowsVolumeMetadataInformationDecoder.Extents(bytes));
    }

    [Fact]
    public void SignedRangeEndOverflowIsInvalidAndZeroCountIsAnEmptyList()
    {
        Assert.Null(WindowsVolumeMetadataInformationDecoder.Extents(ExtentFrame([new(1, long.MaxValue, 1)])));
        Assert.Empty(WindowsVolumeMetadataInformationDecoder.Extents(new byte[8])!);
        var huge = new byte[8]; BinaryPrimitives.WriteUInt32LittleEndian(huge, uint.MaxValue);
        Assert.Null(WindowsVolumeMetadataInformationDecoder.Extents(huge));
        Assert.Null(WindowsVolumeMetadataInformationDecoder.Extents(new byte[7]));
    }

    [Fact]
    public void ContinuouslyChangingExtentDirectoryIsBoundedAndClosed()
    {
        var calls = new Calls { InfiniteExtentGrowth = true };
        Assert.Null(new WindowsVolumeMetadataInformationNativeApi(calls).ReadDiskExtents(VolumeSpaceInformationCollectorTests.RootA));
        Assert.Equal(WindowsVolumeMetadataInformationNativeApi.MaximumAttempts, calls.ExtentCapacities.Count); Assert.True(calls.Handle!.IsClosed);
    }

    [Fact]
    public void MountPathsGrowOnceAndKeepDirectoryMountsWithoutTrimming()
    {
        var calls = new Calls { GrowPaths = true, Paths = "D:\\folder with space\\\0D:\\\0\0" };
        var result = new WindowsVolumeMetadataInformationNativeApi(calls).ReadMountPaths(VolumeSpaceInformationCollectorTests.RootA);
        Assert.Equal(new[] { @"D:\", @"D:\folder with space\" }, result); Assert.Equal(new[] { 512, 1024 }, calls.PathCapacities);
        Assert.Equal(0, calls.Opens);
    }

    [Theory]
    [InlineData("\0")] [InlineData("\0\0")]
    public void SuccessfulUnmountedVolumeKeepsEmptyPathList(string paths)
    {
        Assert.Empty(new WindowsVolumeMetadataInformationNativeApi(new Calls { Paths = paths }).ReadMountPaths(VolumeSpaceInformationCollectorTests.RootA)!);
    }

    [Theory]
    [InlineData("")] [InlineData("D:\\")] [InlineData("D:\\\0")] [InlineData("D:\\\0\0junk")]
    public void MalformedMultiSzIsNotPublished(string paths)
    {
        Assert.Null(new WindowsVolumeMetadataInformationNativeApi(new Calls { Paths = paths }).ReadMountPaths(VolumeSpaceInformationCollectorTests.RootA));
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void FailedDirtyOversizedNonGrowingOrEndlessMountPathsAreRejected(int failure)
    {
        var calls = new Calls { PathFailure = failure };
        Assert.Null(new WindowsVolumeMetadataInformationNativeApi(calls).ReadMountPaths(VolumeSpaceInformationCollectorTests.RootA));
        Assert.Equal(failure == 3 ? WindowsVolumeMetadataInformationNativeApi.MaximumAttempts : 1, calls.PathCapacities.Count);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void FilesystemQueriesSuppressCriticalDialogsOnlyOnTheirThreadAndRestoreMode(int failure)
    {
        var calls = new Calls { FileSystemFailure = failure };
        var api = new WindowsVolumeMetadataInformationNativeApi(calls);
        if (failure == 2) Assert.Throws<IOException>(() => api.ReadFileSystem(VolumeSpaceInformationCollectorTests.RootA));
        else if (failure == 0) Assert.Equal(SampleFileSystem(), api.ReadFileSystem(VolumeSpaceInformationCollectorTests.RootA));
        else Assert.Null(api.ReadFileSystem(VolumeSpaceInformationCollectorTests.RootA));
        Assert.Equal(new uint[] { 0x8001, 0x8000 }, calls.Modes); Assert.Equal(1, calls.FileSystemReads);
        Assert.True(calls.SuppressedDuringFileSystem);
    }

    [Fact]
    public void ErrorModeSetupFailureDoesNotEnterPotentiallyModalFilesystemCall()
    {
        var calls = new Calls { SetModeFailure = true };
        Assert.Null(new WindowsVolumeMetadataInformationNativeApi(calls).ReadFileSystem(VolumeSpaceInformationCollectorTests.RootA));
        Assert.Equal(0, calls.FileSystemReads); Assert.Single(calls.Modes);
    }

    [Fact]
    public void InvalidRootDoesNotOpenQueryOrChangeThreadMode()
    {
        var calls = new Calls(); var api = new WindowsVolumeMetadataInformationNativeApi(calls);
        Assert.Null(api.ReadDiskExtents(@"D:\")); Assert.Null(api.ReadMountPaths(@"D:\")); Assert.Null(api.ReadFileSystem(@"D:\"));
        Assert.Equal(0, calls.Opens); Assert.Empty(calls.PathCapacities); Assert.Empty(calls.Modes);
    }

    [Fact]
    public void MetadataProjectionPreservesBytePrecisionBlankLabelsZeroFlagsAndFormatSerial()
    {
        var volume = new DeviceTopologyVolumeSpaceInformation(VolumeSpaceInformationCollectorTests.IdA)
            { DiskExtents = SampleExtents(), MountPaths = [], FileSystem = new("NTFS", "", 0, 0, 0) };
        var projection = Project(volume); var extent = Field(projection, "volume_all_disk_extents_reported").Value!.Value[0];
        Assert.Equal(2U, extent.GetProperty("diskNumberReported").GetUInt32()); Assert.Equal("9007199254740993", extent.GetProperty("startingOffsetBytes").GetString());
        Assert.Equal("4096", extent.GetProperty("extentLengthBytes").GetString());
        Assert.Equal(0, Field(projection, "volume_all_mount_paths_reported").Value!.Value.GetArrayLength());
        Assert.Equal("", Field(projection, "volume_all_label_configured").Value!.Value.GetString());
        Assert.Equal("00000000", Field(projection, "volume_all_filesystem_serial_reported").Value!.Value.GetString());
        Assert.Equal("0", Field(projection, "volume_all_filesystem_flags_reported").Value!.Value.GetString());
        Assert.Equal("0", Field(projection, "volume_all_maximum_component_length_reported").Value!.Value.GetString());
        volume = volume with { FileSystem = SampleFileSystem() }; projection = Project(volume);
        Assert.Equal("  数据  ", Field(projection, "volume_all_label_configured").Value!.Value.GetString());
        Assert.Equal("89ABCDEF", Field(projection, "volume_all_filesystem_serial_reported").Value!.Value.GetString());
        Assert.Equal("2147483648", Field(projection, "volume_all_filesystem_flags_reported").Value!.Value.GetString());
        Assert.All(Assert.Single(projection.Devices, item => item.DeviceIdentity == volume.DeviceIdentity).Fields.Where(field => field.Definition.SourceId == "windows.volume-metadata"),
            field => { Assert.Equal("all", field.Definition.Vendor); Assert.Equal("volume", field.Definition.Resource); });
    }

    [Fact]
    public void MetadataOrderingDoesNotChangeSemanticVersionButRealChangesDoAndPersistenceDoesNotPublishValues()
    {
        var volume = new DeviceTopologyVolumeSpaceInformation(VolumeSpaceInformationCollectorTests.IdA)
            { DiskExtents = SampleExtents(), MountPaths = [@"D:\", @"C:\mnt\"], FileSystem = SampleFileSystem() };
        var snapshot = DeviceInformationTestData.Snapshot() with { VolumeSpaceInformation = [volume] };
        var comparer = new DeviceTopologySemanticComparer(); var hash = comparer.ComputeHash(snapshot);
        Assert.Equal(hash, comparer.ComputeHash(snapshot with { VolumeSpaceInformation = [volume with { DiskExtents = volume.DiskExtents.Reverse().ToArray(), MountPaths = volume.MountPaths.Reverse().ToArray() }] }));
        Assert.NotEqual(hash, comparer.ComputeHash(snapshot with { VolumeSpaceInformation = [volume with { FileSystem = volume.FileSystem with { FlagsReported = 0 } }] }));
        var restored = JsonSerializer.Deserialize<DeviceTopologySnapshot>(JsonSerializer.Serialize(snapshot))!;
        Assert.Equal(hash, comparer.ComputeHash(restored));
        var initial = DeviceTopologyInformationProjection.Create(restored, null);
        Assert.All(Assert.Single(initial.Devices, item => item.DeviceIdentity == volume.DeviceIdentity).Fields, field => Assert.Null(field.Value));
    }

    [Fact]
    public void OfficialSdkCompiledExtentFixtureMatchesOffsetsControlCodeAndExactValues()
    {
        var assembly = typeof(VolumeTopologyInformationCollectorTests).Assembly;
        var resource = Assert.Single(assembly.GetManifestResourceNames(), name => name.EndsWith(".Fixtures.Storage.windows-sdk-volume-extents.json", StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(resource)!; using var sdk = JsonDocument.Parse(stream);
        Assert.Equal(8, sdk.RootElement.GetProperty("extentOffset").GetInt32()); Assert.Equal(24, sdk.RootElement.GetProperty("extentSize").GetInt32());
        Assert.Equal(WindowsVolumeMetadataInformationNativeApi.ExtentsIoctl, sdk.RootElement.GetProperty("ioctl").GetUInt32());
        var result = WindowsVolumeMetadataInformationDecoder.Extents(Convert.FromBase64String(sdk.RootElement.GetProperty("sample").GetString()!));
        Assert.Equal(new[] { new DeviceTopologyVolumeDiskExtent(2, 9007199254740993, 4096), new DeviceTopologyVolumeDiskExtent(5, 1048576, 8192) }, result);
    }

    private static DeviceInformationSnapshot Project(DeviceTopologyVolumeSpaceInformation volume)
        => DeviceTopologyInformationProjection.Create(DeviceInformationTestData.Snapshot() with { VolumeSpaceInformation = [volume] }, DateTimeOffset.UtcNow);
    private static DeviceInformationField Field(DeviceInformationSnapshot value, string name)
        => Assert.Single(Assert.Single(value.Devices, item => item.DeviceIdentity == VolumeSpaceInformationCollectorTests.IdA).Fields, field => field.Definition.Name == name);

    internal static byte[] ExtentFrame(IReadOnlyList<DeviceTopologyVolumeDiskExtent> extents)
    {
        var frame = new byte[8 + extents.Count * 24]; BinaryPrimitives.WriteUInt32LittleEndian(frame, (uint)extents.Count);
        for (var index = 0; index < extents.Count; index++)
        {
            var item = frame.AsSpan(8 + index * 24); var extent = extents[index]; BinaryPrimitives.WriteUInt32LittleEndian(item, extent.DiskNumberReported);
            BinaryPrimitives.WriteUInt64LittleEndian(item[8..], extent.StartingOffsetBytes); BinaryPrimitives.WriteUInt64LittleEndian(item[16..], extent.ExtentLengthBytes);
        }
        return frame;
    }

    internal sealed class Api : IWindowsVolumeMetadataInformationApi
    {
        internal int FailureGroup = -1;
        internal List<string> Requests = [];
        public IReadOnlyList<DeviceTopologyVolumeDiskExtent>? ReadDiskExtents(string root) { Requests.Add(root); Fail(root, 0); return SampleExtents(); }
        public IReadOnlyList<string>? ReadMountPaths(string root) { Requests.Add(root); Fail(root, 1); return [@"D:\", @"C:\mnt\"]; }
        public DeviceTopologyVolumeFileSystemInformation? ReadFileSystem(string root) { Requests.Add(root); Fail(root, 2); return SampleFileSystem(); }
        private void Fail(string root, int group) { if (root == VolumeSpaceInformationCollectorTests.RootA && group == FailureGroup) throw new IOException("unavailable"); }
    }

    private sealed class Calls : IWindowsVolumeMetadataNativeCalls
    {
        internal SafeFileHandle? Handle;
        internal int Opens, FileSystemReads;
        internal string? OpenPath;
        internal bool InvalidHandle, GrowExtents, InfiniteExtentGrowth, GrowPaths, SetModeFailure, SuppressedDuringFileSystem;
        internal int ExtentFailure = -1, PathFailure = -1, FileSystemFailure;
        internal Exception? ExtentError;
        internal string Paths = "D:\\\0\0";
        internal List<int> ExtentCapacities = [], PathCapacities = [];
        internal List<uint> Modes = [];
        private uint mode = 0x8000;
        private int error = 234;
        public SafeFileHandle OpenVolume(string path) { Opens++; OpenPath = path; return Handle = new SafeFileHandle(new IntPtr(InvalidHandle ? -1 : 42), false); }
        public bool Extents(SafeFileHandle handle, byte[] buffer, out uint returnedBytes)
        {
            Assert.Same(Handle, handle); Assert.False(handle.IsClosed); ExtentCapacities.Add(buffer.Length);
            if (ExtentError is not null) throw ExtentError;
            var frame = ExtentFrame(SampleExtents()); Array.Copy(frame, buffer, Math.Min(frame.Length, buffer.Length)); returnedBytes = (uint)Math.Min(buffer.Length, frame.Length);
            if (ExtentFailure >= 0)
            {
                if (ExtentFailure == 0) { error = 5; return false; }
                if (ExtentFailure == 1) { returnedBytes = 4; return false; }
                if (ExtentFailure == 2) { BinaryPrimitives.WriteUInt32LittleEndian(buffer, uint.MaxValue); return false; }
                if (ExtentFailure == 3) { returnedBytes = (uint)buffer.Length + 1; return true; }
                if (ExtentFailure == 4) { BinaryPrimitives.WriteUInt32LittleEndian(buffer, 1); return false; }
                return true; // Success cannot certify the truncated three-item frame.
            }
            if (InfiniteExtentGrowth) { BinaryPrimitives.WriteUInt32LittleEndian(buffer, (uint)ExtentCapacities.Count + 1); return false; }
            return !GrowExtents || buffer.Length >= frame.Length;
        }
        public bool MountPaths(string root, char[] buffer, out uint returnedCharacters)
        {
            Assert.Equal(VolumeSpaceInformationCollectorTests.RootA, root); PathCapacities.Add(buffer.Length);
            Paths.AsSpan(0, Math.Min(Paths.Length, buffer.Length)).CopyTo(buffer); returnedCharacters = (uint)Paths.Length;
            if (PathFailure >= 0)
            {
                if (PathFailure == 0) { error = 5; return false; }
                returnedCharacters = PathFailure == 1 ? 65537U : PathFailure == 2 ? (uint)buffer.Length : (uint)buffer.Length + 1;
                return false;
            }
            if (GrowPaths && buffer.Length == 512) { returnedCharacters = 1024; return false; }
            return true;
        }
        public bool FileSystem(string root, out DeviceTopologyVolumeFileSystemInformation? value)
        {
            FileSystemReads++; SuppressedDuringFileSystem = mode == 0x8001; value = SampleFileSystem();
            if (FileSystemFailure == 2) throw new IOException("failed");
            return FileSystemFailure != 1;
        }
        public int LastError() => error;
        public uint ThreadErrorMode() => mode;
        public bool SetThreadErrorMode(uint next, out uint previous)
        { Modes.Add(next); previous = mode; if (SetModeFailure || (FileSystemFailure == 3 && Modes.Count == 2)) return false; mode = next; return true; }
    }
}
