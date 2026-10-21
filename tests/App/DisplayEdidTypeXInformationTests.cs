using System.Text.Json;
using System.Text.Json.Nodes;
using ResourceManager.App.Infrastructure.DeviceTopology;
using ResourceManager.App.Infrastructure.DeviceTopology.Snapshots;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.ReadWrite.Windows.Devices.Display;
using ResourceManager.TestFixtures;

namespace Resource_Manager_APP.Tests;

public sealed class DisplayEdidTypeXInformationTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RealTypedPathDomainAndInformationRetainCvtDeclarationsOnActiveOrInactivePath(bool active)
    {
        var path = DisplayEdidModeTestData.Path(WindowsMonitorEdidReader.CreateReadResult(Bytes())) with { Active = active, TargetAvailable = active };
        var direct = WindowsDisplayPathTopologyReader.ToInformation(path, "recorded-caller");
        Assert.Same(path.Edid!.ModeCapability, direct.ModeCapabilityDeclared);
        var domain = DisplayEdidModeTestData.Snapshot(path);
        AssertValue(DisplayEdidModeInformationCollectorTests.Field(DeviceTopologyInformationProjection.Create(domain, DateTimeOffset.UtcNow)).Value!.Value);
        Assert.Equal(path.EdidRawDeclaredBase64, domain.Ports[0].Display!.EdidRawDeclaredBase64);
    }

    [Fact]
    public void OpaqueRoundTripAndSemanticHashUseActualCvtValuesWithoutReinterpretingRaw()
    {
        var path = DisplayEdidModeTestData.Path(WindowsMonitorEdidReader.CreateReadResult(Bytes()));
        var catalog = new NativeDisplayCoordinatorPayloadCatalog();
        var fact = Assert.Single(Assert.Single(NativeDisplayCoordinatorObservationProjector.Create([path], [], catalog, DateTimeOffset.UtcNow),
            batch => batch.Source == NativeDisplaySource.DisplayConfig).Facts);
        var imported = new NativeDisplayCoordinatorPayloadCatalog(); imported.Import(catalog.Export());
        var actual = Assert.Single(NativeDisplayCoordinatorReadProjection.CreateDisplayPaths(
            [new NativeDisplayNodeOutput { Kind = (uint)NativeDisplayNodeKind.Connector, PrimaryPayloadHandle = fact.PayloadHandle }], imported)).Path;
        AssertValue(JsonSerializer.SerializeToElement(actual.Edid!.ModeCapability, Web));
        Assert.Equal(JsonSerializer.Serialize(path, Web), JsonSerializer.Serialize(actual, Web));
        var capability = path.Edid!.ModeCapability!;
        var changed = path with { Edid = path.Edid! with { ModeCapability = capability with {
            Modes = capability.Modes.SetItem(2, capability.Modes[2] with { CvtParameters = capability.Modes[2].CvtParameters! with { DeltaHorizontalBlankingCode = 5 } }) } } };
        Assert.Equal(path.EdidRawDeclaredBase64, changed.EdidRawDeclaredBase64);
        Assert.NotEqual(catalog.AddDisplayPath(path), catalog.AddDisplayPath(changed));
        var comparer = new DeviceTopologySemanticComparer();
        Assert.Equal(comparer.ComputeHash(DisplayEdidModeTestData.Snapshot(path)), comparer.ComputeHash(DisplayEdidModeTestData.Snapshot(actual)));
        Assert.NotEqual(comparer.ComputeHash(DisplayEdidModeTestData.Snapshot(path)), comparer.ComputeHash(DisplayEdidModeTestData.Snapshot(changed)));
    }

    [Fact]
    public void LegacyMissingCvtStaysUnknownAndUnknownFinalBodyClearsOnlyDeclaration()
    {
        var path = DisplayEdidModeTestData.Path(WindowsMonitorEdidReader.CreateReadResult(Bytes()));
        var node = JsonSerializer.SerializeToNode(path, Web)!;
        foreach (var mode in node["edid"]!["modeCapability"]!["modes"]!.AsArray()) mode!.AsObject().Remove("cvtParameters");
        var legacy = node.Deserialize<DeviceTopologyDisplayPath>(Web)!;
        Assert.All(legacy.Edid!.ModeCapability!.Modes, mode => { Assert.Null(mode.CvtParameters); Assert.Null(mode.AspectRatioDeclared); });
        Assert.Equal(path.EdidRawDeclaredBase64, legacy.EdidRawDeclaredBase64);
        var bytes = Bytes(unknown: true); var failed = DisplayEdidModeTestData.Path(WindowsMonitorEdidReader.CreateReadResult(bytes));
        var projected = DeviceTopologyInformationProjection.Create(DisplayEdidModeTestData.Snapshot(failed), DateTimeOffset.UtcNow);
        Assert.Null(DisplayEdidModeInformationCollectorTests.Field(projected).Value);
        Assert.Equal(Convert.ToBase64String(bytes), DisplayHdrInformationCollectorTests.Field(projected, "display_all_edid_raw_declared").Value!.Value.GetString());
        Assert.NotNull(DisplayHdrInformationCollectorTests.Field(projected, "display_all_resolution_actual").Value);
    }

    [Theory]
     [InlineData(0x20)]
    public void NonModeCtaMetadataTraversesTypedOpaqueAndInformationWithoutAddingModes(byte version)
    {
        var bytes = Bytes(version: version, ctaMetadata: true);
        var path = DisplayEdidModeTestData.Path(WindowsMonitorEdidReader.CreateReadResult(bytes));
        AssertValue(JsonSerializer.SerializeToElement(path.Edid!.ModeCapability, Web), version);
        AssertValue(DisplayEdidModeInformationCollectorTests.Field(
            DeviceTopologyInformationProjection.Create(DisplayEdidModeTestData.Snapshot(path), DateTimeOffset.UtcNow)).Value!.Value, version);
        var catalog = new NativeDisplayCoordinatorPayloadCatalog();
        var handle = catalog.AddDisplayPath(path);
        var imported = new NativeDisplayCoordinatorPayloadCatalog(); imported.Import(catalog.Export());
        var actual = Assert.Single(NativeDisplayCoordinatorReadProjection.CreateDisplayPaths(
            [new NativeDisplayNodeOutput { Kind = (uint)NativeDisplayNodeKind.Connector, PrimaryPayloadHandle = handle }], imported)).Path;
        Assert.Equal(JsonSerializer.Serialize(path, Web), JsonSerializer.Serialize(actual, Web));
        Assert.Equal(Convert.ToBase64String(bytes), actual.EdidRawDeclaredBase64);
        Assert.Null(actual.Edid!.ModeCapability!.CtaFlags);
    }

    [Theory]
     [InlineData(0x20)]
    public void ModeBearingCtaInsideTheWrapperClearsOnlyModesAndRawRemainsExact(byte version)
    {
        var bytes = DisplayIdNonModeCtaFixture.Bytes(version, modeBearingCta: true);
        var read = WindowsMonitorEdidReader.CreateReadResult(bytes);
        Assert.True(read.Complete); Assert.NotNull(read.Capabilities); Assert.Null(read.Capabilities.ModeCapability);
        Assert.Equal(bytes, Convert.FromBase64String(read.RawEdidDeclaredBase64!));
        Assert.NotNull(read.Capabilities.BitsPerColorChannel);
    }

    internal static byte[] Bytes(bool unknown = false, byte version = 0x20, bool ctaMetadata = false)
    {
        if (ctaMetadata) return DisplayIdNonModeCtaFixture.Bytes(version, unknownTiming: unknown);
        var bytes = Convert.FromBase64String("AP///////wAAAAAAAAAAAAAAAQS1AAAAAAAAAAAAAAAAAAAAAAABAQEBAQEBAQEBAQEBAQEBAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAATtwIB4CACoABhJ/BzcEOyoQBwJ/BzcEKwEqIAibfwc3BDtYARAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAkA==");
        bytes[129] = version;
        if (unknown) bytes[155] = 0; // Complete final8B body, unsupported RB0, not a malformed checksum.
        bytes[163] = 0; bytes[163] = unchecked((byte)-bytes.AsSpan(129, 34).ToArray().Sum(value => (int)value));
        bytes[255] = 0; bytes[255] = unchecked((byte)-bytes.AsSpan(128, 127).ToArray().Sum(value => (int)value));
        return bytes;
    }

    internal static void AssertValue(JsonElement value, byte version = 0x20)
    {
        var modes = value.GetProperty("modes"); Assert.Equal(3, modes.GetArrayLength());
        Assert.Equal(0, value.GetProperty("ranges").GetArrayLength()); Assert.Equal(0, value.GetProperty("selectionDeclarations").GetArrayLength());
        byte[][] bodies = [[0x12,0x7F,7,0x37,4,0x3B], [2,0x7F,7,0x37,4,0x2B,1], [0x9B,0x7F,7,0x37,4,0x3B,0x58,1]];
        uint[] offsets = [8,17,27];
        for (var i = 0; i < 3; i++) {
            var mode = modes[i]; var cvt = mode.GetProperty("cvtParameters");
            Assert.Equal("displayid-x", mode.GetProperty("encoding").GetString());
            Assert.Equal(1u, mode.GetProperty("blockIndex").GetUInt32()); Assert.Equal(offsets[i], mode.GetProperty("byteOffset").GetUInt32());
            Assert.Equal(1920u, mode.GetProperty("width").GetUInt32()); Assert.Equal(1080u, mode.GetProperty("height").GetUInt32());
            Assert.False(mode.GetProperty("interlaced").GetBoolean()); Assert.Equal(i == 1 ? 300u : 60u, mode.GetProperty("nominalVerticalRateHz").GetUInt32());
            Assert.Equal(0, mode.GetProperty("revision").GetInt32()); Assert.Equal(i * 16, mode.GetProperty("containerFlags").GetInt32());
            Assert.Equal(version, mode.GetProperty("containerRevision").GetByte());
            Assert.Equal(bodies[i], mode.GetProperty("rawDescriptor").EnumerateArray().Select(item => item.GetByte()));
            Assert.True(mode.GetProperty("reducedBlanking").GetBoolean()); Assert.Equal("cvt", mode.GetProperty("formula").GetString());
            Assert.Equal(i == 2, mode.GetProperty("yCbCr420Encoding").GetBoolean());
            Assert.Equal(i == 2 ? 3 : 2, cvt.GetProperty("reducedBlankingVersion").GetInt32()); Assert.Equal(0, cvt.GetProperty("stereoCode").GetInt32());
            if (i < 2) {
                Assert.Equal(i == 0, cvt.GetProperty("alternativeRefresh1000Over1001Supported").GetBoolean());
                foreach (var key in new[] { "horizontalBlankingBase160Pixels", "earlyVsyncRequired", "deltaHorizontalBlankingCode", "additionalVerticalBlankingCode", "alternativeMinimumVerticalBlanking" })
                    Assert.Equal(JsonValueKind.Null, cvt.GetProperty(key).ValueKind);
                Assert.Equal(JsonValueKind.Null, mode.GetProperty("yCbCr420Supported").ValueKind);
            } else {
                Assert.Equal(JsonValueKind.Null, cvt.GetProperty("alternativeRefresh1000Over1001Supported").ValueKind);
                Assert.True(cvt.GetProperty("horizontalBlankingBase160Pixels").GetBoolean()); Assert.True(cvt.GetProperty("earlyVsyncRequired").GetBoolean());
                Assert.Equal(6, cvt.GetProperty("deltaHorizontalBlankingCode").GetInt32()); Assert.Equal(2, cvt.GetProperty("additionalVerticalBlankingCode").GetInt32());
                Assert.True(cvt.GetProperty("alternativeMinimumVerticalBlanking").GetBoolean()); Assert.True(mode.GetProperty("yCbCr420Supported").GetBoolean());
            }
            foreach (var key in new[] { "timing", "code", "native", "preferred", "bitIndex", "horizontalPixelRepetition", "dscPassThrough", "yCbCr420Only", "yCbCr420CapabilityMap" })
                Assert.Equal(JsonValueKind.Null, mode.GetProperty(key).ValueKind);
            Assert.False(mode.TryGetProperty("aspectRatioDeclared", out _));
        }
    }
}
