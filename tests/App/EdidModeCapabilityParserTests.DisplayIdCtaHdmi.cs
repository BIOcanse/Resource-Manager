using System.Security.Cryptography;
using System.Text.Json;
using ResourceManager.ReadWrite.Windows.Devices.Display;
using ResourceManager.TestFixtures;

namespace Resource_Manager_APP.Tests;

public sealed partial class EdidModeCapabilityParserTests
{
    [Theory]
     [InlineData(0x20)]
    public void DisplayIdEmbeddedHdmiLiteralKeepsPriorColourAndActualContainer(byte version)
    {
        var bytes = DisplayIdCtaHdmiFixture.Bytes(version);
        Assert.Equal(version == 0x20 ? DisplayIdCtaHdmiFixture.Sha256Version20 : DisplayIdCtaHdmiFixture.Sha256Version21,
            Convert.ToHexString(SHA256.HashData(bytes)));
        var read = WindowsMonitorEdidReader.CreateReadResult(bytes);
        Assert.True(read.Complete);
        var capability = Assert.IsType<EdidModeCapability>(read.Capabilities!.ModeCapability);
        Assert.Null(capability.CtaFlags); Assert.Empty(capability.Ranges); Assert.Empty(capability.SelectionDeclarations);
        Assert.Equal(9, capability.Modes.Length);
        var previous = Capability(DisplayIdCtaColourFixture.Bytes(version));
        Assert.Equal(JsonSerializer.Serialize(previous.Modes), JsonSerializer.Serialize(capability.Modes.Take(5)));
        var modes = capability.Modes.Skip(5).ToArray();
        Assert.Equal(new uint?[] { 1, 2, 3, 4 }, modes.Select(mode => mode.Code));
        Assert.Equal(new uint[] { 38, 39, 40, 41 }, modes.Select(mode => mode.ByteOffset));
        Assert.All(modes, mode => AssertEmbeddedHdmiSource(mode, version));
        Assert.Equal(bytes, Convert.FromBase64String(read.RawEdidDeclaredBase64!));
        bytes[166] = 0;
        Assert.Equal(new byte[] { 1 }, modes[0].RawDescriptor.ToArray());
    }

    [Theory]
    [InlineData(0x20, 1, 3840, 30)] [InlineData(0x20, 2, 3840, 25)]
    [InlineData(0x20, 3, 3840, 24)] [InlineData(0x20, 4, 4096, 24)]
    public void DisplayIdEmbeddedHdmiFourCodesOnlySupplyDeclaredNominalFacts(byte version, byte code, uint width, uint rate)
    {
        var mode = Assert.Single(Capability(TypeXSection(version, DidCtaMetadataData(HdmiData(HdmiPayload([code]))))).Modes);
        AssertEmbeddedHdmiSource(mode, version);
        Assert.Equal((width, 2160u, rate), (mode.Width!.Value, mode.Height!.Value, mode.NominalVerticalRateHz!.Value));
        Assert.Equal((1u, 19u, (uint?)code), (mode.BlockIndex, mode.ByteOffset, mode.Code));
    }

    [Theory]
    [InlineData(0x20, 0)] [InlineData(0x20, 2)] [InlineData(0x20, 4)]
    public void DisplayIdEmbeddedHdmiLatencyAndRepeatedCodePositionsAreExact(byte version, int latencyBytes)
    {
        var modes = Capability(TypeXSection(version, DidCtaMetadataData(HdmiData(HdmiPayload([1, 2, 3, 4, 1, 2, 3], latencyBytes))))).Modes;
        Assert.Equal(new uint?[] { 1, 2, 3, 4, 1, 2, 3 }, modes.Select(mode => mode.Code));
        Assert.Equal(Enumerable.Range(19 + latencyBytes, 7).Select(offset => (uint)offset), modes.Select(mode => mode.ByteOffset));
        Assert.All(modes, mode => AssertEmbeddedHdmiSource(mode, version));
    }

    [Theory]
    [InlineData(5)] [InlineData(6)] [InlineData(7)] [InlineData(8)]
    public void DisplayIdEmbeddedHdmiKnownShortMetadataDoesNotFilterExistingDtd(int length)
    {
        var payload = new byte[length]; new byte[] { 3, 12, 0, 16, 0 }.CopyTo(payload, 0);
        if (length >= 6) payload[5] = 0xF9;
        if (length >= 7) payload[6] = 255;
        var bytes = TypeXSection(0x20, DidCtaMetadataData(HdmiData(payload)));
        Dtd().CopyTo(bytes, 54); Checksums(bytes);
        var capability = Capability(bytes); var mode = Assert.Single(capability.Modes);
        Assert.Equal("edid-detailed", mode.Encoding); Assert.Equal(new EdidModeRate(60, 1), mode.Timing!.FrameRate);
        Assert.Null(mode.Native); Assert.Null(capability.CtaFlags);
    }

    [Theory]
    [InlineData(0)] [InlineData(2)] [InlineData(4)]
    public void DisplayIdEmbeddedHdmiEmptyExplicitListAndLatencyOnlyAreKnownEmpty(int latencyBytes)
    {
        foreach (var version in new byte[] { 0x20 })
        {
            Assert.Empty(Capability(TypeXSection(version, DidCtaMetadataData(HdmiData(HdmiPayload([], latencyBytes))))).Modes);
            var payload = HdmiPayload([], latencyBytes)[..(8 + latencyBytes)]; payload[7] &= 0xDF;
            Assert.Empty(Capability(TypeXSection(version, DidCtaMetadataData(HdmiData(payload)))).Modes);
        }
    }

    [Theory]
    [InlineData(0)] [InlineData(8)] [InlineData(16)] [InlineData(24)]
    public void DisplayIdEmbeddedHdmiLinkAndImageMetadataDoNotFilterOrGenerateGeometry(byte imageFlags)
    {
        foreach (var clock in new byte[] { 0, 1, 68, 255 })
        {
            var payload = HdmiPayload([1, 2, 3, 4]); payload[5] = 0xF9; payload[6] = clock; payload[8] = imageFlags;
            var modes = Capability(TypeXSection(0x20, DidCtaMetadataData(HdmiData(payload)))).Modes;
            Assert.Equal(4, modes.Length); Assert.All(modes, mode => AssertEmbeddedHdmiSource(mode, 0x20));
        }
    }

    [Theory]
      [InlineData(0x20, false)] [InlineData(0x20, true)]
    public void DisplayIdEmbeddedHdmiDoesNotConsumeCombinedSvdMapOrdinals(byte version, bool embeddedFirst)
    {
        var bytes = EmbeddedColourMixedChain(version, embeddedFirst,
            [0x41, 16, .. HdmiData(HdmiPayload([1, 1])), 0xE2, 0x0F, 5], [0x42, 31, 95]);
        var modes = Capability(bytes).Modes;
        Assert.Equal(5, modes.Length);
        Assert.True(modes.Single(mode => mode.Code == 16).YCbCr420Supported);
        Assert.True(modes.Single(mode => mode.Code == 31).YCbCr420Supported);
        Assert.False(modes.Single(mode => mode.Code == 95).YCbCr420Supported);
        var hdmi = modes.Where(mode => mode.Encoding == "displayid-cta-hdmi-vic").ToArray();
        Assert.Equal(2, hdmi.Length);
        Assert.All(hdmi, mode => AssertEmbeddedHdmiSource(mode, version));
        Assert.Equal(embeddedFirst ? new uint?[] { 16, 1, 1, 31, 95 } : new uint?[] { 31, 95, 16, 1, 1 }, modes.Select(mode => mode.Code));
        var invalid = EmbeddedColourMixedChain(version, embeddedFirst,
            [0x41, 16, .. HdmiData(HdmiPayload([1, 1])), 0xE2, 0x0F, 8], [0x42, 31, 95]);
        AssertTypeXUnknown(invalid);
    }

    [Fact]
    public void DisplayIdEmbeddedHdmiAndOrdinaryEquivalentDeclarationsRemainDistinct()
    {
        var bytes = EmbeddedColourMixedChain(0x20, true, HdmiData(HdmiPayload([1, 1])), [0x41, 95, .. HdmiData(HdmiPayload([1]))]);
        var modes = Capability(bytes).Modes;
        Assert.Equal(new[] { "displayid-cta-hdmi-vic", "displayid-cta-hdmi-vic", "cta-vic", "hdmi-vic" }, modes.Select(mode => mode.Encoding));
        Assert.Equal(new uint[] { 1, 1, 2, 2 }, modes.Select(mode => mode.BlockIndex));
        Assert.Equal(new uint[] { 19, 20, 5, 17 }, modes.Select(mode => mode.ByteOffset));
        Assert.All(modes, mode => Assert.Equal(3840u, mode.Width));
    }

    [Theory]
     [InlineData(0x20)]
    public void DisplayIdEmbeddedHdmiContinuationAndMultipleWrappersRetainPhysicalSources(byte version)
    {
        var bytes = Base(2);
        Did8Section(bytes, 1, version, DidCtaMetadataData(HdmiData(HdmiPayload([1]))), 1, false);
        Did8Section(bytes, 2, version, [.. DidCtaMetadataData(HdmiData(HdmiPayload([2]))), .. DidCtaMetadataData(HdmiData(HdmiPayload([3])))], 0, true);
        var modes = Capability(Checked(bytes)).Modes;
        Assert.Equal(new uint?[] { 1, 2, 3 }, modes.Select(mode => mode.Code));
        Assert.Equal(new uint[] { 1, 2, 2 }, modes.Select(mode => mode.BlockIndex));
        Assert.Equal(new uint[] { 19, 19, 34 }, modes.Select(mode => mode.ByteOffset));
        Assert.All(modes, mode => AssertEmbeddedHdmiSource(mode, version));
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    public void DisplayIdEmbeddedHdmiIncompleteOuiOrAddressRejectsAllModes(int length)
        => AssertTypeXUnknown(DidCtaMetadataWithTypeX(0x20, HdmiData(new byte[] { 3, 12, 0, 16, 0 }[..length])));

    [Theory]
    [InlineData(0)] [InlineData(5)] [InlineData(255)]
    public void DisplayIdEmbeddedHdmiUnknownFinalCodeRejectsGoodPrefix(byte code)
        => AssertTypeXUnknown(DidCtaMetadataWithTypeX(0x20, HdmiData(HdmiPayload([1, code]))));

    [Theory]
    [InlineData(0)] [InlineData(2)] [InlineData(4)]
    public void DisplayIdEmbeddedHdmiEveryDeclaredLatencyAndVideoTruncationRejectsWholeValue(int latencyBytes)
    {
        var payload = HdmiPayload([1, 2, 3, 4], latencyBytes);
        for (var length = 8; length < payload.Length; length++)
            AssertTypeXUnknown(DidCtaMetadataWithTypeX(0x20, HdmiData(payload[..length])));
    }

    [Theory]
    [InlineData(0x80)] [InlineData(0x20)] [InlineData(0x40)] [InlineData(0x60)]
    [InlineData(0xE0)] [InlineData(1)] [InlineData(2)] [InlineData(4)]
    public void DisplayIdEmbeddedHdmi3dAndUnverifiedVideoFlagsRemainWholeUnknown(byte flags)
    {
        var payload = HdmiPayload([1]); payload[8] = flags;
        AssertTypeXUnknown(DidCtaMetadataWithTypeX(0x20, HdmiData(payload)));
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public void DisplayIdEmbeddedHdmiUnknownOuiDoesNotBecomeNonModeMetadata(int variant)
    {
        var payload = HdmiPayload([1]);
        if (variant == 0) { payload[0] = 0; payload[2] = 3; }
        else if (variant == 1) payload[1] = 13;
        else { payload[0] = 0xD8; payload[1] = 0x5D; payload[2] = 0xC4; }
        AssertTypeXUnknown(DidCtaMetadataWithTypeX(0x20, HdmiData(payload)));
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)]
    public void DisplayIdEmbeddedHdmiUnverifiedMetadataAnd3dLengthsRejectWholeValue(int variant)
    {
        var payload = HdmiPayload([1]);
        switch (variant)
        {
            case 0: payload[5] = 2; break;
            case 1: payload[5] = 4; break;
            case 2: payload[7] |= 0x10; break;
            case 3: payload[7] = 0x60; break;
            case 4: payload[7] = 0x40; break;
            default: payload[9] |= 1; payload = [.. payload, 0]; break;
        }
        AssertTypeXUnknown(DidCtaMetadataWithTypeX(0x20, HdmiData(payload)));
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public void DisplayIdEmbeddedHdmiUnknownExtraTailAndInnerExtentNeverPublishPrefix(int variant)
    {
        var data = HdmiData(HdmiPayload([1]));
        if (variant == 0) data = HdmiData([.. HdmiPayload([1]), 0]);
        else if (variant == 1) data = HdmiData([.. HdmiPayload([1]), 255]);
        else data[0] = 0x7F;
        AssertTypeXUnknown(DidCtaMetadataWithTypeX(0x20, data));
    }

    [Theory]
    [InlineData(1)] [InlineData(8)] [InlineData(16)] [InlineData(255)]
    public void DisplayIdEmbeddedHdmiUnverifiedWrapperIsNotAnInventedCtaHeader(byte flags)
        => AssertTypeXUnknown(DidCtaMetadataWithTypeX(0x20, HdmiData(HdmiPayload([1])), flags));

    [Theory]
     [InlineData(0x12)]
    public void DisplayIdEmbeddedHdmiLegacyContainerRemainsOutsideKnownScope(byte version)
    {
        var bytes = Base(1); Dtd().CopyTo(bytes, 54);
        Did8Section(bytes, 1, version, DidCtaMetadataData(HdmiData(HdmiPayload([1]))), 0, false);
        AssertTypeXUnknown(Checked(bytes));
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void DisplayIdEmbeddedHdmiChecksumsAndLateSectionFailureRemainWholeChain(int variant)
    {
        var bytes = DisplayIdCtaHdmiFixture.Bytes(0x20);
        if (variant == 0) bytes[255] ^= 1;
        else if (variant == 1) { bytes[170] ^= 1; Checksums(bytes); }
        else if (variant == 2) { bytes[144]++; DisplayIdChecksum(bytes, 1); Checksums(bytes); }
        else { Array.Resize(ref bytes, 384); bytes[126] = 2; SetCta(bytes, 2, [0x63, 3, 12, 0]); Checksums(bytes); }
        AssertTypeXUnknown(bytes);
    }

    private static void AssertEmbeddedHdmiSource(EdidModeDeclaration mode, byte version)
    {
        Assert.Equal("displayid-cta-hdmi-vic", mode.Encoding);
        Assert.Equal((byte)0, mode.Revision); Assert.Equal((byte?)0, mode.ContainerFlags); Assert.Equal((byte?)version, mode.ContainerRevision);
        Assert.False(mode.Interlaced); Assert.Null(mode.Timing); Assert.Null(mode.Native); Assert.Null(mode.Preferred);
        Assert.Null(mode.DscPassThrough); Assert.Null(mode.CvtParameters); Assert.Null(mode.AspectRatioDeclared);
        Assert.Null(mode.YCbCr420Supported); Assert.Null(mode.YCbCr420Only); Assert.Null(mode.YCbCr420CapabilityMap);
        Assert.Equal(new[] { checked((byte)mode.Code!.Value) }, mode.RawDescriptor.ToArray());
    }
}
