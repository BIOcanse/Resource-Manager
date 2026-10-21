using System.Buffers.Binary;
using System.Text.Json;
using ResourceManager.App.Domain.DeviceTopology;
using ResourceManager.App.Infrastructure.DeviceTopology;
using ResourceManager.App.Infrastructure.DeviceTopology.Snapshots;
using ResourceManager.App.Infrastructure.Monitoring;

namespace Resource_Manager_APP.Tests;

public sealed partial class DiskIdentityInformationCollectorTests
{
    internal const string RelationName = "storage_all_physical_disk_relation_actual";
    internal const string HardwareId = "windows-storage|unique|ID-A";

    [Fact]
    public void OfficialSdkIdentityFramesUseActualDeviceAndIdentifierOffsets()
    {
        using var sdk = Fixture(); var root = sdk.RootElement;
        Assert.Equal(40, root.GetProperty("descriptorSize").GetInt32()); Assert.Equal(24, root.GetProperty("serialOffsetMember").GetInt32());
        Assert.Equal(28, root.GetProperty("busOffset").GetInt32()); Assert.Equal(16, root.GetProperty("deviceIdsSize").GetInt32());
        Assert.Equal(12, root.GetProperty("identifierArrayOffset").GetInt32()); Assert.Equal(16, root.GetProperty("identifierPayloadOffset").GetInt32());
        Assert.Equal("SERIAL-A", WindowsAtaSmartReader.ParseDeviceSerial(Frame("ata")));
        Assert.Equal("SERIAL-N", WindowsNvmeHealthReader.ParseDeviceSerial(Frame("nvme")));
        Assert.Null(WindowsAtaSmartReader.ParseDeviceSerial(Frame("virtual"))); Assert.Null(WindowsNvmeHealthReader.ParseDeviceSerial(Frame("virtual")));
        Assert.True(WindowsAtaSmartReader.MatchesUniqueId(Frame("vpd"), "ID-A", 0));
        Assert.True(WindowsAtaSmartReader.MatchesUniqueId(Frame("binary"), "0102030405060708", 3));
    }

    [Fact]
    public void OriginalIdentifySourcePublishesOnlyCurrentOpenedIdentityEvidence()
    {
        var physical = new Dictionary<string, object?> { ["DeviceId"] = 29, ["BusType"] = 11, ["SerialNumber"] = "SERIAL-A", ["UniqueId"] = "ID-A", ["UniqueIdFormat"] = 0, ["Size"] = 100000UL };
        var os = new Dictionary<string, object?>(physical) { ["Number"] = 2 };
        var fail = false; var reads = 0;
        var zone = new WindowsStorageSensorsMonitoringZone((_, sql) => new(sql.Contains("MSFT_PhysicalDisk") ? [physical] : [os], null),
            (_, _, _, _) => null, readIdentify: (number, protocol, serial, id, format) =>
            {
                reads++; Assert.Equal(2, number); Assert.Equal(StorageIdentifyProtocol.Ata, protocol); Assert.Equal("ID-A", id); Assert.Equal(0, format);
                return fail ? null : new(WindowsStorageIdentifyReader.ParseAta(StorageIdentifyInformationCollectorTests.AtaRaw()), null);
            });
        var source = Assert.Single(zone.ReadInformationDeclarations()!);
        Assert.Equal(new StorageHardwareIdentityEvidence("SERIAL-A", "ID-A", 0), source.HardwareIdentityEvidence);
        Assert.Equal(HardwareId, Relation(Disk(), [source]));
        fail = true; source = Assert.Single(zone.ReadInformationDeclarations()!);
        Assert.Null(source.HardwareIdentityEvidence); Assert.Null(Relation(Disk(), [source])); Assert.Equal(2, reads);
    }

    [Fact]
    public void VerifiedVpdRelationLinksExistingHardwareObjectWithoutMergingIdentityLayers()
    {
        var disk = Disk(); var hardware = Hardware(); var snapshot = Snapshot(disk, [hardware]);
        var fields = DeviceTopologyInformationProjection.Create(snapshot, DateTimeOffset.UtcNow);
        Assert.Equal(HardwareId, DiskLayoutInformationCollectorTests.Field(fields, disk.DeviceIdentity, RelationName).Value!.Value.GetString());
        Assert.Equal("physical_disk", Assert.Single(fields.Devices, item => item.DeviceIdentity == HardwareId).ObjectType);
        Assert.Equal("os_disk", Assert.Single(fields.Devices, item => item.DeviceIdentity == disk.DeviceIdentity).ObjectType);
        Assert.Equal("ATA Model", DeviceInformationTestData.Field(fields, HardwareId, "storage_all_model_declared").Value!.Value.GetString());
        Assert.Equal("windows.storage-device-identity", DiskLayoutInformationCollectorTests.Field(fields, disk.DeviceIdentity, RelationName).Definition.SourceId);
        Assert.Equal(HardwareId, Relation(disk with { OsDiskNumberReported = 999 }, [hardware]));
        var anotherPath = disk with { DeviceIdentity = DiskLayoutInformationCollectorTests.IdB, OsDiskNumberReported = 5 };
        var multiplePaths = DeviceTopologyInformationProjection.Create(snapshot with { DiskLayoutInformation = [disk, anotherPath] }, DateTimeOffset.UtcNow);
        Assert.Equal(HardwareId, DiskLayoutInformationCollectorTests.Field(multiplePaths, disk.DeviceIdentity, RelationName).Value!.Value.GetString());
        Assert.Equal(HardwareId, DiskLayoutInformationCollectorTests.Field(multiplePaths, anotherPath.DeviceIdentity, RelationName).Value!.Value.GetString());
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)] [InlineData(6)]
    [InlineData(7)] [InlineData(8)] [InlineData(9)] [InlineData(10)] [InlineData(11)] [InlineData(12)]
    public void MissingFailedOrContradictoryIdentityEvidenceCannotFallBackToDiskNumberOrSerial(int failure)
    {
        var disk = Disk(); var physical = Hardware(); IReadOnlyList<DeviceTopologyStorageInformation>? sources = [physical];
        switch (failure)
        {
            case 0: disk = disk with { HardwareIdentification = null }; break;
            case 1: disk = disk with { HardwareIdentification = disk.HardwareIdentification! with { SerialNumber = "other" } }; break;
            case 2: disk = disk with { HardwareIdentification = disk.HardwareIdentification! with { Protocol = StorageIdentifyProtocol.NvmeController } }; break;
            case 3: sources = null; break;
            case 4: sources = []; break;
            case 5: sources = [physical with { HardwareIdentityEvidence = null }]; break;
            case 6: sources = [physical with { Ata = null }]; break;
            case 7: sources = [physical with { HardwareIdentityEvidence = physical.HardwareIdentityEvidence! with { UniqueIdFormat = 4 } }]; break;
            case 8: disk = disk with { HardwareIdentification = disk.HardwareIdentification! with { DeviceIdentifiers = null } }; break;
            case 9: disk = disk with { HardwareIdentification = disk.HardwareIdentification! with { DeviceIdentifiers = Frame("vpd")[..^1] } }; break;
            case 10: sources = [physical with { DeviceIdentity = "windows-storage|unique|OTHER" }]; break;
            case 11: sources = [physical with { HardwareIdentityEvidence = physical.HardwareIdentityEvidence! with { SerialNumber = "other" } }]; break;
            case 12: sources = [physical with { Protocol = (StorageIdentifyProtocol)77 }]; break;
        }
        Assert.Null(Relation(disk, sources));
        Assert.NotNull(DiskLayoutInformationCollectorTests.Field(DeviceTopologyInformationProjection.Create(Snapshot(disk, sources), DateTimeOffset.UtcNow), disk.DeviceIdentity, "storage_all_partition_layout_reported").Value);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void WrongVpdAssociationHigherIdTypeOrDirtyIdentifierCannotConfirmHardware(int failure)
    {
        var bytes = Frame("vpd");
        switch (failure)
        {
            case 0: BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(24), 1); break; // Port, not device.
            case 1: BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16), 3); break; // Higher ID than the selected vendor ID.
            case 2: bytes[28] = (byte)'X'; break;
            case 3: BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(22), 1); break;
        }
        Assert.Null(Relation(Disk() with { HardwareIdentification = Disk().HardwareIdentification! with { DeviceIdentifiers = bytes } }, [Hardware()]));
    }

    [Fact]
    public void BinaryVpdAndExplicitSerialOnlyIdentityUseTheirOriginalExactRepresentations()
    {
        var physical = Hardware() with { DeviceIdentity = "windows-storage|unique|0102030405060708", HardwareIdentityEvidence = new("SERIAL-A", "0102030405060708", 3) };
        var disk = Disk() with { HardwareIdentification = Disk().HardwareIdentification! with { DeviceIdentifiers = Frame("binary") } };
        Assert.Equal(physical.DeviceIdentity, Relation(disk, [physical]));
        physical = Hardware() with { DeviceIdentity = "windows-storage|serial|SERIAL-A", HardwareIdentityEvidence = new("SERIAL-A", null, null) };
        disk = Disk() with { HardwareIdentification = Disk().HardwareIdentification! with { SerialNumber = " serial-a ", DeviceIdentifiers = null } };
        Assert.Equal(physical.DeviceIdentity, Relation(disk, [physical]));
        var second = Hardware() with { DeviceIdentity = "windows-storage|unique|OTHER", HardwareIdentityEvidence = new("SERIAL-A", "OTHER", 0) };
        Assert.Null(Relation(disk, [physical, second]));
    }

    [Fact]
    public void DuplicatePhysicalIdentityOrSerialAndOversizedCandidatesAreUnknown()
    {
        var hardware = Hardware(); Assert.Null(Relation(Disk(), [hardware, hardware]));
        var other = hardware with { DeviceIdentity = "windows-storage|unique|OTHER", HardwareIdentityEvidence = new("SERIAL-A", "OTHER", 0) };
        Assert.Null(Relation(Disk(), [hardware, other]));
        Assert.Null(Relation(Disk(), Enumerable.Repeat(hardware, WindowsInformationFieldProjection.MaximumPhysicalDiskRelationCandidates + 1).ToArray()));
    }

    [Fact]
    public void RelationSurvivesSerializationButCannotPublishBeforeACompleteCurrentObservation()
    {
        var snapshot = Snapshot(Disk(), [Hardware()]); var comparer = new DeviceTopologySemanticComparer();
        var restored = JsonSerializer.Deserialize<DeviceTopologySnapshot>(JsonSerializer.Serialize(snapshot))!;
        Assert.Equal(comparer.ComputeHash(snapshot), comparer.ComputeHash(restored)); Assert.Equal(HardwareId, Relation(restored.DiskLayoutInformation![0], restored.StorageInformation));
        Assert.All(DeviceTopologyInformationProjection.Create(restored, null).Devices.SelectMany(item => item.Fields), item => Assert.Null(item.Value));
        Assert.NotEqual(comparer.ComputeHash(snapshot), comparer.ComputeHash(snapshot with { StorageInformation = [Hardware() with { HardwareIdentityEvidence = null }] }));
    }

    internal static DeviceTopologyDiskLayoutInformation Disk() => DiskAlignmentInformationCollectorTests.Disk() with
    { HardwareIdentification = new(StorageIdentifyProtocol.Ata, "SERIAL-A", Frame("vpd")) };
    internal static DeviceTopologyStorageInformation Hardware() => new(HardwareId, StorageIdentifyProtocol.Ata,
        WindowsStorageIdentifyReader.ParseAta(StorageIdentifyInformationCollectorTests.AtaRaw()), null) { HardwareIdentityEvidence = new("SERIAL-A", "ID-A", 0) };
    internal static DeviceTopologySnapshot Snapshot(DeviceTopologyDiskLayoutInformation disk, IReadOnlyList<DeviceTopologyStorageInformation>? physical)
        => DeviceInformationTestData.Snapshot() with { DiskLayoutInformation = [disk], StorageInformation = physical, VolumeSpaceInformation = [] };
    internal static string? Relation(DeviceTopologyDiskLayoutInformation disk, IReadOnlyList<DeviceTopologyStorageInformation>? physical)
        => DiskLayoutInformationCollectorTests.Field(DeviceTopologyInformationProjection.Create(Snapshot(disk, physical), DateTimeOffset.UtcNow), disk.DeviceIdentity, RelationName).Value?.GetString();
    internal static byte[] Frame(string key) { using var sdk = Fixture(); return Convert.FromBase64String(sdk.RootElement.GetProperty(key).GetString()!); }
    internal static JsonDocument Fixture()
    {
        var assembly = typeof(DiskIdentityInformationCollectorTests).Assembly;
        var name = Assert.Single(assembly.GetManifestResourceNames(), item => item.EndsWith(".Fixtures.Storage.windows-sdk-disk-identity.json", StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(name)!; return JsonDocument.Parse(stream);
    }
}
