using System.Text.Json;
using System.Text.Json.Nodes;
using ResourceManager.App.Domain.DeviceTopology;
using ResourceManager.App.Infrastructure.DeviceTopology;
using ResourceManager.App.Infrastructure.DeviceTopology.Snapshots;
using ResourceManager.App.Infrastructure.NativeCore;

namespace Resource_Manager_APP.Tests;

public sealed class DisplayHdrLuminanceInformationCollectorTests
{
    internal const string Name = "display_all_hdr_luminance_declared";
    internal const string Source = "windows.registry-monitor-edid";

    [Fact]
    public void GoldenCodesDeliverDesiredContentValuesWithCorrectUnitsAndMinDependency()
    {
        var item = Assert.Single(EdidParser.Parse(Bytes())!.HdrLuminanceDeclared!);
        Assert.Equal(1u, item.ExtensionBlock); Assert.Equal(4u, item.DataBlockOffset);
        Assert.Equal(800d, item.DesiredContentMaxLuminanceNits); Assert.Equal(400d, item.DesiredContentMaxFrameAverageLuminanceNits);
        Assert.Equal(0.32, item.DesiredContentMinLuminanceNits!.Value, 12);
        var field = Field(DeviceTopologyInformationProjection.Create(Healthy(), DateTimeOffset.UtcNow));
        Assert.Equal("hdr-luminance-declarations", field.Definition.ValueKind); Assert.Equal("nit", field.Definition.Unit);
        Assert.Equal(Source, field.Definition.SourceId); Assert.Equal("display.windows_registry_edid_cta_desired_content_luminance_declared", field.Definition.Quantity);
        Assert.Equal(item, Assert.Single(field.Value!.Value.Deserialize<DeviceInformationHdrLuminanceDeclaration[]>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!));
    }

    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(31)]
    public void OptionalLengthAndFutureTailNeverInventAbsentValues(int length)
    {
        var payload = new byte[length]; new byte[] { 6, 4, 1 }.CopyTo(payload, 0);
        if (length > 3) payload[3] = 32; if (length > 4) payload[4] = 64; if (length > 5) payload[5] = 255;
        var value = Assert.Single(EdidParser.Parse(Edid(Cta(payload)))!.HdrLuminanceDeclared!);
        Assert.Equal(length > 3 ? 100d : (double?)null, value.DesiredContentMaxLuminanceNits);
        Assert.Equal(length > 4 ? 200d : (double?)null, value.DesiredContentMaxFrameAverageLuminanceNits);
        Assert.Equal(length > 5 ? 1d : (double?)null, value.DesiredContentMinLuminanceNits);
    }

    [Fact]
    public void ZeroCodesAreMissingAndMinimumCannotBorrowAverageOrAnotherBlocksMaximum()
    {
        var first = Cta([6, 0, 0, 0, 64, 255]); var second = Cta([6, 4, 1, 32, 0, 0]);
        var values = EdidParser.Parse(Edid(first, second))!.HdrLuminanceDeclared!;
        Assert.Equal(2, values.Count); Assert.Null(values[0].DesiredContentMaxLuminanceNits); Assert.Null(values[0].DesiredContentMinLuminanceNits);
        Assert.Equal(200d, values[0].DesiredContentMaxFrameAverageLuminanceNits);
        Assert.Equal(100d, values[1].DesiredContentMaxLuminanceNits); Assert.Null(values[1].DesiredContentMaxFrameAverageLuminanceNits); Assert.Null(values[1].DesiredContentMinLuminanceNits);
        var unset = Assert.Single(EdidParser.Parse(Edid(Cta([6, 4, 1, 0, 0, 0])))!.HdrLuminanceDeclared!);
        Assert.Null(unset.DesiredContentMaxLuminanceNits); Assert.Null(unset.DesiredContentMaxFrameAverageLuminanceNits); Assert.Null(unset.DesiredContentMinLuminanceNits);
    }

    [Fact]
    public void LowestNonzeroCodeIsNotRoundedAndFlagsDoNotReplaceLuminanceBytes()
    {
        var value = Assert.Single(EdidParser.Parse(Edid(Cta([6, 255, 255, 1, 1, 1])))!.HdrLuminanceDeclared!);
        Assert.Equal(51.09485743270583, value.DesiredContentMaxLuminanceNits!.Value, 12);
        Assert.Equal(value.DesiredContentMaxLuminanceNits, value.DesiredContentMaxFrameAverageLuminanceNits);
        Assert.True(value.DesiredContentMinLuminanceNits is > 0);
    }

    [Fact]
    public void RepeatedBlocksKeepConflictingDeclarationsAndTheirOrderWithoutMerging()
    {
        var block = Cta([6, 4, 1, 32]);
        block[9] = 0xE4; block[10] = 6; block[11] = 4; block[12] = 1; block[13] = 64; block[2] = 14; Checksum(block);
        var values = EdidParser.Parse(Edid(block, Cta([6, 4, 1, 96])))!.HdrLuminanceDeclared!;
        Assert.Equal(new uint[] { 1, 1, 2 }, values.Select(static value => value.ExtensionBlock));
        Assert.Equal(new uint[] { 4, 9, 4 }, values.Select(static value => value.DataBlockOffset));
        Assert.Equal(new double?[] { 100, 200, 400 }, values.Select(static value => value.DesiredContentMaxLuminanceNits));
        Assert.Throws<NotSupportedException>(() => ((IList<DeviceInformationHdrLuminanceDeclaration>)values).Clear());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    public void NoDataCollectionDoesNotReadHiddenHdrBytes(int offset)
    {
        var block = Cta([6, 4, 1, 128, 96, 51]); block[2] = (byte)offset; Checksum(block);
        Assert.Empty(EdidParser.Parse(Edid(block))!.HdrLuminanceDeclared!);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(128)]
    [InlineData(255)]
    public void InvalidDirectoryOffsetClearsOnlyNewDeclaration(int offset)
    {
        var block = Cta([6, 4, 1, 32]); block[2] = (byte)offset; Checksum(block);
        AssertUnavailableAndRawRetained(Edid(block));
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(3, true)]
    [InlineData(4, true)]
    public void CtaRevisionUsesOnlyKnownDataBlockFormatIncludingSpecifiedFuturePrefix(int revision, bool parsed)
    {
        var block = Cta([6, 4, 1, 32]); block[1] = (byte)revision; Checksum(block);
        var values = EdidParser.Parse(Edid(block))!.HdrLuminanceDeclared!;
        Assert.Equal(parsed ? 1 : 0, values.Count);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void IncompleteHdrBlockOrExtendedTagHasNoTrustedDeclaration(int length)
    {
        var payload = new byte[length]; if (length > 0) payload[0] = 6;
        AssertUnavailableAndRawRetained(Edid(Cta(payload)));
    }

    [Fact]
    public void DataBlockBoundaryNeverConsumesDetailedTimingOrChecksumAndPaddingAlwaysAdvances()
    {
        var block = Cta([6, 4, 1, 32]); block[2] = 8; Checksum(block); AssertUnavailableAndRawRetained(Edid(block));
        block = Cta([6, 4, 1, 32]); block[2] = 127; Checksum(block);
        Assert.Single(EdidParser.Parse(Edid(block))!.HdrLuminanceDeclared!);
        block[126] = 0xE3; Checksum(block); AssertUnavailableAndRawRetained(Edid(block));
    }

    [Fact]
    public void CompleteDeclaredChainAndEachChecksumAreRequiredButUndeclaredTailIsNotParsed()
    {
        var bytes = Bytes(); bytes[127] ^= 1; AssertUnavailableAndRawRetained(bytes);
        bytes = Bytes(); bytes[255] ^= 1; AssertUnavailableAndRawRetained(bytes);
        bytes = Bytes(); bytes[126] = 2; Checksum(bytes.AsSpan(0, 128)); AssertUnavailableAndRawRetained(bytes);
        var unknown = new byte[128]; unknown[0] = 0x70; Checksum(unknown);
        bytes = Edid(Cta([6, 4, 1, 32]), unknown); bytes[383] ^= 1; AssertUnavailableAndRawRetained(bytes);
        bytes = Bytes(); var tail = new byte[bytes.Length + 128]; bytes.CopyTo(tail, 0); Cta([6, 4, 1, 64]).CopyTo(tail, bytes.Length);
        Assert.Single(EdidParser.Parse(tail)!.HdrLuminanceDeclared!);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void BaseRevisionDoesNotUseChromaticitySpecificRestriction(int revision)
    {
        var bytes = Bytes(); bytes[19] = (byte)revision; Checksum(bytes.AsSpan(0, 128));
        Assert.Single(EdidParser.Parse(bytes)!.HdrLuminanceDeclared!);
    }

    [Fact]
    public void UnknownBaseFormatIsEmptyButKnownNoHdrIsSuccessfulEmptyArray()
    {
        var bytes = Bytes(); bytes[19] = 5; Checksum(bytes.AsSpan(0, 128)); AssertUnavailableAndRawRetained(bytes);
        bytes[18] = 2; bytes[19] = 4; Checksum(bytes.AsSpan(0, 128)); AssertUnavailableAndRawRetained(bytes);
        Assert.Empty(EdidParser.Parse(Edid())!.HdrLuminanceDeclared!);
        var block = Cta([5, 4, 1, 32]); Assert.Empty(EdidParser.Parse(Edid(block))!.HdrLuminanceDeclared!);
        var field = Field(DeviceTopologyInformationProjection.Create(Snapshot(Edid()), DateTimeOffset.UtcNow));
        Assert.Equal(JsonValueKind.Array, field.Value!.Value.ValueKind); Assert.Equal(0, field.Value.Value.GetArrayLength());
    }

    [Fact]
    public void ExtensionCountIsBoundedAndLaterCtaBlocksAreReadWithoutExtraIo()
    {
        var extensions = new byte[255][]; for (var index = 0; index < extensions.Length; index++) extensions[index] = new byte[128];
        extensions[254] = Cta([6, 4, 1, 32]);
        Assert.Equal(255u, Assert.Single(EdidParser.Parse(Edid(extensions))!.HdrLuminanceDeclared!).ExtensionBlock);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void CompletedDeclarationDoesNotRequireActivePath(bool complete, bool active)
    {
        var path = Path(Bytes()) with { EdidObservationComplete = complete, Active = active };
        var field = Field(DeviceTopologyInformationProjection.Create(DisplayHdrInformationCollectorTests.Snapshot(path), DateTimeOffset.UtcNow));
        if (complete) Assert.NotNull(field.Value); else Assert.Null(field.Value);
    }

    [Fact]
    public void TypedPayloadAndSemanticHashIncludeDeclarationsButOldPayloadHasNoInventedValue()
    {
        var path = Path(Bytes()); var catalog = new NativeDisplayCoordinatorPayloadCatalog();
        var fact = Assert.Single(Assert.Single(NativeDisplayCoordinatorObservationProjector.Create([path], [], catalog, DateTimeOffset.UtcNow), b => b.Source == NativeDisplaySource.DisplayConfig).Facts);
        var imported = new NativeDisplayCoordinatorPayloadCatalog(); imported.Import(catalog.Export());
        var resolved = Assert.Single(NativeDisplayCoordinatorReadProjection.CreateDisplayPaths(
            [new NativeDisplayNodeOutput { Kind = (uint)NativeDisplayNodeKind.Connector, PrimaryPayloadHandle = fact.PayloadHandle }], imported));
        Assert.Equal(JsonSerializer.Serialize(path), JsonSerializer.Serialize(resolved.Path));
        var changed = path with { Edid = path.Edid! with { HdrLuminanceDeclared = [] } };
        Assert.NotEqual(catalog.AddDisplayPath(path), catalog.AddDisplayPath(changed));
        var comparer = new DeviceTopologySemanticComparer(); Assert.NotEqual(comparer.ComputeHash(DisplayHdrInformationCollectorTests.Snapshot(path)), comparer.ComputeHash(DisplayHdrInformationCollectorTests.Snapshot(changed)));
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web); var node = JsonSerializer.SerializeToNode(path, options)!.AsObject();
        node["edid"]!.AsObject().Remove("hdrLuminanceDeclared"); var old = node.Deserialize<DeviceTopologyDisplayPath>(options)!;
        Assert.Null(Field(DeviceTopologyInformationProjection.Create(DisplayHdrInformationCollectorTests.Snapshot(old), DateTimeOffset.UtcNow)).Value);
        Assert.Null(Field(DeviceTopologyInformationProjection.Create(Healthy(), null)).Value);
    }

    internal static byte[] Bytes() => Edid(Cta([6, 4, 1, 128, 96, 51]));
    internal static byte[] Edid(params byte[][] extensions)
    {
        var bytes = new byte[128 * (extensions.Length + 1)]; DisplayChromaticityInformationCollectorTests.Bytes().CopyTo(bytes, 0);
        bytes[126] = checked((byte)extensions.Length); Checksum(bytes.AsSpan(0, 128));
        for (var index = 0; index < extensions.Length; index++) extensions[index].CopyTo(bytes, (index + 1) * 128);
        return bytes;
    }
    private static byte[] Cta(byte[] payload)
    {
        var block = new byte[128]; block[0] = 2; block[1] = 3; block[2] = checked((byte)(5 + payload.Length));
        block[4] = (byte)(0xE0 | payload.Length); payload.CopyTo(block, 5); Checksum(block); return block;
    }
    private static void Checksum(Span<byte> block)
    {
        block[127] = 0; var sum = 0; foreach (var value in block) sum += value; block[127] = unchecked((byte)-sum);
    }
    private static void AssertUnavailableAndRawRetained(byte[] bytes)
    {
        var result = WindowsMonitorEdidReader.CreateReadResult(bytes); Assert.NotNull(result.Capabilities); Assert.Null(result.Capabilities.HdrLuminanceDeclared);
        Assert.Equal(bytes, Convert.FromBase64String(result.RawEdidDeclaredBase64!));
        Assert.Null(Field(DeviceTopologyInformationProjection.Create(Snapshot(bytes), DateTimeOffset.UtcNow)).Value);
    }
    internal static DeviceTopologyDisplayPath Path(byte[] bytes) => DisplayEdidRawInformationCollectorTests.Path(WindowsMonitorEdidReader.CreateReadResult(bytes));
    internal static DeviceTopologySnapshot Snapshot(byte[] bytes) => DisplayHdrInformationCollectorTests.Snapshot(Path(bytes));
    internal static DeviceTopologySnapshot Healthy() => Snapshot(Bytes());
    internal static DeviceInformationField Field(DeviceInformationSnapshot snapshot) => DisplayHdrInformationCollectorTests.Field(snapshot, Name);
}
