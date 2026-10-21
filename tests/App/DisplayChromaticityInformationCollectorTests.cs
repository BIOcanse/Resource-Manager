using System.Text.Json;
using System.Text.Json.Nodes;
using ResourceManager.App.Domain.DeviceTopology;
using ResourceManager.App.Infrastructure.DeviceTopology;
using ResourceManager.App.Infrastructure.DeviceTopology.Snapshots;
using ResourceManager.App.Infrastructure.NativeCore;

namespace Resource_Manager_APP.Tests;

public sealed class DisplayChromaticityInformationCollectorTests
{
    internal const string Name = "display_all_chromaticity_declared";
    internal const string Source = "windows.registry-monitor-edid";

    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    public void StaticBitPairsPreserveEveryCoordinateAndAllTenBits(int revision)
    {
        var value = EdidParser.Parse(Bytes(revision))!.ChromaticityDeclared!;
        Assert.Equal(new(0, 1 / 1024d), value.Red);
        Assert.Equal(new(2 / 1024d, 3 / 1024d), value.Green);
        Assert.Equal(new(4 / 1024d, 511 / 1024d), value.Blue);
        Assert.Equal(new(512 / 1024d, 1023 / 1024d), value.DefaultWhite);
        var field = Field(DeviceTopologyInformationProjection.Create(Healthy(revision), DateTimeOffset.UtcNow));
        Assert.Equal(value, field.Value!.Value.Deserialize<DeviceInformationChromaticity>(new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        Assert.Equal("chromaticity", field.Definition.ValueKind); Assert.Equal("coordinate", field.Definition.Unit);
        Assert.Equal(Source, field.Definition.SourceId);
        Assert.Equal("display.windows_registry_edid_base_chromaticity_declared_xy", field.Definition.Quantity);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    public void VesaBinaryFractionExamplesAreDecodedWithoutDecimalRounding(int revision)
    {
        var bytes = Bytes(revision);
        // Table 3.17: 1001110001, 0100111010, 0010011010 (625, 314, 154).
        new byte[] { 0x68, 0, 156, 78, 38, 0, 0, 0, 0, 0 }.CopyTo(bytes, 25); Checksum(bytes);
        var value = EdidParser.Parse(bytes)!.ChromaticityDeclared!;
        Assert.Equal(0.6103515625, value.Red!.X); Assert.Equal(0.306640625, value.Red.Y);
        Assert.Equal(0.150390625, value.Green!.X);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void DigitalEncodingFlagsAllIncludeRgb(int type)
    {
        var bytes = Bytes(); bytes[24] = (byte)(type << 3); Checksum(bytes);
        var value = EdidParser.Parse(bytes)!.ChromaticityDeclared!;
        Assert.NotNull(value.Red); Assert.NotNull(value.Green); Assert.NotNull(value.Blue);
        Assert.Equal(new(0.5, 1023 / 1024d), value.DefaultWhite);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(3, false)]
    public void AnalogColorTypeDoesNotInventPrimariesButRetainsDefaultWhite(int type, bool rgb)
    {
        var bytes = Bytes(); bytes[20] = 0; bytes[24] = (byte)(type << 3); Checksum(bytes);
        var value = EdidParser.Parse(bytes)!.ChromaticityDeclared!;
        Assert.Equal(rgb, value.Red is not null); Assert.Equal(rgb, value.Green is not null); Assert.Equal(rgb, value.Blue is not null);
        Assert.Equal(new(0.5, 1023 / 1024d), value.DefaultWhite);
        var field = Field(DeviceTopologyInformationProjection.Create(Snapshot(bytes), DateTimeOffset.UtcNow));
        if (!rgb) Assert.Equal(JsonValueKind.Null, field.Value!.Value.GetProperty("red").ValueKind);
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(1, 1)]
    [InlineData(1, 2)]
    [InlineData(1, 5)]
    [InlineData(0, 4)]
    [InlineData(2, 4)]
    [InlineData(255, 255)]
    public void UnreviewedVersionsKeepRawAndExistingCapabilitiesButHaveNoNewCoordinates(int version, int revision)
    {
        var bytes = Bytes(); bytes[18] = (byte)version; bytes[19] = (byte)revision; Checksum(bytes);
        var result = WindowsMonitorEdidReader.CreateReadResult(bytes);
        Assert.NotNull(result.Capabilities); Assert.Null(result.Capabilities.ChromaticityDeclared);
        Assert.Equal(bytes, Convert.FromBase64String(result.RawEdidDeclaredBase64!));
        Assert.Null(Field(DeviceTopologyInformationProjection.Create(Snapshot(bytes), DateTimeOffset.UtcNow)).Value);
    }

    [Theory]
    [InlineData(3, 0)]
    [InlineData(3, 34)]
    [InlineData(3, 127)]
    [InlineData(4, 0)]
    [InlineData(4, 34)]
    [InlineData(4, 127)]
    public void TruncatedBaseBlockDoesNotProduceCoordinates(int revision, int length)
    {
        var bytes = Bytes(revision)[..length]; Assert.Null(EdidParser.Parse(bytes));
        Assert.Equal(bytes, Convert.FromBase64String(WindowsMonitorEdidReader.CreateReadResult(bytes).RawEdidDeclaredBase64!));
    }

    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    public void InvalidHeaderOrChecksumDoesNotEraseRawAndDoesNotChangeExistingParserBehavior(int revision)
    {
        var bytes = Bytes(revision); bytes[127] ^= 1;
        var result = WindowsMonitorEdidReader.CreateReadResult(bytes);
        Assert.NotNull(result.Capabilities); Assert.Null(result.Capabilities.ChromaticityDeclared);
        Assert.Equal($"EDID 1.{revision}", result.Capabilities.Version); Assert.Equal<uint?>(revision == 4 ? 10u : null, result.Capabilities.BitsPerColorChannel);
        Assert.Equal(bytes, Convert.FromBase64String(result.RawEdidDeclaredBase64!));
        bytes[0] = 1; Assert.Null(EdidParser.Parse(bytes));
        Assert.Equal(bytes, Convert.FromBase64String(WindowsMonitorEdidReader.CreateReadResult(bytes).RawEdidDeclaredBase64!));
    }

    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    public void OnlyBaseChecksumMattersAndSrgbFlagNeverReplacesDeclaredCoordinates(int revision)
    {
        var bytes = Bytes(revision); bytes[24] |= 4; bytes[126] = 1; Checksum(bytes);
        var extended = new byte[256]; bytes.CopyTo(extended, 0); Array.Fill(extended, (byte)13, 128, 128);
        Assert.Equal(EdidParser.Parse(Bytes(revision))!.ChromaticityDeclared, EdidParser.Parse(extended)!.ChromaticityDeclared);
        Array.Clear(bytes, 25, 10); Checksum(bytes);
        var zero = EdidParser.Parse(bytes)!.ChromaticityDeclared!;
        Assert.Equal(new(0, 0), zero.Red); Assert.Equal(new(0, 0), zero.DefaultWhite);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void DeclarationRequiresCompletedEdidButNotAnActivePath(bool complete, bool active)
    {
        var path = Path(Bytes()) with { EdidObservationComplete = complete, Active = active };
        var fields = DeviceTopologyInformationProjection.Create(DisplayHdrInformationCollectorTests.Snapshot(path), DateTimeOffset.UtcNow);
        if (complete) Assert.NotNull(Field(fields).Value); else Assert.Null(Field(fields).Value);
        var rotation = DisplayHdrInformationCollectorTests.Field(fields, DisplayRotationInformationCollectorTests.Name).Value;
        if (active) Assert.Equal("90", rotation!.Value.GetString()); else Assert.Null(rotation);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    public void OriginalTypedPayloadRoundTripsCoordinatesWithoutAnotherIoOrAbi(int revision)
    {
        var path = Path(Bytes(revision)); var catalog = new NativeDisplayCoordinatorPayloadCatalog();
        var fact = Assert.Single(Assert.Single(NativeDisplayCoordinatorObservationProjector.Create([path], [], catalog, DateTimeOffset.UtcNow),
            b => b.Source == NativeDisplaySource.DisplayConfig).Facts);
        var restored = new NativeDisplayCoordinatorPayloadCatalog(); restored.Import(catalog.Export());
        var resolved = Assert.Single(NativeDisplayCoordinatorReadProjection.CreateDisplayPaths(
            [new NativeDisplayNodeOutput { Kind = (uint)NativeDisplayNodeKind.Connector, PrimaryPayloadHandle = fact.PayloadHandle }], restored));
        Assert.Equal(JsonSerializer.Serialize(path), JsonSerializer.Serialize(resolved.Path));
        Assert.Equal(path.Edid!.ChromaticityDeclared, resolved.Path.Edid!.ChromaticityDeclared);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    public void DeclarationChangesSemanticHashButOldPayloadAndUnobservedSnapshotHaveNoValue(int revision)
    {
        var path = Path(Bytes(revision)); var changed = path with { Edid = path.Edid! with {
            ChromaticityDeclared = path.Edid.ChromaticityDeclared! with { DefaultWhite = new(0.25, 0.5) } } };
        var comparer = new DeviceTopologySemanticComparer();
        Assert.NotEqual(comparer.ComputeHash(DisplayHdrInformationCollectorTests.Snapshot(path)), comparer.ComputeHash(DisplayHdrInformationCollectorTests.Snapshot(changed)));
        var catalog = new NativeDisplayCoordinatorPayloadCatalog(); Assert.NotEqual(catalog.AddDisplayPath(path), catalog.AddDisplayPath(changed));
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web); var node = JsonSerializer.SerializeToNode(path, options)!.AsObject();
        node["edid"]!.AsObject().Remove("chromaticityDeclared"); var old = node.Deserialize<DeviceTopologyDisplayPath>(options)!;
        Assert.Null(old.Edid!.ChromaticityDeclared);
        Assert.Null(Field(DeviceTopologyInformationProjection.Create(DisplayHdrInformationCollectorTests.Snapshot(old), DateTimeOffset.UtcNow)).Value);
        var field = Field(DeviceTopologyInformationProjection.Create(Healthy(revision), null)); Assert.Null(field.Value);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(field, new JsonSerializerOptions(JsonSerializerDefaults.Web) {
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull }));
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("value").ValueKind);
    }

    [Theory]
    [InlineData(false, 0, false)]
    [InlineData(false, 1, true)]
    [InlineData(false, 2, false)]
    [InlineData(false, 3, false)]
    [InlineData(true, 0, false)]
    [InlineData(true, 1, true)]
    [InlineData(true, 2, false)]
    [InlineData(true, 3, false)]
    public void LegacyDisplayTypeControlsPrimariesForBothInputSignals(bool digital, int type, bool rgb)
    {
        var bytes = Bytes(3); bytes[20] = digital ? (byte)0x81 : (byte)0; bytes[24] = (byte)(type << 3); Checksum(bytes);
        var result = WindowsMonitorEdidReader.CreateReadResult(bytes);
        var value = result.Capabilities!.ChromaticityDeclared!;
        Assert.Equal(rgb, value.Red is not null); Assert.Equal(rgb, value.Green is not null); Assert.Equal(rgb, value.Blue is not null);
        Assert.Equal(new(0.5, 1023 / 1024d), value.DefaultWhite);
        Assert.Equal(bytes, Convert.FromBase64String(result.RawEdidDeclaredBase64!));
        var projected = Field(DeviceTopologyInformationProjection.Create(Snapshot(bytes), DateTimeOffset.UtcNow)).Value!.Value;
        Assert.Equal(value, projected.Deserialize<DeviceInformationChromaticity>(new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(false, 2)]
    [InlineData(false, 3)]
    [InlineData(true, 0)]
    [InlineData(true, 2)]
    [InlineData(true, 3)]
    public void LegacySrgbFlagDoesNotInventMissingPrimaries(bool digital, int type)
    {
        var bytes = Bytes(3); bytes[20] = digital ? (byte)0x81 : (byte)0; bytes[24] = (byte)((type << 3) | 4); Checksum(bytes);
        var value = EdidParser.Parse(bytes)!.ChromaticityDeclared!;
        Assert.Null(value.Red); Assert.Null(value.Green); Assert.Null(value.Blue);
        Assert.Equal(new(0.5, 1023 / 1024d), value.DefaultWhite);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LegacyRgbPreservesZeroAndMaximumBinaryFractions(bool digital)
    {
        var bytes = Bytes(3); bytes[20] = digital ? (byte)0x81 : (byte)0;
        Array.Clear(bytes, 25, 10); Checksum(bytes);
        var zero = EdidParser.Parse(bytes)!.ChromaticityDeclared!;
        Assert.Equal(new(0, 0), zero.Red); Assert.Equal(new(0, 0), zero.Green);
        Assert.Equal(new(0, 0), zero.Blue); Assert.Equal(new(0, 0), zero.DefaultWhite);
        Array.Fill(bytes, (byte)255, 25, 10); Checksum(bytes);
        var maximum = EdidParser.Parse(bytes)!.ChromaticityDeclared!;
        var expected = new DeviceInformationChromaticityPoint(1023 / 1024d, 1023 / 1024d);
        Assert.Equal(expected, maximum.Red); Assert.Equal(expected, maximum.Green);
        Assert.Equal(expected, maximum.Blue); Assert.Equal(expected, maximum.DefaultWhite);
    }

    internal static byte[] Bytes(int revision = 4)
    {
        var bytes = new byte[128]; new byte[] { 0, 255, 255, 255, 255, 255, 255, 0 }.CopyTo(bytes, 0);
        bytes[18] = 1; bytes[19] = (byte)revision; bytes[20] = revision == 3 ? (byte)0x81 : (byte)0xB5;
        if (revision == 3) bytes[24] = 8; // EDID 1.3 RGB display type, independent of input signal.
        // Independent static bit-pattern: raw coordinates 0,1,2,3,4,511,512,1023.
        new byte[] { 0x1B, 0x33, 0, 0, 0, 0, 1, 127, 128, 255 }.CopyTo(bytes, 25);
        Checksum(bytes); return bytes;
    }
    private static void Checksum(byte[] bytes)
    {
        bytes[127] = 0; var sum = bytes.Take(128).Sum(static value => (int)value); bytes[127] = unchecked((byte)-sum);
    }
    internal static DeviceTopologyDisplayPath Path(byte[] bytes) => DisplayEdidRawInformationCollectorTests.Path(WindowsMonitorEdidReader.CreateReadResult(bytes));
    internal static DeviceTopologySnapshot Snapshot(byte[] bytes) => DisplayHdrInformationCollectorTests.Snapshot(Path(bytes));
    internal static DeviceTopologySnapshot Healthy(int revision = 4) => Snapshot(Bytes(revision));
    internal static DeviceInformationField Field(DeviceInformationSnapshot snapshot) => DisplayHdrInformationCollectorTests.Field(snapshot, Name);
}
