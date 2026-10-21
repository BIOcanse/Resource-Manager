using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using ResourceManager.App.Domain.Adaptation;
using ResourceManager.App.Domain.DeviceTopology;
using ResourceManager.App.Infrastructure.DeviceTopology.Snapshots;
using ResourceManager.App.Infrastructure.Monitoring;
using static ResourceManager.App.Infrastructure.Monitoring.PlatformSensorWmiUtilities;

namespace Resource_Manager_APP.Tests;

public sealed class StorageIdentifyInformationCollectorTests
{
    [Theory]
    [InlineData(StorageIdentifyProtocol.Ata, 50, 2, 0, 512)]
    [InlineData(StorageIdentifyProtocol.NvmeController, 49, 3, 1, 4096)]
    public void RequestsHaveExactProtocolAndControllerOrDeviceDomain(StorageIdentifyProtocol protocol, uint property, uint type, uint cns, int size)
    {
        var request = WindowsStorageIdentifyReader.CreateIdentifyQuery(protocol);
        Assert.Equal(48 + size, request.Length);
        Assert.Equal(property, U32(request, 0));
        Assert.Equal(0U, U32(request, 4));
        Assert.Equal(type, U32(request, 8));
        Assert.Equal(1U, U32(request, 12));
        Assert.Equal(cns, U32(request, 16));
        Assert.Equal(0U, U32(request, 20));
        Assert.Equal(40U, U32(request, 24));
        Assert.Equal((uint)size, U32(request, 28));
        Assert.All(request[32..], value => Assert.Equal(0, value));
        var raw = protocol == StorageIdentifyProtocol.Ata ? AtaRaw() : NvmeRaw();
        Assert.Equal(raw, WindowsStorageIdentifyReader.ParseIdentifyResponse(Response(protocol, raw), protocol));
        Assert.Throws<ArgumentOutOfRangeException>(() => WindowsStorageIdentifyReader.CreateIdentifyQuery((StorageIdentifyProtocol)123));
    }

    [Theory]
    [InlineData(0, 47)] [InlineData(4, 49)] [InlineData(8, 2)] [InlineData(12, 2)]
    [InlineData(16, 0)] [InlineData(20, 1)] [InlineData(24, 39)] [InlineData(24, uint.MaxValue)]
    [InlineData(28, 4095)] [InlineData(28, 4097)]
    public void InvalidDescriptorsAreNotParsed(int offset, uint value)
    {
        var response = Response(StorageIdentifyProtocol.NvmeController, NvmeRaw());
        W32(response, offset, value);
        Assert.Null(WindowsStorageIdentifyReader.ParseIdentifyResponse(response, StorageIdentifyProtocol.NvmeController));
    }

    [Fact]
    public void AtaDeclarationsAreWordSwappedAndChecksumIsOptional()
    {
        var raw = AtaRaw();
        var data = WindowsStorageIdentifyReader.ParseAta(raw)!;
        Assert.Equal("ATA Model", data.Model);
        Assert.Equal("SERIAL-A", data.SerialNumber);
        Assert.Equal("FW1", data.FirmwareRevision);
        Assert.Equal(123456UL * 512, data.CapacityBytes);
        Assert.Equal(512UL, data.LogicalSectorBytes);
        Assert.Equal(4096UL, data.PhysicalSectorBytes);
        Assert.True(data.TrimSupported);
        Assert.True(data.NonRotatingMedia);
        Assert.Null(data.MediaRotationRateRpm);
        Assert.Equal(raw, data.Raw);
        raw[510] = 0xA5;
        raw[511] = (byte)((256 - (raw.Sum(static value => value) & 255)) & 255);
        Assert.NotNull(WindowsStorageIdentifyReader.ParseAta(raw));
        raw[511] ^= 1;
        Assert.Null(WindowsStorageIdentifyReader.ParseAta(raw));
        raw[510] = 0; raw[511] = 0xA5; // A signature in the high byte is not the checksum flag.
        Assert.NotNull(WindowsStorageIdentifyReader.ParseAta(raw));
        Assert.Null(WindowsStorageIdentifyReader.ParseAta(raw[..511]));
        Assert.Null(WindowsStorageIdentifyReader.ParseAta(new byte[512]));
        Assert.Null(WindowsStorageIdentifyReader.ParseAta(Enumerable.Repeat((byte)255, 512).ToArray()));
        W16(raw, 0, 0x8000);
        Assert.Null(WindowsStorageIdentifyReader.ParseAta(raw));
        W16(raw, 0, 0x848A);
        Assert.NotNull(WindowsStorageIdentifyReader.ParseAta(raw));
    }

    [Fact]
    public void AtaInvalidUnitsAndUnknownDeclarationsStayEmpty()
    {
        var raw = AtaRaw();
        W16(raw, 106, 0);
        var data = WindowsStorageIdentifyReader.ParseAta(raw)!;
        Assert.Null(data.LogicalSectorBytes); Assert.Null(data.PhysicalSectorBytes); Assert.Null(data.CapacityBytes);
        W16(raw, 106, 0x7003); // 8 logical sectors of 4096 bytes per physical sector.
        W32(raw, 234, 2048);
        data = WindowsStorageIdentifyReader.ParseAta(raw)!;
        Assert.Equal(4096UL, data.LogicalSectorBytes); Assert.Equal(32768UL, data.PhysicalSectorBytes);
        Assert.Equal(123456UL * 4096, data.CapacityBytes);
        W32(raw, 234, 256); // Longer-than-256-word flag contradicts the size.
        Assert.Null(WindowsStorageIdentifyReader.ParseAta(raw)!.LogicalSectorBytes);
        W16(raw, 106, 0x6000); // A multiple-sector flag with a ratio of one contradicts the physical size.
        Assert.Null(WindowsStorageIdentifyReader.ParseAta(raw)!.PhysicalSectorBytes);
        W16(raw, 106, 0x4000); W16(raw, 83, 0x0400); W32(raw, 120, 100);
        Assert.Equal(51200UL, WindowsStorageIdentifyReader.ParseAta(raw)!.CapacityBytes); // Explicit LBA28 support.
        W16(raw, 49, 0);
        Assert.Null(WindowsStorageIdentifyReader.ParseAta(raw)!.CapacityBytes);
        W16(raw, 169, ushort.MaxValue); W16(raw, 80, ushort.MaxValue); W16(raw, 217, 0x400);
        data = WindowsStorageIdentifyReader.ParseAta(raw)!;
        Assert.Null(data.TrimSupported); Assert.Null(data.MajorVersionSupportedMask);
        Assert.Null(data.NonRotatingMedia); Assert.Null(data.MediaRotationRateRpm);
        W16(raw, 169, 0); W16(raw, 217, 7200);
        data = WindowsStorageIdentifyReader.ParseAta(raw)!;
        Assert.False(data.TrimSupported); Assert.False(data.NonRotatingMedia); Assert.Equal((ushort)7200, data.MediaRotationRateRpm);
        raw[54] = 0xFF;
        Assert.Null(WindowsStorageIdentifyReader.ParseAta(raw)!.Model);
        raw = AtaRaw(); W16(raw, 106, 0x5000); W32(raw, 234, uint.MaxValue);
        BinaryPrimitives.WriteUInt64LittleEndian(raw.AsSpan(200), 0x0000FFFFFFFFFFFFUL);
        Assert.Null(WindowsStorageIdentifyReader.ParseAta(raw)!.CapacityBytes); // Multiplication must not wrap.
        raw = AtaRaw(); W16(raw, 53, 2); W16(raw, 69, 8);
        Assert.Null(WindowsStorageIdentifyReader.ParseAta(raw)!.CapacityBytes); // Extended-sector contract is not substituted by LBA48.
    }

    [Theory]
    [InlineData(0x00010100, 8, 0, false)] [InlineData(0x00010200, 8, 0, true)]
    [InlineData(0x00010400, 0, 0x800, false)] [InlineData(0x00020000, 0, 0x800, true)]
    [InlineData(0x00020300, 0, 0x1000, true)] [InlineData(0x00020300, 0, 0, false)]
    [InlineData(uint.MaxValue, 8, 0x1800, false)]
    public void NvmeCapacityRequiresDefinedVersionAndCapability(uint version, ushort oacs, uint ctratt, bool supported)
    {
        var raw = NvmeRaw(); W32(raw, 80, version); W16(raw, 128, oacs); W32(raw, 96, ctratt);
        var data = WindowsStorageIdentifyReader.ParseNvmeController(raw)!;
        Assert.Equal(supported, data.TotalNvmCapacityBytes.HasValue);
        Assert.Equal(supported, data.UnallocatedNvmCapacityBytes.HasValue);
        if (supported) Assert.Equal(UInt128.MaxValue - 17, data.TotalNvmCapacityBytes);
    }

    [Fact]
    public void NvmeFieldsKeepEndianDomainAndUnsigned128Precision()
    {
        var raw = NvmeRaw();
        var data = WindowsStorageIdentifyReader.ParseNvmeController(raw)!;
        Assert.Equal("NVMe Model", data.Model); Assert.Equal("SERIAL-N", data.SerialNumber);
        Assert.Equal((ushort)0x1234, data.VendorId); Assert.Equal((ushort)0x5678, data.SubsystemVendorId);
        Assert.Equal(0xABCDEFU, data.IeeeOui); Assert.Equal((ushort)0x123, data.ControllerId);
        Assert.Equal(42U, data.MaximumValidNamespaceId); Assert.True(data.DatasetManagementSupported);
        Assert.Equal(UInt128.Zero, data.UnallocatedNvmCapacityBytes);
        Assert.Equal(raw, data.Raw);
        raw[24] = 0x80; W16(raw, 260, 0);
        data = WindowsStorageIdentifyReader.ParseNvmeController(raw)!;
        Assert.Null(data.Model); Assert.False(data.DatasetManagementSupported);
        Assert.Null(WindowsStorageIdentifyReader.ParseNvmeController(raw[..4095]));
        W16(raw, 0, 0);
        Assert.Null(WindowsStorageIdentifyReader.ParseNvmeController(raw));
    }

    [Theory]
    [InlineData(StorageIdentifyProtocol.Ata)] [InlineData(StorageIdentifyProtocol.NvmeController)]
    public void SameOpenedObjectAndIdentifySerialMustMatch(StorageIdentifyProtocol protocol)
    {
        var serial = protocol == StorageIdentifyProtocol.Ata ? "SERIAL-A" : "SERIAL-N";
        var raw = protocol == StorageIdentifyProtocol.Ata ? AtaRaw() : NvmeRaw();
        var query = new DeviceQuery(Descriptor(protocol, serial), Response(protocol, raw));
        Assert.NotNull(Read(query)); Assert.True(query.Disposed); Assert.Equal(new uint[] { 0, 2, protocol == StorageIdentifyProtocol.Ata ? 50U : 49U }, query.Properties);
        query = new DeviceQuery(Descriptor(protocol, "other"), Response(protocol, raw));
        Assert.Null(Read(query)); Assert.Equal(new uint[] { 0 }, query.Properties);
        var wrongBus = Descriptor(protocol, serial); W32(wrongBus, 28, protocol == StorageIdentifyProtocol.Ata ? 17U : 11U);
        query = new DeviceQuery(wrongBus, Response(protocol, raw));
        Assert.Null(Read(query)); Assert.Equal(new uint[] { 0 }, query.Properties);
        query = new DeviceQuery(Descriptor(protocol, serial), Response(protocol, raw)) { UniqueId = "replacement" };
        Assert.Null(Read(query)); Assert.Equal(new uint[] { 0, 2 }, query.Properties);
        query = new DeviceQuery(Descriptor(protocol, serial), Response(protocol, raw));
        if (protocol == StorageIdentifyProtocol.Ata) AtaString(raw, 20, 20, "changed"); else Ascii(raw, 4, 20, "changed");
        query.Response = Response(protocol, raw);
        Assert.Null(Read(query));
        query = new DeviceQuery(Descriptor(protocol, serial), Response(protocol, raw));
        Assert.Null(WindowsStorageIdentifyReader.Read(7, protocol, serial, "id", null, _ => query));
        Assert.Equal(new uint[] { 0 }, query.Properties);
        var opened = false;
        Assert.Null(WindowsStorageIdentifyReader.Read(-1, protocol, serial, null, null, _ => { opened = true; return query; }));
        Assert.False(opened);

        StorageIdentifyRead? Read(DeviceQuery q) => WindowsStorageIdentifyReader.Read(7, protocol, serial, "id", 0, number => { Assert.Equal(7, number); return q; });
    }

    [Fact]
    public void ZoneUsesExactIdentityAndFreezeDoesNotQuery()
    {
        var physical = new[] { Disk(9, 11, "SERIAL-A", "a"), Disk(21, 17, "SERIAL-N", "n"), Disk(22, 7, "usb", "usb") };
        var os = new[] { OsDisk(4, physical[0]), OsDisk(6, physical[1]), OsDisk(8, physical[2]) };
        var queryCount = 0; var reads = new List<int>(); var failPhysical = false; var failOs = false;
        var zone = new WindowsStorageSensorsMonitoringZone((_, sql) =>
        {
            queryCount++;
            return sql.Contains("MSFT_PhysicalDisk") ? new(physical, failPhysical ? "failed" : null) : new(os, failOs ? "failed" : null);
        }, (_, _, _, _) => null, readIdentify: (number, protocol, serial, id, format) =>
        {
            reads.Add(number);
            return protocol == StorageIdentifyProtocol.Ata ? new(WindowsStorageIdentifyReader.ParseAta(AtaRaw()), null)
                : new(null, WindowsStorageIdentifyReader.ParseNvmeController(NvmeRaw()));
        });
        var current = zone.ReadInformationDeclarations()!;
        Assert.Equal(new[] { 4, 6 }, reads); Assert.Equal(2, current.Count);
        Assert.Equal("windows-storage|unique|A", current[0].DeviceIdentity);
        Assert.Equal(StorageIdentifyProtocol.Ata, current[0].Protocol);
        zone.ApplyMode(ResourceManagerComputeZoneMode.Freeze);
        Assert.Null(zone.ReadInformationDeclarations()); Assert.Equal(2, queryCount);
        zone.ApplyMode(ResourceManagerComputeZoneMode.LowPower);
        failOs = true;
        Assert.All(zone.ReadInformationDeclarations()!, disk => { Assert.Null(disk.Ata); Assert.Null(disk.Nvme); });
        Assert.Equal(2, reads.Count);
        failPhysical = true;
        Assert.Null(zone.ReadInformationDeclarations());
        failPhysical = false; failOs = false;
        physical[0]["SerialNumber"] = "SERIAL-N"; // Ambiguous serials never route by ordinal.
        zone.ReadInformationDeclarations(); Assert.Equal(2, reads.Count);
    }

    [Fact]
    public void ProjectionKeepsProtocolFieldsRawPagesAndCurrentNulls()
    {
        var information = Declarations();
        var snapshot = DeviceInformationTestData.Snapshot() with { StorageInformation = information };
        var fields = DeviceTopologyInformationProjection.Create(snapshot, DateTimeOffset.UtcNow);
        Assert.Equal("ATA Model", DeviceInformationTestData.Field(fields, "ata", "storage_all_model_declared").Value!.Value.GetString());
        Assert.Equal((UInt128.MaxValue - 17).ToString(), DeviceInformationTestData.Field(fields, "nvme", "storage_all_nvme_controller_accessible_total_nvm_capacity_reported_bytes").Value!.Value.GetString());
        Assert.Equal(NvmeRaw(), DeviceInformationTestData.Field(fields, "nvme", "storage_all_nvme_identify_controller_raw").Value!.Value.GetBytesFromBase64());
        Assert.DoesNotContain(fields.Devices.Single(d => d.DeviceIdentity == "nvme").Fields, field => field.Definition.Name == "storage_all_capacity_declared");
        Assert.DoesNotContain(fields.Devices.Single(d => d.DeviceIdentity == "ata").Fields, field => field.Definition.SourceId == "windows.nvme-identify-controller");
        Assert.All(DeviceTopologyInformationProjection.Create(snapshot, null).Devices.SelectMany(d => d.Fields), field => Assert.Null(field.Value));
        var failed = snapshot with { StorageInformation = information.Select(d => d with { Ata = null, Nvme = null }).ToArray() };
        Assert.All(DeviceTopologyInformationProjection.Create(failed, DateTimeOffset.UtcNow).Devices.Where(d => d.ObjectType == "physical_disk").SelectMany(d => d.Fields), field => Assert.Null(field.Value));
        var duplicate = snapshot with { StorageInformation = [information[0], information[0]] };
        Assert.DoesNotContain(DeviceTopologyInformationProjection.Create(duplicate, DateTimeOffset.UtcNow).Devices, d => d.DeviceIdentity == "ata");
    }

    [Fact]
    public void SnapshotSerializationPreservesDeclarationsAndOneDeviceFailureDoesNotBorrowValues()
    {
        var snapshot = DeviceInformationTestData.Snapshot() with { StorageInformation = Declarations() };
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var restored = JsonSerializer.Deserialize<DeviceTopologySnapshot>(JsonSerializer.Serialize(snapshot, options), options)!;
        Assert.Equal(UInt128.MaxValue - 17, restored.StorageInformation![1].Nvme!.TotalNvmCapacityBytes);
        Assert.Equal(NvmeRaw(), restored.StorageInformation[1].Nvme!.Raw);
        var physical = new[] { Disk(1, 11, "SERIAL-A", "a"), Disk(2, 17, "SERIAL-N", "n") };
        var zone = new WindowsStorageSensorsMonitoringZone((_, sql) => new(
            sql.Contains("MSFT_PhysicalDisk") ? physical : [OsDisk(8, physical[0]), OsDisk(9, physical[1])], null),
            (_, _, _, _) => null, readIdentify: (_, protocol, _, _, _) => protocol == StorageIdentifyProtocol.Ata
                ? new(WindowsStorageIdentifyReader.ParseAta(AtaRaw()), null) : null);
        var information = zone.ReadInformationDeclarations()!;
        var fields = DeviceTopologyInformationProjection.Create(snapshot with { StorageInformation = information }, DateTimeOffset.UtcNow);
        Assert.Equal("ATA Model", DeviceInformationTestData.Field(fields, "windows-storage|unique|A", "storage_all_model_declared").Value!.Value.GetString());
        Assert.All(fields.Devices.Single(d => d.DeviceIdentity == "windows-storage|unique|N").Fields, f => Assert.Null(f.Value));
    }

    internal static IReadOnlyList<DeviceTopologyStorageInformation> Declarations() =>
        [new("ata", StorageIdentifyProtocol.Ata, WindowsStorageIdentifyReader.ParseAta(AtaRaw()), null),
         new("nvme", StorageIdentifyProtocol.NvmeController, null, WindowsStorageIdentifyReader.ParseNvmeController(NvmeRaw()))];

    [Fact]
    public void ExistingReadQueryPublishesExtendedCapacityAfterExactOpenedObjectValidation()
    {
        var raw = AtaExtendedCapacityInformationCollectorTests.Raw();
        var query = new DeviceQuery(Descriptor(StorageIdentifyProtocol.Ata, "SERIAL-A"), Response(StorageIdentifyProtocol.Ata, raw));
        var read = WindowsStorageIdentifyReader.Read(7, StorageIdentifyProtocol.Ata, "SERIAL-A", "id", 0, _ => query)!;
        Assert.Equal(AtaExtendedCapacityInformationCollectorTests.MaximumSectors * 512, read.Ata!.CapacityBytes);
        Assert.Equal(raw, read.Ata.Raw);
        Assert.Equal(new uint[] { 0, 2, 50 }, query.Properties);
        Assert.True(query.Disposed);
        AtaString(raw, 20, 20, "replacement");
        query = new DeviceQuery(Descriptor(StorageIdentifyProtocol.Ata, "SERIAL-A"), Response(StorageIdentifyProtocol.Ata, raw));
        Assert.Null(WindowsStorageIdentifyReader.Read(7, StorageIdentifyProtocol.Ata, "SERIAL-A", "id", 0, _ => query));
    }

    internal static byte[] AtaRaw()
    {
        var raw = new byte[512]; W16(raw, 0, 0x40);
        AtaString(raw, 20, 20, "SERIAL-A"); AtaString(raw, 46, 8, "FW1"); AtaString(raw, 54, 40, "ATA Model");
        W16(raw, 49, 0x200); W16(raw, 83, 0x4400); W16(raw, 106, 0x6003); W16(raw, 169, 1); W16(raw, 217, 1); W16(raw, 80, 0x1F0);
        BinaryPrimitives.WriteUInt64LittleEndian(raw.AsSpan(200), 123456);
        return raw;
    }

    internal static byte[] NvmeRaw()
    {
        var raw = new byte[4096]; W16(raw, 0, 0x1234); W16(raw, 1, 0x5678);
        Ascii(raw, 4, 20, "SERIAL-N"); Ascii(raw, 24, 40, "NVMe Model"); Ascii(raw, 64, 8, "FW2");
        raw[73] = 0xEF; raw[74] = 0xCD; raw[75] = 0xAB; W16(raw, 39, 0x123);
        W32(raw, 80, 0x00020300); W16(raw, 128, 8); W32(raw, 516, 42); W16(raw, 260, 4);
        BinaryPrimitives.WriteUInt128LittleEndian(raw.AsSpan(280, 16), UInt128.MaxValue - 17);
        return raw;
    }

    private static byte[] Response(StorageIdentifyProtocol protocol, byte[] raw)
    {
        var bytes = WindowsStorageIdentifyReader.CreateIdentifyQuery(protocol); W32(bytes, 0, 48); W32(bytes, 4, 48);
        raw.CopyTo(bytes, 48); return bytes;
    }
    private static byte[] Descriptor(StorageIdentifyProtocol protocol, string serial)
    {
        var raw = new byte[36 + serial.Length + 1]; W32(raw, 0, 36); W32(raw, 4, (uint)raw.Length); W32(raw, 24, 36);
        W32(raw, 28, protocol == StorageIdentifyProtocol.Ata ? 11U : 17U); Encoding.ASCII.GetBytes(serial).CopyTo(raw, 36); return raw;
    }
    private static Dictionary<string, object?> Disk(int index, int bus, string serial, string id) => new()
        { ["DeviceId"] = index, ["BusType"] = bus, ["SerialNumber"] = serial, ["UniqueId"] = id, ["UniqueIdFormat"] = 0, ["Size"] = 100000UL };
    private static Dictionary<string, object?> OsDisk(int number, Dictionary<string, object?> physical) =>
        new(physical) { ["Number"] = number };
    private static void Ascii(byte[] raw, int offset, int length, string text)
        => Encoding.ASCII.GetBytes(text.PadRight(length)).CopyTo(raw, offset);
    private static void AtaString(byte[] raw, int offset, int length, string text)
    {
        var bytes = Encoding.ASCII.GetBytes(text.PadRight(length));
        for (var i = 0; i < bytes.Length; i += 2) { raw[offset + i] = bytes[i + 1]; raw[offset + i + 1] = bytes[i]; }
    }
    private static uint U32(byte[] raw, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(offset));
    private static void W32(byte[] raw, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(offset), value);
    private static void W16(byte[] raw, int word, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(word * 2), value);

    private sealed class DeviceQuery(byte[] descriptor, byte[] response) : IWindowsStorageIdentifyQuery
    {
        public byte[] Response { get; set; } = response;
        public string UniqueId { get; init; } = "id";
        public List<uint> Properties { get; } = [];
        public bool Disposed { get; private set; }
        public byte[]? Query(byte[] input, int outputLength)
        {
            var property = U32(input, 0); Properties.Add(property);
            if (property == 0) return descriptor;
            if (property is 49 or 50) { Assert.Equal(Response.Length, outputLength); return Response; }
            Assert.Equal(2U, property);
            var bytes = Encoding.ASCII.GetBytes(UniqueId); var result = new byte[28 + bytes.Length];
            W32(result, 0, 16); W32(result, 4, (uint)result.Length); W32(result, 8, 1); W32(result, 12, 2);
            BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(20), (ushort)bytes.Length); bytes.CopyTo(result, 28);
            return result;
        }
        public void Dispose() => Disposed = true;
    }
}
