using System.Buffers.Binary;
using System.Text.Json;
using ResourceManager.App.Domain.DeviceTopology;
using ResourceManager.App.Infrastructure.DeviceTopology;
using ResourceManager.App.Infrastructure.DeviceTopology.Snapshots;

namespace Resource_Manager_APP.Tests;

public sealed partial class DiskAlignmentInformationCollectorTests
{
    internal const string Source = "windows.storage-access-alignment";

    [Fact]
    public void OfficialSdkAlignmentFrameAndPropertyQueryHaveExactOffsetsAndSeparateCacheFields()
    {
        using var sdk = Fixture(); var root = sdk.RootElement;
        Assert.Equal(28, root.GetProperty("descriptorSize").GetInt32());
        Assert.Equal(12, root.GetProperty("querySize").GetInt32());
        Assert.Equal(new[] { 0, 4, 8, 12, 16, 20, 24 }, root.GetProperty("offsets").EnumerateArray().Select(item => item.GetInt32()));
        Assert.Equal(WindowsDiskLayoutInformationNativeApi.PropertyIoctl, root.GetProperty("ioctl").GetUInt32());
        Assert.Equal(WindowsDiskLayoutInformationNativeApi.AccessAlignmentProperty, root.GetProperty("property").GetUInt32());
        Assert.Equal(new DiskAccessAlignmentInformation(64, 16, 512, 4096, 1536), WindowsDiskAccessAlignmentDecoder.Decode(Frame()));
        var disk = Disk(); var fields = Project(disk);
        Assert.Equal("64", Value(fields, "storage_all_os_cache_line_size_reported_bytes")!.Value.GetString());
        Assert.Equal("16", Value(fields, "storage_all_os_cache_alignment_offset_reported_bytes")!.Value.GetString());
        Assert.Equal("512", Value(fields, "storage_all_os_logical_sector_size_reported_bytes")!.Value.GetString());
        Assert.Equal("4096", Value(fields, "storage_all_os_physical_sector_size_reported_bytes")!.Value.GetString());
        Assert.Equal("1536", Value(fields, "storage_all_os_physical_sector_offset_reported_bytes")!.Value.GetString());
        var device = Assert.Single(fields.Devices, item => item.DeviceIdentity == disk.DeviceIdentity);
        Assert.Equal("os_disk", device.ObjectType);
        var alignment = device.Fields.Where(item => item.Definition.SourceId == Source).ToArray();
        Assert.Equal(6, alignment.Length); Assert.All(alignment, item => { Assert.Equal("storage", item.Definition.Resource); Assert.Equal("all", item.Definition.Vendor); });
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)] [InlineData(6)]
    public void TruncatedOrContradictoryDescriptorSizesNeverBecomeGeometry(int failure)
    {
        var bytes = Frame();
        switch (failure)
        {
            case 0: bytes = bytes[..27]; break;
            case 1: BinaryPrimitives.WriteUInt32LittleEndian(bytes, 0); break;
            case 2: BinaryPrimitives.WriteUInt32LittleEndian(bytes, 27); break;
            case 3: BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), 27); break;
            case 4: BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), 29); break;
            case 5: BinaryPrimitives.WriteUInt32LittleEndian(bytes, 29); break;
            case 6: bytes = new byte[WindowsDiskAccessAlignmentDecoder.MaximumBytes + 1]; break;
        }
        Assert.Null(WindowsDiskAccessAlignmentDecoder.Decode(bytes));
    }

    [Fact]
    public void CompleteFutureDescriptorPrefixAndRawZeroReportsArePreserved()
    {
        var bytes = new byte[56]; Frame().CopyTo(bytes, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, 56); BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), 56);
        Assert.Equal(WindowsDiskAccessAlignmentDecoder.Decode(Frame()), WindowsDiskAccessAlignmentDecoder.Decode(bytes));
        var zero = WindowsDiskAccessAlignmentDecoder.Decode(Frame("zero"))!;
        Assert.Equal(new DiskAccessAlignmentInformation(0, 0, 0, 0, 0), zero);
        var projected = Project(Disk() with { AccessAlignment = zero });
        foreach (var name in new[] { "storage_all_os_logical_sector_size_reported_bytes", "storage_all_os_physical_sector_size_reported_bytes",
                     "storage_all_os_physical_sector_offset_reported_bytes", "storage_all_os_cache_line_size_reported_bytes", "storage_all_os_cache_alignment_offset_reported_bytes" })
            Assert.Equal("0", Value(projected, name)!.Value.GetString());
        Assert.All(Value(projected, "storage_all_partition_alignment_actual")!.Value.EnumerateArray(), item =>
        {
            Assert.Equal(JsonValueKind.Null, item.GetProperty("logicalStartAligned").ValueKind);
            Assert.Equal(JsonValueKind.Null, item.GetProperty("physicalStartAligned").ValueKind);
            Assert.Equal(JsonValueKind.Null, item.GetProperty("physicalStartRemainderBytes").ValueKind);
        });
    }

    [Theory]
    [InlineData(2560UL, 4096UL, 512U, 4096U, 1536U, "0", "0", true, true)]
    [InlineData(1536UL, 4096UL, 512U, 4096U, 1536U, "3072", "0", false, true)]
    [InlineData(0UL, 4096UL, 512U, 4096U, 1536U, "1536", "0", false, true)]
    [InlineData(1048576UL, 8192UL, 512U, 4096U, 0U, "0", "0", true, true)]
    [InlineData(512UL, 512UL, 512U, 512U, 0U, "0", "0", true, true)]
    [InlineData(4096UL, 4096UL, 4096U, 4096U, 0U, "0", "0", true, true)]
    [InlineData(2560UL, 512UL, 512U, 4096U, 1536U, "0", "512", true, false)]
    [InlineData(9007199254740993UL, 8193UL, 512U, 4096U, 1536U, "1537", "1", false, false)]
    public void ActualAlignmentUsesReportedOffsetWithCorrectDirectionAndExactLargeIntegers(ulong start, ulong length,
        uint logical, uint physical, uint offset, string expectedStart, string expectedLength, bool startAligned, bool lengthAligned)
    {
        var disk = Disk(start, length) with { AccessAlignment = new(0, 0, logical, physical, offset) };
        var item = Assert.Single(Value(Project(disk), "storage_all_partition_alignment_actual")!.Value.EnumerateArray());
        Assert.Equal(7U, item.GetProperty("partitionNumber").GetUInt32());
        Assert.Equal(expectedStart, item.GetProperty("physicalStartRemainderBytes").GetString());
        Assert.Equal(expectedLength, item.GetProperty("physicalLengthRemainderBytes").GetString());
        Assert.Equal(startAligned, item.GetProperty("physicalStartAligned").GetBoolean());
        Assert.Equal(lengthAligned, item.GetProperty("physicalLengthAligned").GetBoolean());
        Assert.Equal(start % logical == 0, item.GetProperty("logicalStartAligned").GetBoolean());
        Assert.Equal(length % logical == 0, item.GetProperty("logicalLengthAligned").GetBoolean());
        Assert.Equal((start % logical).ToString(), item.GetProperty("logicalStartRemainderBytes").GetString());
        Assert.Equal((length % logical).ToString(), item.GetProperty("logicalLengthRemainderBytes").GetString());
    }

    [Theory]
    [InlineData(512U, 0U, 0U)] [InlineData(512U, 256U, 0U)] [InlineData(512U, 768U, 0U)]
    [InlineData(512U, 4096U, 4096U)] [InlineData(512U, 4096U, 1U)] [InlineData(0U, 4096U, 0U)]
    public void ContradictoryPhysicalGeometryIsRawButCannotProduceAnAlignedOrUnalignedClaim(uint logical, uint physical, uint offset)
    {
        var result = Project(Disk() with { AccessAlignment = new(0, uint.MaxValue, logical, physical, offset) });
        Assert.Equal(physical.ToString(), Value(result, "storage_all_os_physical_sector_size_reported_bytes")!.Value.GetString());
        Assert.Equal(offset.ToString(), Value(result, "storage_all_os_physical_sector_offset_reported_bytes")!.Value.GetString());
        var item = Assert.Single(Value(result, "storage_all_partition_alignment_actual")!.Value.EnumerateArray());
        foreach (var name in new[] { "physicalStartRemainderBytes", "physicalLengthRemainderBytes", "physicalStartAligned", "physicalLengthAligned" })
            Assert.Equal(JsonValueKind.Null, item.GetProperty(name).ValueKind);
        if (logical == 0) Assert.Equal(JsonValueKind.Null, item.GetProperty("logicalStartAligned").ValueKind);
        else Assert.True(item.GetProperty("logicalStartAligned").GetBoolean());
    }

    [Fact]
    public void MissingSourcesRemainUnknownWhileCompleteRawLayoutHasNoPartitions()
    {
        Assert.Null(Value(Project(Disk() with { Layout = null }), "storage_all_partition_alignment_actual"));
        Assert.Null(Value(Project(Disk() with { AccessAlignment = null }), "storage_all_partition_alignment_actual"));
        var disk = Disk() with { Layout = WindowsDiskLayoutInformationDecoder.Layout(DiskLayoutInformationCollectorTests.Frame("raw")) };
        Assert.Empty(Value(Project(disk), "storage_all_partition_alignment_actual")!.Value.EnumerateArray());
        disk = Disk() with { Layout = WindowsDiskLayoutInformationDecoder.Layout(DiskLayoutInformationCollectorTests.Frame("mbr")) };
        Assert.Equal(new uint[] { 1, 5 }, Value(Project(disk), "storage_all_partition_alignment_actual")!.Value.EnumerateArray().Select(item => item.GetProperty("partitionNumber").GetUInt32()));
    }

    [Fact]
    public void AlignmentChangesAreSemanticAndPersistedDeclarationsCannotPublishOldGeometry()
    {
        var disk = Disk(); var snapshot = DeviceInformationTestData.Snapshot() with { DiskLayoutInformation = [disk] };
        var comparer = new DeviceTopologySemanticComparer(); var hash = comparer.ComputeHash(snapshot);
        var restored = JsonSerializer.Deserialize<DeviceTopologySnapshot>(JsonSerializer.Serialize(snapshot))!;
        Assert.Equal(hash, comparer.ComputeHash(restored));
        Assert.NotEqual(hash, comparer.ComputeHash(snapshot with { DiskLayoutInformation = [disk with { AccessAlignment = disk.AccessAlignment! with { PhysicalSectorOffsetBytes = 0 } }] }));
        Assert.All(Assert.Single(DeviceTopologyInformationProjection.Create(restored, null).Devices,
            item => item.DeviceIdentity == disk.DeviceIdentity).Fields, item => Assert.Null(item.Value));
    }

    internal static JsonDocument Fixture()
    {
        var assembly = typeof(DiskAlignmentInformationCollectorTests).Assembly;
        var name = Assert.Single(assembly.GetManifestResourceNames(), item => item.EndsWith(".Fixtures.Storage.windows-sdk-disk-alignment.json", StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(name)!; return JsonDocument.Parse(stream);
    }
    internal static byte[] Frame(string key = "alignment") { using var sdk = Fixture(); return Convert.FromBase64String(sdk.RootElement.GetProperty(key).GetString()!); }
    internal static DeviceTopologyDiskLayoutInformation Disk(ulong start = 2560, ulong length = 4096) => new(DiskLayoutInformationCollectorTests.IdA)
    {
        OsDiskNumberReported = 2, AccessAlignment = WindowsDiskAccessAlignmentDecoder.Decode(Frame()),
        Layout = new(DiskPartitionStyle.Mbr, new(0, 0), null, [new(7, start, length, false, false, new(7, false, true, 0, Guid.Empty), null)])
    };
    internal static DeviceInformationSnapshot Project(DeviceTopologyDiskLayoutInformation disk) => DiskLayoutInformationCollectorTests.Project([disk], []);
    internal static JsonElement? Value(DeviceInformationSnapshot fields, string name) => DiskLayoutInformationCollectorTests.Field(fields, DiskLayoutInformationCollectorTests.IdA, name).Value;
}
