using System.Text.Json;
using System.Text.Json.Nodes;
using ResourceManager.App.Domain.DeviceTopology;
using ResourceManager.App.Infrastructure.DeviceTopology;
using ResourceManager.App.Infrastructure.DeviceTopology.Snapshots;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.ReadWrite.Windows.Devices.Display;

namespace Resource_Manager_APP.Tests;

public sealed class DisplayEdidType3InformationTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RealParserTypedPathDomainAndInformationKeepPartialGeometryAndFieldRate(bool active)
    {
        var path = Path() with { Active = active, TargetAvailable = active };
        var direct = WindowsDisplayPathTopologyReader.ToInformation(path, "recorded-caller");
        Assert.Same(path.Edid!.ModeCapability, direct.ModeCapabilityDeclared);
        var domain = DisplayEdidModeTestData.Snapshot(path);
        var value = DisplayEdidModeInformationCollectorTests.Field(DeviceTopologyInformationProjection.Create(domain, DateTimeOffset.UtcNow)).Value!.Value;
        AssertDeclaration(value);
        Assert.Equal(path.EdidRawDeclaredBase64, domain.Ports[0].Display!.EdidRawDeclaredBase64);
    }

    [Fact]
    public void OpaqueExportImportRoundTripRetainsRatioAndRawByValue()
    {
        var path = Path(); var catalog = new NativeDisplayCoordinatorPayloadCatalog();
        var fact = Assert.Single(Assert.Single(NativeDisplayCoordinatorObservationProjector.Create([path], [], catalog, DateTimeOffset.UtcNow),
            batch => batch.Source == NativeDisplaySource.DisplayConfig).Facts);
        var restored = new NativeDisplayCoordinatorPayloadCatalog(); restored.Import(catalog.Export());
        var actual = Assert.Single(NativeDisplayCoordinatorReadProjection.CreateDisplayPaths(
            [new NativeDisplayNodeOutput { Kind = (uint)NativeDisplayNodeKind.Connector, PrimaryPayloadHandle = fact.PayloadHandle }], restored)).Path;
        var actualCapability = actual.Edid!.ModeCapability!;
        var expectedCapability = path.Edid!.ModeCapability!;
        AssertDeclaration(JsonSerializer.SerializeToElement(actualCapability, Web));
        Assert.Equal(path.EdidRawDeclaredBase64, actual.EdidRawDeclaredBase64);
        Assert.Equal(expectedCapability.Modes[0].RawDescriptor.ToArray(), actualCapability.Modes[0].RawDescriptor.ToArray());
        Assert.Equal(expectedCapability.Modes[0].AspectRatioDeclared, actualCapability.Modes[0].AspectRatioDeclared);
    }

    [Fact]
    public void RatioAloneChangesOriginalOpaqueHandleAndSemanticHashWithoutChangingRaw()
    {
        var path = Path(); var capability = path.Edid!.ModeCapability!;
        var other = path with { Edid = path.Edid! with { ModeCapability = capability with {
            Modes = capability.Modes.SetItem(0, capability.Modes[0] with { AspectRatioDeclared = new(8, null, null) }) } } };
        Assert.Equal(path.EdidRawDeclaredBase64, other.EdidRawDeclaredBase64);
        var catalog = new NativeDisplayCoordinatorPayloadCatalog();
        Assert.NotEqual(catalog.AddDisplayPath(path), catalog.AddDisplayPath(other));
        var comparer = new DeviceTopologySemanticComparer();
        Assert.NotEqual(comparer.ComputeHash(DisplayEdidModeTestData.Snapshot(path)), comparer.ComputeHash(DisplayEdidModeTestData.Snapshot(other)));
    }

    [Fact]
    public void MissingNewPropertyIsLegacyUnknownAndNeverReparsesRawToInventAspect()
    {
        var path = Path(); var node = JsonSerializer.SerializeToNode(path, Web)!.AsObject();
        foreach (var mode in node["edid"]!["modeCapability"]!["modes"]!.AsArray())
            mode!.AsObject().Remove("aspectRatioDeclared");
        var legacy = node.Deserialize<DeviceTopologyDisplayPath>(Web)!;
        Assert.Equal(path.EdidRawDeclaredBase64, legacy.EdidRawDeclaredBase64);
        var capability = legacy.Edid!.ModeCapability!;
        Assert.Equal(2, capability.Modes.Length);
        Assert.All(capability.Modes, mode => Assert.Null(mode.AspectRatioDeclared));
        Assert.Null(capability.Modes[0].Height);
        Assert.Equal(1920u, capability.Modes[0].Width);
        Assert.True(capability.Modes[0].Interlaced);
        Assert.Equal(60u, capability.Modes[0].NominalVerticalRateHz);
        Assert.DoesNotContain("aspectRatioDeclared", JsonSerializer.Serialize(legacy, Web));
    }

    [Fact]
    public void UnrelatedDetailedModeOmitsOnlyNewNullMemberAndKeepsOpaqueAndHashRoundTrip()
    {
        var path = DisplayEdidModeInformationCollectorTests.Path();
        var text = JsonSerializer.Serialize(path, Web);
        Assert.DoesNotContain("aspectRatioDeclared", text);
        var old = JsonSerializer.Deserialize<DeviceTopologyDisplayPath>(text, Web)!;
        Assert.Equal(text, JsonSerializer.Serialize(old, Web));
        Assert.Equal(path.Edid!.ModeCapability!.Modes[0].Timing, old.Edid!.ModeCapability!.Modes[0].Timing);
        var catalog = new NativeDisplayCoordinatorPayloadCatalog(); Assert.Equal(catalog.AddDisplayPath(path), catalog.AddDisplayPath(old));
        var comparer = new DeviceTopologySemanticComparer();
        Assert.Equal(comparer.ComputeHash(DisplayEdidModeTestData.Snapshot(path)), comparer.ComputeHash(DisplayEdidModeTestData.Snapshot(old)));
    }

    [Fact]
    public void LegacyModeJsonHasExactlyItsOriginalOrderedMembersWhenAspectIsAbsent()
    {
        // Independent pre-addition model shape, not constructed from the candidate serializer.
        const string expected = "{\"blockIndex\":1,\"byteOffset\":8,\"encoding\":\"legacy\",\"revision\":0,\"rawDescriptor\":[4],\"code\":null,\"bitIndex\":null,\"width\":null,\"height\":null,\"horizontalPixelRepetition\":null,\"interlaced\":null,\"nominalVerticalRateHz\":null,\"native\":null,\"preferred\":null,\"yCbCr420Only\":null,\"yCbCr420Encoding\":null,\"yCbCr420Supported\":null,\"yCbCr420CapabilityMap\":null,\"containerFlags\":null,\"containerRevision\":null,\"dscPassThrough\":null,\"formula\":null,\"reducedBlanking\":null,\"timing\":null,\"cvtParameters\":null}";
        var legacy = JsonSerializer.Deserialize<EdidModeDeclaration>(expected, Web)!;
        Assert.Null(legacy.AspectRatioDeclared);
        Assert.Equal(expected, JsonSerializer.Serialize(new EdidModeDeclaration(1, 8, "legacy", 0, [4]), Web));
        Assert.Equal(expected, JsonSerializer.Serialize(legacy, Web));
    }

    [Fact]
    public void ReservedLastDescriptorClearsModeInformationWithoutLosingRawOrCurrentResolution()
    {
        var bytes = Bytes(); bytes[139] = 9; Recheck(bytes);
        var path = DisplayEdidModeTestData.Path(WindowsMonitorEdidReader.CreateReadResult(bytes));
        var information = DeviceTopologyInformationProjection.Create(DisplayEdidModeTestData.Snapshot(path), DateTimeOffset.UtcNow);
        Assert.Null(DisplayEdidModeInformationCollectorTests.Field(information).Value);
        Assert.Equal(Convert.ToBase64String(bytes), DisplayHdrInformationCollectorTests.Field(information, "display_all_edid_raw_declared").Value!.Value.GetString());
        Assert.NotNull(DisplayHdrInformationCollectorTests.Field(information, "display_all_resolution_actual").Value);
    }

    private static DeviceTopologyDisplayPath Path() => DisplayEdidModeTestData.Path(WindowsMonitorEdidReader.CreateReadResult(Bytes()));

    // Independent full256B input: legacy1.3/tag05/revision1, interlaced16:9 + explicitly undefined aspect.
    internal static byte[] Bytes()
    {
        var bytes = new byte[256];
        new byte[] { 0, 255, 255, 255, 255, 255, 255, 0 }.CopyTo(bytes, 0);
        bytes[18] = 1; bytes[19] = 4; bytes[20] = 0xB5; bytes[126] = 1;
        for (var offset = 38; offset < 54; offset += 2) { bytes[offset] = 1; bytes[offset + 1] = 1; }
        new byte[] { 0x70, 0x12, 9, 2, 0, 5, 1, 6, 0x14, 0xEF, 0xBB, 8, 0, 0 }.CopyTo(bytes, 128);
        Recheck(bytes); return bytes;
    }

    private static void Recheck(byte[] bytes)
    {
        var end = 133 + bytes[130]; bytes[end] = 0;
        bytes[end] = unchecked((byte)-bytes.AsSpan(129, end - 128).ToArray().Sum(value => (int)value));
        for (var block = 0; block < bytes.Length; block += 128)
        {
            bytes[block + 127] = 0;
            bytes[block + 127] = unchecked((byte)-bytes.AsSpan(block, 127).ToArray().Sum(value => (int)value));
        }
    }

    private static void AssertDeclaration(JsonElement capability)
    {
        var modes = capability.GetProperty("modes"); Assert.Equal(2, modes.GetArrayLength());
        var first = modes[0]; var aspect = first.GetProperty("aspectRatioDeclared");
        Assert.Equal(4, aspect.GetProperty("code").GetInt32());
        Assert.Equal(16u, aspect.GetProperty("horizontal").GetUInt32()); Assert.Equal(9u, aspect.GetProperty("vertical").GetUInt32());
        Assert.Equal(1920u, first.GetProperty("width").GetUInt32()); Assert.Equal(JsonValueKind.Null, first.GetProperty("height").ValueKind);
        Assert.True(first.GetProperty("interlaced").GetBoolean()); Assert.Equal(60u, first.GetProperty("nominalVerticalRateHz").GetUInt32());
        Assert.Equal(JsonValueKind.Null, first.GetProperty("timing").ValueKind);
        Assert.Equal(new uint[] { 0x14, 0xEF, 0xBB }, first.GetProperty("rawDescriptor").EnumerateArray().Select(value => value.GetUInt32()));
        var undefined = modes[1].GetProperty("aspectRatioDeclared"); Assert.Equal(8, undefined.GetProperty("code").GetInt32());
        Assert.Equal(JsonValueKind.Null, undefined.GetProperty("horizontal").ValueKind);
        Assert.Equal(JsonValueKind.Null, undefined.GetProperty("vertical").ValueKind);
    }
}
