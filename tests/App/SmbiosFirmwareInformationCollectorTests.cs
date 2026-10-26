using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using ResourceManager.App.Domain.DeviceTopology;
using ResourceManager.ReadWrite.Windows.Devices;
using ResourceManager.App.Infrastructure.DeviceTopology.Snapshots;
using ResourceManager.App.Infrastructure.Monitoring;

namespace Resource_Manager_APP.Tests;

public sealed partial class SmbiosFirmwareInformationCollectorTests
{
    [Fact]
    public void ChassisTypesComeFromTheSameRawReadAndExcludeLockBit()
    {
        byte[] chassis = [3, 9, 1, 0, 0, 0x8a, 0, 0, 0, 0, 0];
        var reads = 0;
        var capture = SmbiosFirmwareInformationReader.Read(() => { reads++; return Table(3, 10, chassis, Bios("09/30/2026")); });
        Assert.Equal(1, reads);
        Assert.Equal(new byte[] { 10 }, capture!.ChassisTypes);
        Assert.Equal("2026-09-30", capture.Declarations.BiosReleaseDate);
        Assert.Null(SmbiosFirmwareInformationReader.Parse(Table(3, 10, Bios("09/30/2026")))!.ChassisTypes);
        Assert.Null(SmbiosFirmwareInformationReader.Parse(Table(3, 10, [3, 5, 1, 0, 0, 0, 0]))!.ChassisTypes);
        byte[] tower = [3, 9, 2, 0, 0, 7, 0, 0, 0, 0, 0];
        Assert.Equal(new byte[] { 10, 7 }, SmbiosFirmwareInformationReader.Parse(Table(3, 10, chassis, tower))!.ChassisTypes);
    }

    [Theory]
    [InlineData("09/30/2026", 3, 10, "2026-09-30")]
    [InlineData("01/02/99", 2, 2, "1999-01-02")]
    [InlineData("01/02/00", 2, 0, "1900-01-02")]
    [InlineData("01/02/99", 2, 3, null)]
    [InlineData("02/29/2025", 3, 10, null)]
    [InlineData("2026-09-30", 3, 10, null)]
    [InlineData("00/00/0000", 3, 10, null)]
    public void ReleaseDateUsesDeclaredCalendarAndExplicitLegacyCentury(string value, byte major, byte minor, string? expected)
        => Assert.Equal(expected, SmbiosFirmwareInformationReader.ParseReleaseDate(value, major, minor));

    [Fact]
    public void OneRawReadSuppliesBiosSlotAndDimmDeclarationsWithCorrectUnits()
    {
        var readCount = 0;
        var raw = Table(3, 10, Bios("09/30/2026"), Slot(0x1234, 0x11), Dimm());
        var capture = SmbiosFirmwareInformationReader.Read(() => { readCount++; return raw; });
        Assert.NotNull(capture);
        Assert.Equal(1, readCount);
        Assert.Equal("2026-09-30", capture.Declarations.BiosReleaseDate);
        var slot = Assert.Single(capture.Declarations.Slots);
        Assert.Equal("smbios-slot|1234", slot.DeviceIdentity);
        Assert.Equal("主板插槽", slot.Designation);
        Assert.Equal((byte)0xa5, slot.SlotType);
        Assert.Equal((byte)0x0d, slot.DataBusWidth);
        Assert.Equal((byte)4, slot.CurrentUsage);
        Assert.Equal((ushort)0x2345, slot.SlotId);
        Assert.Equal((ushort)0, slot.PciSegment);
        Assert.Equal((byte)3, slot.PciBus);
        Assert.Equal((byte)17, slot.PciDevice);
        Assert.Equal((byte)5, slot.PciFunction);
        var dimm = Assert.Single(capture.Modules);
        Assert.Equal("中文厂商", dimm.Manufacturer);
        Assert.Equal(1024UL * 1024 * 1024, dimm.CapacityBytes);
        var snapshot = DeviceInformationTestData.Snapshot() with {
            FirmwareDeclarations = capture.Declarations, MemoryModules = capture.Modules
        };
        var fields = DeviceTopologyInformationProjection.Create(snapshot, snapshot.CapturedAt);
        Assert.Equal("2026-09-30", DeviceInformationTestData.Field(fields, "bios", "bios_all_release_date").Value!.Value.GetString());
        var device = Assert.Single(fields.Devices, d => d.DeviceIdentity == slot.DeviceIdentity);
        Assert.Equal("motherboard_slot", device.ObjectType);
        Assert.Equal(12, device.Fields.Count);
        Assert.All(device.Fields, field => Assert.Equal("smbios.type9", field.Definition.SourceId));
        Assert.Equal("4", DeviceInformationTestData.Field(fields, slot.DeviceIdentity, "motherboard_all_slot_current_usage_declared_code").Value!.Value.GetString());
        Assert.DoesNotContain(device.Fields, field => field.Definition.Name.Contains("actual", StringComparison.Ordinal));
        var persisted = JsonSerializer.Deserialize<DeviceTopologySnapshot>(JsonSerializer.Serialize(snapshot))!;
        Assert.Equal("主板插槽", Assert.Single(persisted.FirmwareDeclarations!.Slots).Designation);
        var declarationsOnly = DeviceTopologyInformationProjection.Create(persisted, null);
        Assert.All(declarationsOnly.Devices.SelectMany(d => d.Fields), field => Assert.Null(field.Value));
    }

    [Theory]
    [InlineData(2, 0, 0x0c, false, false)]
    [InlineData(2, 1, 0x0d, true, false)]
    [InlineData(2, 5, 0x11, true, false)]
    [InlineData(2, 6, 0x0d, true, false)]
    [InlineData(2, 6, 0x11, true, true)]
    public void OptionalSlotFieldsRequireBothVersionAndLength(byte major, byte minor, byte length, bool characteristics2, bool address)
    {
        var capture = SmbiosFirmwareInformationReader.Parse(Table(major, minor, Slot(1, length, "SLOT")))!;
        var slot = Assert.Single(capture.Declarations.Slots);
        Assert.Equal(characteristics2, slot.Characteristics2 is not null);
        Assert.Equal(address, slot.PciSegment is not null);
        Assert.Equal("SLOT", slot.Designation);
    }

    [Fact]
    public void UnknownEnumsAndCharacteristicsDoNotFabricateZerosAndSlotIdDependsOnType()
    {
        var bytes = Slot(1, 0x11, "SLOT");
        bytes[5] = 2; bytes[6] = 0; bytes[7] = 0xff; bytes[8] = 2; bytes[0x0b] = 1;
        var slot = Assert.Single(SmbiosFirmwareInformationReader.Parse(Table(3, 10, bytes))!.Declarations.Slots);
        Assert.Null(slot.SlotType); Assert.Null(slot.DataBusWidth); Assert.Null(slot.CurrentUsage);
        Assert.Null(slot.SlotLength); Assert.Null(slot.SlotId); Assert.Null(slot.Characteristics1);
        Assert.Null(slot.PciSegment); Assert.Null(slot.PciBus); Assert.Null(slot.PciDevice); Assert.Null(slot.PciFunction);
        Assert.Equal((byte)0, slot.Characteristics2);
        bytes[5] = 3; // ISA has no defined Slot ID/BDF; bytes do not invent one.
        slot = Assert.Single(SmbiosFirmwareInformationReader.Parse(Table(3, 10, bytes))!.Declarations.Slots);
        Assert.Null(slot.SlotId); Assert.Null(slot.PciSegment);
        bytes[5] = 4; BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(9), 16);
        Assert.Null(Assert.Single(SmbiosFirmwareInformationReader.Parse(Table(3, 10, bytes))!.Declarations.Slots).SlotId);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(9), 15);
        Assert.Equal((ushort)15, Assert.Single(SmbiosFirmwareInformationReader.Parse(Table(3, 10, bytes))!.Declarations.Slots).SlotId);
    }

    [Fact]
    public void PciDeclarationRetainsLegalFullWidthValuesWithoutUsingNonPciSentinels()
    {
        var bytes = Slot(1, 0x11, "SLOT");
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(9), ushort.MaxValue);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(0x0d), ushort.MaxValue);
        bytes[0x0f] = 0xff; bytes[0x10] = 0xff;
        var slot = Assert.Single(SmbiosFirmwareInformationReader.Parse(Table(3, 10, bytes))!.Declarations.Slots);
        Assert.Equal(ushort.MaxValue, slot.SlotId);
        Assert.Equal(ushort.MaxValue, slot.PciSegment);
        Assert.Equal((byte)255, slot.PciBus);
        Assert.Equal((byte)31, slot.PciDevice);
        Assert.Equal((byte)7, slot.PciFunction);
    }

    [Fact]
    public void DuplicateSlotHandlesAndMultipleBiosRecordsAreNotResolvedByFirstMatch()
    {
        var parsed = SmbiosFirmwareInformationReader.Parse(Table(3, 10, Bios("01/01/2020"), Bios("01/01/2021"),
            Slot(1, 0x11), Slot(1, 0x11), Slot(2, 0x11)))!;
        Assert.Null(parsed.Declarations.BiosReleaseDate);
        Assert.Equal("smbios-slot|0002", Assert.Single(parsed.Declarations.Slots).DeviceIdentity);
    }

    [Fact]
    public void TextUsesStrictUtf8At35AndDoesNotGuessPre35Encoding()
    {
        Assert.Null(Assert.Single(SmbiosFirmwareInformationReader.Parse(Table(3, 4, Slot(1, 0x11)))!.Declarations.Slots).Designation);
        var bytes = Slot(1, 0x11, "SLOT");
        bytes[0x11] = 0xff;
        var parsed = SmbiosFirmwareInformationReader.Parse(Table(3, 5, bytes))!;
        Assert.Null(Assert.Single(parsed.Declarations.Slots).Designation);
        Assert.NotNull(Assert.Single(parsed.Declarations.Slots).SlotType);
        var dimm = Dimm();
        dimm[0x28 + Encoding.UTF8.GetByteCount("DIMM\0")] = 0xff;
        var module = Assert.Single(SmbiosFirmwareInformationReader.Parse(Table(3, 5, dimm))!.Modules);
        Assert.True(string.IsNullOrEmpty(module.Manufacturer));
        Assert.Equal("Part", module.PartNumber);
    }

    [Fact]
    public void ControlCharactersAreRejectedBeforeWhitespaceTrimmingAndOtherValuesRemainCurrent()
    {
        var parsed = SmbiosFirmwareInformationReader.Parse(Table(3, 10,
            Bios("09/30/2026\r"), Slot(1, 0x11, "\tSLOT"), Dimm("Manufacturer\n")))!;
        Assert.Null(parsed.Declarations.BiosReleaseDate);
        var slot = Assert.Single(parsed.Declarations.Slots);
        Assert.Null(slot.Designation);
        Assert.Equal((byte)0xa5, slot.SlotType);
        var module = Assert.Single(parsed.Modules);
        Assert.True(string.IsNullOrEmpty(module.Manufacturer));
        Assert.Equal("Part", module.PartNumber);
        var clean = SmbiosFirmwareInformationReader.Parse(Table(3, 10, Slot(1, 0x11, " SLOT "), Dimm(" Manufacturer ")))!;
        Assert.Equal("SLOT", Assert.Single(clean.Declarations.Slots).Designation);
        Assert.Equal("Manufacturer", Assert.Single(clean.Modules).Manufacturer);
    }

    [Theory]
    [InlineData("header")]
    [InlineData("length")]
    [InlineData("formatted")]
    [InlineData("unterminated")]
    public void TruncatedSourcesReturnUnavailableRatherThanPartialSlotLists(string corruption)
    {
        var raw = Table(3, 10, Slot(1, 0x11));
        switch (corruption)
        {
            case "header": raw = raw[..7]; break;
            case "length": BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(4), uint.MaxValue); break;
            case "formatted": raw[9] = 0xff; break;
            case "unterminated": raw[^1] = 1; break;
        }
        Assert.Null(SmbiosFirmwareInformationReader.Parse(raw));
    }

    [Fact]
    public void TerminatorStopsTraversalAndMissingFieldsDoNotBorrowOtherRecords()
    {
        var end = Record(127, 4, 0xffff);
        var raw = Table(3, 10, Slot(1, 0x11), end, Slot(2, 0x11), Dimm());
        var terminated = SmbiosFirmwareInformationReader.Parse(raw)!;
        Assert.Single(terminated.Declarations.Slots);
        Assert.Empty(terminated.Modules);
        var shortSlot = Record(9, 6, 1, "NAME");
        var parsed = SmbiosFirmwareInformationReader.Parse(Table(3, 10, shortSlot))!;
        var slot = Assert.Single(parsed.Declarations.Slots);
        Assert.Null(slot.Designation); Assert.Null(slot.SlotType);
        Assert.Null(parsed.Declarations.BiosReleaseDate);
    }

    [Fact]
    public void RawReadIsBoundedHandlesSizeGrowthAndDoesNotAllocateOversizeResponses()
    {
        var calls = 0;
        Assert.Null(SmbiosMemoryReader.ReadRawSmbiosData(buffer => { calls++; Assert.Null(buffer); return uint.MaxValue; }));
        Assert.Equal(1, calls);
        calls = 0;
        var expected = Table(3, 10, Bios("09/30/2026"));
        var raw = SmbiosMemoryReader.ReadRawSmbiosData(buffer => {
            calls++;
            if (calls == 1) return 8;
            if (calls == 2) return (uint)expected.Length;
            expected.CopyTo(buffer!, 0);
            return (uint)expected.Length;
        });
        Assert.Equal(3, calls);
        Assert.Equal(expected, raw);
        calls = 0;
        Assert.Null(SmbiosMemoryReader.ReadRawSmbiosData(buffer => { calls++; return (uint)(buffer?.Length + 1 ?? 8); }));
        Assert.Equal(3, calls);
        Assert.Null(SmbiosMemoryReader.ReadRawSmbiosData(_ => 0));
    }

    internal static SmbiosFirmwareDeclarations Declarations() => new("2026-09-30", [
        new("smbios-slot|1234", "SLOT", 0xa5, 0x0d, 4, 4, 0, 0, 0, 0, 0, 0, 0)
    ]) { ProcessorSockets = [new("smbios-cpu-socket|4567", "CPU0")] };

    private static byte[] Slot(ushort handle, byte length, string text = "主板插槽")
    {
        var formatted = new byte[length]; formatted[0] = 9; formatted[1] = length;
        BinaryPrimitives.WriteUInt16LittleEndian(formatted.AsSpan(2), handle);
        formatted[4] = 1; formatted[5] = 0xa5; formatted[6] = 0x0d; formatted[7] = 4; formatted[8] = 4;
        BinaryPrimitives.WriteUInt16LittleEndian(formatted.AsSpan(9), 0x2345); formatted[0x0b] = 4;
        if (length >= 0x11) { formatted[0x0f] = 3; formatted[0x10] = (17 << 3) | 5; }
        return formatted.Concat(Encoding.UTF8.GetBytes(text + "\0\0")).ToArray();
    }

    private static byte[] Bios(string date)
    {
        var raw = Record(0, 0x1a, 0, date); raw[8] = 1; return raw;
    }

    private static byte[] Dimm(string manufacturer = "中文厂商")
    {
        var raw = Record(17, 0x28, 0x4321, "DIMM", manufacturer, "Serial", "Part");
        BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(0x0c), 1024);
        raw[0x10] = 1; raw[0x17] = 2; raw[0x18] = 3; raw[0x1a] = 4; raw[0x12] = 0x22;
        return raw;
    }

    private static byte[] Record(byte type, byte length, ushort handle, params string[] strings)
    {
        var raw = new byte[length]; raw[0] = type; raw[1] = length;
        BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(2), handle);
        return raw.Concat(Encoding.UTF8.GetBytes(strings.Length == 0 ? "\0\0" : string.Join('\0', strings) + "\0\0")).ToArray();
    }

    private static byte[] Table(byte major, byte minor, params byte[][] records)
    {
        var body = records.SelectMany(static record => record).ToArray();
        var raw = new byte[8 + body.Length]; raw[1] = major; raw[2] = minor;
        BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(4), (uint)body.Length); body.CopyTo(raw, 8);
        return raw;
    }
}
