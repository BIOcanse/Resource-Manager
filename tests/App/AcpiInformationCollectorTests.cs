using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using ResourceManager.App.Domain.DeviceTopology;
using ResourceManager.ReadWrite.Windows.Devices;
using ResourceManager.App.Infrastructure.DeviceTopology.Snapshots;

namespace Resource_Manager_APP.Tests;

public sealed class AcpiInformationCollectorTests
{
    [Fact]
    public void EnumerationPassesLittleEndianIdsUnchangedAndFetchesDuplicateSignatureOnlyOnce()
    {
        var facp = Id("FACP");
        var ssdt = Id("SSDT");
        var requests = new List<uint>();
        var api = new Api(buffer => Fill(Ids(facp, ssdt, ssdt), buffer), (id, buffer) => {
            requests.Add(id);
            return Fill(Raw(id), buffer);
        });
        var tables = Assert.IsAssignableFrom<IReadOnlyList<AcpiFirmwareTable>>(Reader(api).ReadTables());
        Assert.Equal(0x50434146u, facp);
        Assert.Equal(2, tables.Count);
        Assert.Equal(2u, tables.Single(table => table.Signature == "SSDT").ReportedInstanceCount);
        Assert.Equal(2, requests.Count(id => id == ssdt)); // One size call plus one fetch.
        Assert.All(tables, table => Assert.Equal(table.Signature, Encoding.ASCII.GetString(table.FirstTable!.Raw, 0, 4)));
    }

    [Fact]
    public void ValidHeaderKeepsExactDeclaredBytesAndIndependentRevisionDomains()
    {
        var raw = Raw(Id("FACP"), 44);
        var withExtra = raw.Concat(new byte[] { 0xff, 0x55 }).ToArray();
        var result = Assert.IsType<AcpiDescriptionTable>(WindowsAcpiFirmwareTableReader.ParseTable(Id("FACP"), withExtra));
        Assert.Equal(raw, result.Raw);
        Assert.Equal(6, result.Revision);
        Assert.Equal(" OEM", result.OemId); // Leading spaces are preserved.
        Assert.Equal("TABLE", result.OemTableId);
        Assert.Equal(uint.MaxValue, result.OemRevision);
        Assert.Equal("TEST", result.CreatorId);
        Assert.Equal(0u, result.CreatorRevision); // A legal declaration, not absence.
        var fields = WindowsInformationFieldProjection.CreateAcpi([new(Id("FACP"), "FACP", 1, result)], DateTimeOffset.UtcNow);
        var device = Assert.Single(fields.Devices);
        Assert.Equal("windows-acpi|first-by-signature|50434146", device.DeviceIdentity);
        Assert.Equal("firmware_table", device.ObjectType);
        Assert.Equal(10, device.Fields.Count);
        Assert.Equal("4294967295", Field(fields, "firmware_all_acpi_oem_revision_declared").Value!.Value.GetString());
        Assert.Equal("0", Field(fields, "firmware_all_acpi_creator_revision_declared").Value!.Value.GetString());
        Assert.Equal(raw, Field(fields, "firmware_all_acpi_table_raw").Value!.Value.GetBytesFromBase64());
        Assert.Equal("firmware.acpi_first_table_revision", Field(fields, "firmware_all_acpi_table_revision").Definition.Quantity);
        Assert.Equal("version8", Field(fields, "firmware_all_acpi_table_revision").Definition.Unit);
        Assert.Equal("windows.acpi.tables", Field(fields, "firmware_all_acpi_signature_instance_count_reported").Definition.SourceId);
        Assert.All(device.Fields, field => {
            Assert.Matches("^firmware_all_[a-z0-9_]+$", field.Definition.Name);
            Assert.Equal("firmware", field.Definition.Resource);
            Assert.Equal("all", field.Definition.Vendor);
        });
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(fields, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        Assert.Equal(10, json.RootElement.GetProperty("devices")[0].GetProperty("fields").GetArrayLength());
    }

    [Theory]
    [InlineData("short")]
    [InlineData("length-short")]
    [InlineData("length-too-large")]
    [InlineData("checksum")]
    [InlineData("signature")]
    [InlineData("facs")]
    public void InvalidTablesDoNotYieldContent(string corruption)
    {
        var raw = Raw(Id("FACP"));
        var requested = Id("FACP");
        switch (corruption)
        {
            case "short": raw = raw[..35]; break;
            case "length-short": BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(4), 35); Checksum(raw); break;
            case "length-too-large": BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(4), uint.MaxValue); Checksum(raw); break;
            case "checksum": raw[9]++; break;
            case "signature": requested = Id("SSDT"); break;
            case "facs": requested = Id("FACS"); BinaryPrimitives.WriteUInt32LittleEndian(raw, requested); Checksum(raw); break;
        }
        Assert.Null(WindowsAcpiFirmwareTableReader.ParseTable(requested, raw));
    }

    [Fact]
    public void InvalidAsciiClearsOnlyItsFieldAndRetainsTrustedRawTable()
    {
        var raw = Raw(Id("FACP"));
        raw[12] = 0x80;
        raw[17] = 0; // Embedded NUL before printable content is not padding.
        Checksum(raw);
        var result = Assert.IsType<AcpiDescriptionTable>(WindowsAcpiFirmwareTableReader.ParseTable(Id("FACP"), raw));
        Assert.Null(result.OemId);
        Assert.Null(result.OemTableId);
        Assert.Equal("TEST", result.CreatorId);
        Assert.Equal(raw, result.Raw);
    }

    [Fact]
    public void FacsIsDeclaredFromEnumerationButNeverFetchedOrParsedAsCommonHeader()
    {
        var gets = 0;
        var result = Reader(new Api(buffer => Fill(Ids(Id("FACS")), buffer), (_, _) => {
            gets++;
            throw new InvalidOperationException("FACS must not be requested.");
        })).ReadTables();
        var table = Assert.Single(result!);
        Assert.Equal(0, gets);
        Assert.Null(table.FirstTable);
        var fields = WindowsInformationFieldProjection.CreateAcpi(result!, DateTimeOffset.UtcNow);
        Assert.Equal("FACS", Field(fields, "firmware_all_acpi_table_signature").Value!.Value.GetString());
        Assert.Null(Field(fields, "firmware_all_acpi_table_revision").Value);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(5u)]
    [InlineData(uint.MaxValue)]
    public void UnavailableMalformedOrOversizedEnumerationDoesNotInventAnEmptySuccess(uint bytes)
    {
        var gets = 0;
        var result = Reader(new Api(buffer => new(bytes, 5), (_, _) => { gets++; return new(0, 5); })).ReadTables();
        Assert.Null(result);
        Assert.Equal(0, gets);
    }

    [Fact]
    public void SizeGrowthRetriesWithNewSizeAndStillValidatesReturnedCount()
    {
        var enumerationCalls = 0;
        var gets = 0;
        var raw = Raw(Id("FACP"));
        var result = Reader(new Api(buffer => {
            enumerationCalls++;
            return enumerationCalls switch { 1 => new(4, 0), 2 => new(8, 122), _ => Fill(Ids(Id("FACP"), Id("FACP")), buffer) };
        }, (_, buffer) => {
            gets++;
            return gets switch { 1 => new(36, 0), 2 => new((uint)raw.Length, 122), _ => Fill(raw, buffer) };
        })).ReadTables();
        var table = Assert.Single(result!);
        Assert.Equal(2u, table.ReportedInstanceCount);
        Assert.NotNull(table.FirstTable);
        Assert.Equal(3, enumerationCalls);
        Assert.Equal(3, gets);
    }

    [Fact]
    public void RepeatedSizeGrowthIsBoundedAndDoesNotPublishPartialEnumeration()
    {
        var calls = 0;
        var result = Reader(new Api(buffer => { calls++; return new((uint)(buffer?.Length + 4 ?? 4), 122); }, (_, _) => throw new Exception())).ReadTables();
        Assert.Null(result);
        Assert.Equal(3, calls);
    }

    [Fact]
    public void PerTableFailureKeepsEnumDeclarationsAndOtherTableCurrentValues()
    {
        var result = Reader(new Api(buffer => Fill(Ids(Id("FACP"), Id("SSDT")), buffer),
            (id, buffer) => id == Id("FACP") ? new(0, 5) : Fill(Raw(id), buffer))).ReadTables()!;
        Assert.Null(result.Single(t => t.Signature == "FACP").FirstTable);
        Assert.NotNull(result.Single(t => t.Signature == "SSDT").FirstTable);
        var projected = WindowsInformationFieldProjection.CreateAcpi(result, DateTimeOffset.UtcNow);
        Assert.Equal(2, projected.Devices.Count);
        Assert.Equal("1", projected.Devices[0].Fields.Single(f => f.Definition.Name == "firmware_all_acpi_signature_instance_count_reported").Value!.Value.GetString());
    }

    [Fact]
    public void OversizedTableDoesNotAllocateOrCallFetch()
    {
        var gets = 0;
        var table = Assert.Single(Reader(new Api(buffer => Fill(Ids(Id("FACP")), buffer), (_, buffer) => {
            Assert.Null(buffer);
            gets++;
            return new(uint.MaxValue, 0);
        })).ReadTables()!);
        Assert.Null(table.FirstTable);
        Assert.Equal(1, gets);
    }

    [Fact]
    public void AggregateAllocationBudgetStopsFurtherTableRequests()
    {
        var calls = 0;
        var result = Reader(new Api(buffer => Fill(Ids(Id("T000"), Id("T001"), Id("T002"), Id("T003"), Id("T004")), buffer),
            (_, buffer) => { calls++; return new((uint)WindowsAcpiFirmwareTableReader.MaximumTableBytes, 0); })).ReadTables()!;
        Assert.Equal(5, result.Count);
        Assert.All(result, table => Assert.Null(table.FirstTable));
        Assert.Equal(8, calls); // Four allocated buffers; fifth still has declarations only.
    }

    internal static DeviceInformationField Field(DeviceInformationSnapshot snapshot, string name)
        => AcpiFirmwareTestData.Field(snapshot, name);

    internal static uint Id(string signature) => AcpiFirmwareTestData.Id(signature);

    internal static byte[] Raw(uint id, int length = 40) => AcpiFirmwareTestData.Raw(id, length);

    internal static IReadOnlyList<AcpiFirmwareTable> Tables(byte revision = 6) => AcpiFirmwareTestData.Tables(revision);

    private static void Checksum(byte[] raw) => AcpiFirmwareTestData.Checksum(raw);

    private static byte[] Ids(params uint[] ids)
    {
        var raw = new byte[ids.Length * 4];
        for (var i = 0; i < ids.Length; i++) BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(i * 4), ids[i]);
        return raw;
    }

    private static WindowsFirmwareCallResult Fill(byte[] raw, byte[]? buffer)
    {
        if (buffer is not null && raw.Length <= buffer.Length) raw.CopyTo(buffer, 0);
        return new((uint)raw.Length, 0);
    }

    private static WindowsAcpiFirmwareTableReader Reader(IWindowsAcpiFirmwareApi api)
        => new(api);

    private sealed class Api(Func<byte[]?, WindowsFirmwareCallResult> enumerate,
        Func<uint, byte[]?, WindowsFirmwareCallResult> get) : IWindowsAcpiFirmwareApi
    {
        public WindowsFirmwareCallResult Enumerate(uint provider, byte[]? buffer)
        {
            Assert.Equal(0x41435049u, provider);
            return enumerate(buffer);
        }
        public WindowsFirmwareCallResult GetTable(uint provider, uint tableId, byte[]? buffer)
        {
            Assert.Equal(0x41435049u, provider);
            return get(tableId, buffer);
        }
    }
}
