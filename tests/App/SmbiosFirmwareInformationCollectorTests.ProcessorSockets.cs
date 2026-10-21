using System.Text.Json;
using ResourceManager.App.Domain.DeviceTopology;
using ResourceManager.ReadWrite.Windows.Devices;
using ResourceManager.App.Infrastructure.DeviceTopology.Snapshots;

namespace Resource_Manager_APP.Tests;

public sealed partial class SmbiosFirmwareInformationCollectorTests
{
    [Theory]
    [InlineData(2, 0, 0x1a)]
    [InlineData(2, 1, 0x20)]
    [InlineData(2, 2, 0x20)]
    [InlineData(2, 3, 0x23)]
    [InlineData(2, 4, 0x23)]
    [InlineData(2, 5, 0x28)]
    [InlineData(2, 6, 0x2a)]
    [InlineData(2, 8, 0x2a)]
    [InlineData(3, 0, 0x30)]
    [InlineData(3, 5, 0x30)]
    [InlineData(3, 6, 0x32)]
    [InlineData(3, 7, 0x32)]
    [InlineData(3, 8, 0x33)]
    [InlineData(3, 9, 0x33)]
    public void ProcessorSocketUsesFullFormatLengthForDeclaredVersion(byte major, byte minor, byte minimum)
    {
        var good = SmbiosFirmwareInformationReader.Parse(Table(major, minor, Processor(0x1234, minimum, "CPU0")))!;
        Assert.Equal("CPU0", Assert.Single(good.Declarations.ProcessorSockets).Designation);
        var shortRecord = SmbiosFirmwareInformationReader.Parse(Table(major, minor, Processor(0x1234, (byte)(minimum - 1), "CPU0")))!;
        Assert.Null(Assert.Single(shortRecord.Declarations.ProcessorSockets).Designation);
    }

    [Fact]
    public void CpuSocketsShareRawCaptureWithBiosSlotsAndDimmsAndKeepRecordIdentities()
    {
        var raw = SocketTable(Processor(0x1234, 0x33, "主插槽"), Processor(0x4321, 0x40, "主插槽"));
        var reads = 0;
        var capture = SmbiosFirmwareInformationReader.Read(() => { reads++; return raw; })!;
        Assert.Equal(1, reads);
        Assert.Single(capture.Declarations.Slots);
        Assert.Single(capture.Modules);
        Assert.Equal("2026-09-30", capture.Declarations.BiosReleaseDate);
        Assert.Equal(2, capture.Declarations.ProcessorSockets.Count);
        var snapshot = DeviceInformationTestData.Snapshot() with { FirmwareDeclarations = capture.Declarations };
        var fields = DeviceTopologyInformationProjection.Create(snapshot, snapshot.CapturedAt);
        var sockets = fields.Devices.Where(static device => device.ObjectType == "cpu_socket").ToArray();
        Assert.Equal(2, sockets.Length);
        Assert.All(sockets, socket => {
            var field = Assert.Single(socket.Fields);
            Assert.Equal("cpu_all_socket_designation", field.Definition.Name);
            Assert.Equal("cpu", field.Definition.Resource);
            Assert.Equal("all", field.Definition.Vendor);
            Assert.Equal("smbios.type4", field.Definition.SourceId);
            Assert.Equal("cpu.socket_designation_declared", field.Definition.Quantity);
            Assert.Equal("主插槽", field.Value!.Value.GetString());
        });
        Assert.Equal(new[] { "smbios-cpu-socket|1234", "smbios-cpu-socket|4321" }, sockets.Select(static socket => socket.DeviceIdentity));
        var persisted = JsonSerializer.Deserialize<DeviceTopologySnapshot>(JsonSerializer.Serialize(snapshot))!;
        var startup = DeviceTopologyInformationProjection.Create(persisted, null);
        Assert.All(startup.Devices.Where(static device => device.ObjectType == "cpu_socket").SelectMany(static device => device.Fields),
            static field => Assert.Null(field.Value));
        var comparer = new DeviceTopologySemanticComparer();
        Assert.Equal(comparer.ComputeHash(snapshot), comparer.ComputeHash(snapshot with {
            FirmwareDeclarations = capture.Declarations with { ProcessorSockets = capture.Declarations.ProcessorSockets.Reverse().ToArray() }
        }));
        Assert.NotEqual(comparer.ComputeHash(snapshot), comparer.ComputeHash(snapshot with {
            FirmwareDeclarations = capture.Declarations with { ProcessorSockets = [capture.Declarations.ProcessorSockets[0] with { Designation = null }] }
        }));
    }

    [Theory]
    [InlineData(0, "CPU0")]
    [InlineData(2, "CPU0")]
    [InlineData(1, " ")]
    [InlineData(1, "CPU\n0")]
    public void MissingOrInvalidSocketTextRemainsAnEmptyValue(byte index, string text)
    {
        var record = Processor(1, 0x33, text); record[4] = index;
        var capture = SmbiosFirmwareInformationReader.Parse(Table(3, 9, record))!;
        Assert.Null(Assert.Single(capture.Declarations.ProcessorSockets).Designation);
    }

    [Fact]
    public void SocketTextUsesExistingEncodingRulesAndInvalidUtf8IsEmpty()
    {
        Assert.Null(Assert.Single(SmbiosFirmwareInformationReader.Parse(Table(3, 4, Processor(1, 0x30, "中文")))!
            .Declarations.ProcessorSockets).Designation);
        Assert.Equal("中文", Assert.Single(SmbiosFirmwareInformationReader.Parse(Table(3, 5, Processor(1, 0x30, "中文")))!
            .Declarations.ProcessorSockets).Designation);
        var bad = Processor(1, 0x33, "X"); bad[0x33] = 0xff;
        Assert.Null(Assert.Single(SmbiosFirmwareInformationReader.Parse(Table(3, 9, bad))!.Declarations.ProcessorSockets).Designation);
    }

    [Fact]
    public void NonCpuReservedAndConflictingProcessorRecordsNeverBecomeCpuSocketFacts()
    {
        var nonCpu = Processor(2, 0x33, "DSP"); nonCpu[5] = 5;
        var conflict = Processor(1, 0x33, "VIDEO"); conflict[5] = 6;
        var records = Table(3, 9, Processor(1, 0x33, "CPU0"), conflict, nonCpu,
            Processor(0xfffe, 0x33, "reserved"), Processor(0xffff, 0x33, "reserved"), Processor(0xfffd, 0x33, "CPU1"));
        var socket = Assert.Single(SmbiosFirmwareInformationReader.Parse(records)!.Declarations.ProcessorSockets);
        Assert.Equal("smbios-cpu-socket|fffd", socket.DeviceIdentity);
        Assert.Equal("CPU1", socket.Designation);
    }

    internal static byte[] SocketTable(params byte[][] processors)
        => Table(3, 9, [Bios("09/30/2026"), Slot(0x1111, 0x11), Dimm(), .. processors]);

    internal static byte[] Processor(ushort handle, byte length = 0x33, string text = "CPU0")
    {
        var raw = Record(4, length, handle, text); raw[4] = 1; raw[5] = 3;
        // An unpopulated socket still has a firmware reference designation.
        return raw;
    }
}
