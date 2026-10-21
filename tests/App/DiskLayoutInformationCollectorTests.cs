using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using ResourceManager.App.Domain.DeviceTopology;
using ResourceManager.App.Infrastructure.DeviceTopology;
using ResourceManager.App.Infrastructure.DeviceTopology.Snapshots;

namespace Resource_Manager_APP.Tests;

public sealed partial class DiskLayoutInformationCollectorTests
{
    internal const string IdA = @"windows-pnp|SCSI\DISK&A";
    internal const string IdB = @"windows-pnp|SCSI\DISK&B";
    internal static readonly WindowsDiskInterface DiskA = new(@"\\?\disk-interface-a", @"SCSI\DISK&A");
    internal static readonly WindowsDiskInterface DiskB = new(@"\\?\disk-interface-b", @"SCSI\DISK&B");

    [Fact]
    public void OfficialSdkFramesDecodeExactGptUnionFullNameAndUnsignedAttributes()
    {
        using var fixture = Fixture(); var sdk=fixture.RootElement;
        Assert.Equal(48,sdk.GetProperty("headerSize").GetInt32()); Assert.Equal(144,sdk.GetProperty("partitionSize").GetInt32());
        Assert.Equal(32,sdk.GetProperty("partitionUnionOffset").GetInt32()); Assert.Equal(72,sdk.GetProperty("gptNameOffset").GetInt32()); Assert.Equal(12,sdk.GetProperty("numberSize").GetInt32());
        Assert.Equal(WindowsDiskLayoutInformationNativeApi.LayoutIoctl,sdk.GetProperty("layoutIoctl").GetUInt32()); Assert.Equal(WindowsDiskLayoutInformationNativeApi.NumberIoctl,sdk.GetProperty("numberIoctl").GetUInt32());
        var result=WindowsDiskLayoutInformationDecoder.Layout(Frame("gpt"))!;
        Assert.Equal(DiskPartitionStyle.Gpt,result.Style);Assert.Null(result.Mbr); Assert.Equal(Guid.Parse("01020304-0506-0708-090a-0b0c0d0e0f10"),result.Gpt!.DiskId);
        Assert.Equal(4096UL,result.Gpt.StartingUsableOffsetBytes);Assert.Equal(128U,result.Gpt.MaximumPartitionCount);
        Assert.Equal(new uint[]{1,2},result.Partitions.Select(item=>item.Number));
        Assert.Equal(" 数据 ",result.Partitions[0].Gpt!.Name);Assert.True(result.Partitions[0].IsServiceReported);
        Assert.Equal(9007199254740993UL,result.Partitions[1].StartingOffsetBytes);Assert.Equal(ulong.MaxValue,result.Partitions[1].Gpt!.Attributes);
        Assert.Equal(new string('X',36),result.Partitions[1].Gpt!.Name);Assert.Null(result.Partitions[1].Mbr);
    }

    [Fact]
    public void MbrUnusedSlotsAreAbsentButExtendedContainerAndLogicalPartitionRemainDistinct()
    {
        var layout=WindowsDiskLayoutInformationDecoder.Layout(Frame("mbr"))!;
        Assert.Equal(DiskPartitionStyle.Mbr,layout.Style);Assert.Null(layout.Gpt);Assert.Equal(0U,layout.Mbr!.Signature);Assert.Equal(0x89abcdefU,layout.Mbr.Checksum);
        Assert.Equal(2,layout.Partitions.Count);Assert.Equal((byte)0x0f,layout.Partitions[0].Mbr!.Type);Assert.Equal(5U,layout.Partitions[1].Number);
        Assert.True(layout.Partitions[1].Mbr!.BootIndicator);Assert.True(layout.Partitions[1].Mbr!.Recognized);Assert.Equal(4096U,layout.Partitions[1].Mbr!.HiddenSectors);
        Assert.All(layout.Partitions,item=>Assert.Null(item.Gpt));
        var raw=WindowsDiskLayoutInformationDecoder.Layout(Frame("raw"))!;Assert.Equal(DiskPartitionStyle.Raw,raw.Style);Assert.Empty(raw.Partitions);Assert.Null(raw.Mbr);Assert.Null(raw.Gpt);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)] [InlineData(6)] [InlineData(7)] [InlineData(8)]
    public void InvalidTruncatedRangesStylesCountsDuplicateNumbersAndUtf16RejectWholeLayout(int failure)
    {
        var bytes=Frame("gpt");
        switch(failure)
        {
            case 0: bytes=bytes[..^1];break;
            case 1: BinaryPrimitives.WriteUInt32LittleEndian(bytes,77);break;
            case 2: BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4),uint.MaxValue);break;
            case 3: BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(56),-1);break;
            case 4: BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(64),0);break;
            case 5: BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(48),0);break;
            case 6: BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(216),2);break;
            case 7: bytes[120]=0;bytes[121]=0xd8;break;
            case 8: bytes[76]=2;break;
        }
        Assert.Null(WindowsDiskLayoutInformationDecoder.Layout(bytes));
    }

    [Fact]
    public void SignedEndOverflowAndShortHeaderAreInvalidWhileEmptyLayoutKeepsZero()
    {
        var bytes=Frame("gpt"); BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(56),long.MaxValue); Assert.Null(WindowsDiskLayoutInformationDecoder.Layout(bytes));
        Assert.Null(WindowsDiskLayoutInformationDecoder.Layout(new byte[47]));
        var empty=Frame("mbr")[..48];BinaryPrimitives.WriteUInt32LittleEndian(empty.AsSpan(4),0);
        Assert.Empty(WindowsDiskLayoutInformationDecoder.Layout(empty)!.Partitions);
    }

    [Theory]
    [InlineData(0U,0U)] [InlineData(9U,0U)] [InlineData(9U,4294967295U)]
    public void WholeDiskNumbersAreNotMsftPhysicalDiskIndices(uint number,uint partition)
    {
        var bytes=NumberFrame(number,partition);Assert.Equal(number,WindowsDiskLayoutInformationDecoder.DiskNumber(bytes));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes,2);Assert.Null(WindowsDiskLayoutInformationDecoder.DiskNumber(bytes));
        Assert.Null(WindowsDiskLayoutInformationDecoder.DiskNumber(NumberFrame(uint.MaxValue,0)));
        Assert.Null(WindowsDiskLayoutInformationDecoder.DiskNumber(NumberFrame(number,1)));
        Assert.Null(WindowsDiskLayoutInformationDecoder.DiskNumber(bytes[..11]));
    }

    [Fact]
    public void PnpDirectoryValidationPrecedesAllOpensAndFailuresDoNotLoseOtherDisks()
    {
        var api=new Api();var reader=new WindowsDiskLayoutInformationReader(api);var result=reader.Read()!;
        Assert.Equal(new[]{IdA,IdB},result.Select(item=>item.DeviceIdentity));Assert.Equal(new[]{DiskA,DiskB},api.Reads);
        api.Reads.Clear();api.Directory=[DiskA,DiskA with {PnpInstanceId=DiskA.PnpInstanceId.ToLowerInvariant()}];Assert.Null(reader.Read());Assert.Empty(api.Reads);
        api.Directory=[DiskA,DiskB with {DevicePath=DiskA.DevicePath}];Assert.Null(reader.Read());Assert.Empty(api.Reads);
        api.Directory=[DiskA,DiskB with {PnpInstanceId=""}];Assert.Null(reader.Read());Assert.Empty(api.Reads);
        api.Directory=Enumerable.Repeat(DiskA,WindowsDiskLayoutInformationReader.MaximumDisks+1).ToArray();Assert.Null(reader.Read());Assert.Empty(api.Reads);
        api.Directory=null;Assert.Null(reader.Read());api.Directory=[];Assert.Empty(reader.Read()!);
        api.Directory=[DiskA,DiskB];api.FailFirst=true;result=reader.Read()!;Assert.Equal(new DeviceTopologyDiskLayoutInformation(IdA),result[0]);Assert.NotNull(result[1].Layout);
    }

    [Fact]
    public void LayoutAndRelationsProjectLosslessIntegersWithoutAssumingOneToOneOrVolumeOrder()
    {
        var disk=new DeviceTopologyDiskLayoutInformation(IdA){OsDiskNumberReported=2,Layout=WindowsDiskLayoutInformationDecoder.Layout(Frame("mbr"))};
        var volume=new DeviceTopologyVolumeSpaceInformation(VolumeSpaceInformationCollectorTests.IdA){DiskExtents=[new(2,2097152,2097152),new(5,0,8192),new(2,1048576,1048576)]};
        var projection=Project([disk],[volume]);var relations=Field(projection,IdA,"storage_all_partition_relation_actual").Value!.Value;
        Assert.Equal(3,relations.GetArrayLength());Assert.Equal(1U,relations[0].GetProperty("partitionNumber").GetUInt32());Assert.False(relations[0].GetProperty("rangesEqual").GetBoolean());
        Assert.Equal(5U,relations[2].GetProperty("partitionNumber").GetUInt32());Assert.True(relations[2].GetProperty("rangesEqual").GetBoolean());
        Assert.Equal("2097152",relations[2].GetProperty("overlapLengthBytes").GetString());
        Assert.Equal(2,Field(projection,IdA,"storage_all_volume_relation_actual").Value!.Value.GetArrayLength());
        Assert.Equal("00000000",Field(projection,IdA,"storage_all_mbr_disk_signature_reported").Value!.Value.GetString());
        disk=disk with {Layout=WindowsDiskLayoutInformationDecoder.Layout(Frame("gpt"))};projection=Project([disk],[volume]);
        var entry=Field(projection,IdA,"storage_all_partition_layout_reported").Value!.Value[1];
        Assert.Equal("9007199254740993",entry.GetProperty("startingOffsetBytes").GetString());Assert.Equal("18446744073709551615",entry.GetProperty("gpt").GetProperty("attributes").GetString());
        Assert.Null(Field(projection,IdA,"storage_all_mbr_disk_signature_reported").Value);
    }

    [Fact]
    public void UnavailableVolumeExtentDirectoryOrDuplicateNumbersAreUnknownButCompleteEmptyIsEmpty()
    {
        var disk=new DeviceTopologyDiskLayoutInformation(IdA){OsDiskNumberReported=2,Layout=WindowsDiskLayoutInformationDecoder.Layout(Frame("gpt"))};
        foreach(var volumes in new IReadOnlyList<DeviceTopologyVolumeSpaceInformation>?[]{null,[new(VolumeSpaceInformationCollectorTests.IdA)]})
        {
            var value=Project([disk],volumes);Assert.Null(Field(value,IdA,"storage_all_volume_relation_actual").Value);Assert.Null(Field(value,IdA,"storage_all_partition_relation_actual").Value);
            Assert.NotNull(Field(value,IdA,"storage_all_partition_layout_reported").Value);
        }
        var complete=Project([disk],[]);Assert.Equal(0,Field(complete,IdA,"storage_all_partition_relation_actual").Value!.Value.GetArrayLength());
        var duplicate=Project([disk,disk with {DeviceIdentity=IdB}],[]);Assert.Null(Field(duplicate,IdA,"storage_all_volume_relation_actual").Value);
    }

    [Fact]
    public void RelationWorkBoundsReturnUnknownInsteadOfPartialMatches()
    {
        var partition=new DiskLayoutPartition(1,0,4096,false,false,new(7,false,true,0,Guid.Empty),null);
        var disk=new DeviceTopologyDiskLayoutInformation(IdA){OsDiskNumberReported=2,Layout=new(DiskPartitionStyle.Mbr,new(0,0),null,Enumerable.Repeat(partition,2000).ToArray())};
        var volume=new DeviceTopologyVolumeSpaceInformation(VolumeSpaceInformationCollectorTests.IdA){DiskExtents=Enumerable.Repeat(new DeviceTopologyVolumeDiskExtent(2,0,4096),1000).ToArray()};
        Assert.Null(Field(Project([disk],[volume]),IdA,"storage_all_partition_relation_actual").Value);
        volume=volume with {DiskExtents=Enumerable.Repeat(new DeviceTopologyVolumeDiskExtent(2,0,4096),WindowsInformationFieldProjection.MaximumDiskRelationExtents+1).ToArray()};
        Assert.Null(Field(Project([disk],[volume]),IdA,"storage_all_volume_relation_actual").Value);
    }

    [Fact]
    public void SamePnpPortFieldsMergeIntoOneDiskObjectAndOrderIsSemanticNeutral()
    {
        var raw=DeviceInformationTestData.Snapshot(DeviceInformationTestData.Port(@"PCI\LAYOUT"));var port=raw.Ports[0];var id="windows-pnp|"+port.DeviceId.Trim().ToUpperInvariant();
        var disk=new DeviceTopologyDiskLayoutInformation(id){OsDiskNumberReported=2,Layout=WindowsDiskLayoutInformationDecoder.Layout(Frame("gpt"))};
        var snapshot=raw with {DiskLayoutInformation=[disk],VolumeSpaceInformation=[]};var fields=DeviceTopologyInformationProjection.Create(snapshot,DateTimeOffset.UtcNow);
        var device=Assert.Single(fields.Devices,item=>item.DeviceIdentity==id);Assert.Equal("os_disk",device.ObjectType);
        Assert.Contains(device.Fields,item=>item.Definition.Name=="storage_all_partition_layout_reported");Assert.Contains(device.Fields,item=>item.Definition.SourceId!="windows.disk-layout");
        var comparer=new DeviceTopologySemanticComparer();var hash=comparer.ComputeHash(snapshot);
        Assert.Equal(hash,comparer.ComputeHash(snapshot with {DiskLayoutInformation=[disk with {Layout=disk.Layout! with {Partitions=disk.Layout!.Partitions.Reverse().ToArray()}}]}));
        var restored=JsonSerializer.Deserialize<DeviceTopologySnapshot>(JsonSerializer.Serialize(snapshot))!;Assert.Equal(hash,comparer.ComputeHash(restored));
        Assert.All(Assert.Single(DeviceTopologyInformationProjection.Create(restored,null).Devices,item=>item.DeviceIdentity==id).Fields,item=>Assert.Null(item.Value));
        Assert.NotEqual(hash,comparer.ComputeHash(snapshot with {DiskLayoutInformation=[disk with {OsDiskNumberReported=3}]}));
    }

    [Fact]
    public void DuplicateLegacyPortsDoNotEraseIndependentlyVerifiedDiskLayout()
    {
        var port=DeviceInformationTestData.Port(@"PCI\LAYOUT");
        var disk=new DeviceTopologyDiskLayoutInformation(@"windows-pnp|PCI\LAYOUT"){OsDiskNumberReported=2,Layout=WindowsDiskLayoutInformationDecoder.Layout(Frame("gpt"))};
        var snapshot=DeviceInformationTestData.Snapshot(port,port with {Id="another-port"}) with {DiskLayoutInformation=[disk],VolumeSpaceInformation=[]};
        var result=DeviceTopologyInformationProjection.Create(snapshot,DateTimeOffset.UtcNow);
        var device=Assert.Single(result.Devices,item=>item.DeviceIdentity==disk.DeviceIdentity);Assert.Equal("os_disk",device.ObjectType);Assert.Equal(20,device.Fields.Count);
        var bus=Assert.Single(device.Fields,item=>item.Definition.Name==StorageBusInformationCollectorTests.Name);
        Assert.Equal(StorageBusInformationCollectorTests.Source,bus.Definition.SourceId);Assert.Null(bus.Value);
        Assert.All(device.Fields.Where(item=>item.Definition.SourceId=="windows.storage-access-alignment"),item=>Assert.Null(item.Value));
        Assert.NotNull(Field(result,disk.DeviceIdentity,"storage_all_partition_layout_reported").Value);
    }

    internal static JsonDocument Fixture()
    {
        var assembly=typeof(DiskLayoutInformationCollectorTests).Assembly;var name=Assert.Single(assembly.GetManifestResourceNames(),item=>item.EndsWith(".Fixtures.Storage.windows-sdk-disk-layout.json",StringComparison.Ordinal));
        using var stream=assembly.GetManifestResourceStream(name)!;return JsonDocument.Parse(stream);
    }
    internal static byte[] Frame(string kind){using var sdk=Fixture();return Convert.FromBase64String(sdk.RootElement.GetProperty(kind).GetString()!);}
    internal static byte[] NumberFrame(uint number,uint partition=0){var bytes=new byte[12];BinaryPrimitives.WriteUInt32LittleEndian(bytes,7);BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4),number);BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8),partition);return bytes;}
    internal static DeviceInformationSnapshot Project(IReadOnlyList<DeviceTopologyDiskLayoutInformation> disks,IReadOnlyList<DeviceTopologyVolumeSpaceInformation>? volumes)
        =>DeviceTopologyInformationProjection.Create(DeviceInformationTestData.Snapshot() with {DiskLayoutInformation=disks,VolumeSpaceInformation=volumes},DateTimeOffset.UtcNow);
    internal static DeviceInformationField Field(DeviceInformationSnapshot snapshot,string id,string name)=>Assert.Single(Assert.Single(snapshot.Devices,item=>item.DeviceIdentity==id).Fields,item=>item.Definition.Name==name);
    internal sealed class Api:IWindowsDiskLayoutInformationApi
    {
        internal IReadOnlyList<WindowsDiskInterface>? Directory=[DiskA,DiskB];internal bool FailFirst;internal List<WindowsDiskInterface> Reads=[];
        public IReadOnlyList<WindowsDiskInterface>? Enumerate()=>Directory;
        public WindowsDiskLayoutCapture? Read(WindowsDiskInterface device){Reads.Add(device);if(FailFirst&&device==DiskA)throw new IOException("failed");return new(device==DiskA?2U:5U,WindowsDiskLayoutInformationDecoder.Layout(Frame("gpt")));}
    }
}
