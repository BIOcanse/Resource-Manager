using ResourceManager.ReadWrite.Windows.Devices.Display;

namespace Resource_Manager_APP.Tests;

public sealed partial class EdidModeCapabilityParserTests
{
    [Theory]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    public void HdmiKnownShortNonVideoBlocksDoNotAddOrFilterDetailedModes(int length)
    {
        var payload = new byte[length]; new byte[] { 3, 12, 0, 16, 0 }.CopyTo(payload, 0);
        if (length >= 6) payload[5] = 0xF9;
        if (length >= 7) payload[6] = 255;
        var bytes = Cta(HdmiData(payload)); Dtd().CopyTo(bytes, 54);
        var mode = Assert.Single(Capability(bytes).Modes);
        Assert.Equal("edid-detailed", mode.Encoding); Assert.Equal(new EdidModeRate(60, 1), mode.Timing!.FrameRate);
    }

    [Theory]
    [InlineData(1, 3840, 30)]
    [InlineData(2, 3840, 25)]
    [InlineData(3, 3840, 24)]
    [InlineData(4, 4096, 24)]
    public void HdmiFourExplicitCodesKeepHdmiIdentityAndOnlyNominalMetadata(byte code, uint width, uint rate)
    {
        var bytes = Cta(HdmiData(HdmiPayload([code])));
        var mode = Assert.Single(Capability(bytes).Modes);
        Assert.Equal("hdmi-vic", mode.Encoding); Assert.Equal((uint)code, mode.Code);
        Assert.Equal((width, 2160u, rate), (mode.Width!.Value, mode.Height!.Value, mode.NominalVerticalRateHz!.Value));
        Assert.False(mode.Interlaced); Assert.Null(mode.Timing); Assert.Null(mode.Native); Assert.Null(mode.Preferred);
        Assert.Null(mode.YCbCr420Supported); Assert.Null(mode.YCbCr420Only); Assert.Null(mode.YCbCr420CapabilityMap);
        Assert.Equal(1u, mode.BlockIndex); Assert.Equal(15u, mode.ByteOffset);
        Assert.Equal((byte)3, mode.Revision); Assert.Equal((byte)3, mode.ContainerRevision);
        Assert.Equal(new byte[] { code }, mode.RawDescriptor.ToArray());
        bytes[128 + 15] = 0;
        Assert.Equal(new byte[] { code }, mode.RawDescriptor.ToArray());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(4)]
    public void HdmiLatencyOffsetsAreBoundedAndPreserveAllFourSourcePositions(int latencyBytes)
    {
        var modes = Capability(Cta(HdmiData(HdmiPayload([1, 2, 3, 4], latencyBytes)))).Modes;
        Assert.Equal(new uint?[] { 1, 2, 3, 4 }, modes.Select(static mode => mode.Code));
        Assert.Equal(Enumerable.Range(15 + latencyBytes, 4).Select(static at => (uint)at), modes.Select(static mode => mode.ByteOffset));
        Assert.All(modes, static mode => { Assert.Equal("hdmi-vic", mode.Encoding); Assert.Null(mode.Timing); });
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(4)]
    public void HdmiZeroDeclaredCodesWithout3dIsSuccessfulEmptyIncludingLatencyOnly(int latencyBytes)
    {
        var video = Capability(Cta(HdmiData(HdmiPayload([], latencyBytes))));
        Assert.Empty(video.Modes); Assert.Empty(video.Ranges);
        var payload = HdmiPayload([], latencyBytes)[..(8 + latencyBytes)]; payload[7] &= 0xDF;
        Assert.Empty(Capability(Cta(HdmiData(payload))).Modes);
    }

    [Theory]
    [InlineData(0x00)]
    [InlineData(0x08)]
    [InlineData(0x10)]
    [InlineData(0x18)]
    public void HdmiKnownImageSizeFlagsAreNotConvertedIntoModeGeometry(byte imageFlags)
    {
        var payload = HdmiPayload([1]); payload[8] = imageFlags;
        var mode = Assert.Single(Capability(Cta(HdmiData(payload))).Modes);
        Assert.Equal((3840u, 2160u), (mode.Width!.Value, mode.Height!.Value)); Assert.Null(mode.Timing);
    }

    [Fact]
    public void HdmiRepeatedSourcesAndEquivalentVdbAreNotCoalesced()
    {
        var bytes = Base(2);
        SetCta(bytes, 1, [0x41, 95, .. HdmiData(HdmiPayload([1, 1]))]);
        SetCta(bytes, 2, HdmiData(HdmiPayload([1])));
        var modes = Capability(bytes).Modes;
        Assert.Equal(4, modes.Length); Assert.Equal("cta-vic", modes[0].Encoding); Assert.Equal(95u, modes[0].Code);
        var hdmi = modes.Where(static mode => mode.Encoding == "hdmi-vic").ToArray();
        Assert.Equal(new uint[] { 1, 1, 2 }, hdmi.Select(static mode => mode.BlockIndex));
        Assert.Equal(new uint[] { 17, 18, 15 }, hdmi.Select(static mode => mode.ByteOffset));
        Assert.All(hdmi, static mode => { Assert.Equal(1u, mode.Code); Assert.Equal(3840u, mode.Width); });
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(4)]
    public void HdmiEncodedSevenEntryListDoesNotImposeAFourEntrySoftwareLimit(int latencyBytes)
    {
        var modes = Capability(Cta(HdmiData(HdmiPayload([1, 2, 3, 4, 1, 2, 3], latencyBytes)))).Modes;
        Assert.Equal(7, modes.Length); Assert.Equal(new uint?[] { 1, 2, 3, 4, 1, 2, 3 }, modes.Select(static mode => mode.Code));
    }

    [Fact]
    public void HdmiModesNeverChangeOrdinarySvdCmdbIndexesOrReceiveTheirMetadata()
    {
        byte[] payload = [0x41, 95, .. HdmiData(HdmiPayload([1, 2, 3, 4])), 0x41, 16, 0xE2, 0x0F, 0x02];
        var modes = Capability(Cta(payload)).Modes;
        Assert.Equal(6, modes.Length);
        var regular = modes.Where(static mode => mode.Encoding == "cta-vic").ToArray();
        Assert.Equal(new uint?[] { 95, 16 }, regular.Select(static mode => mode.Code));
        Assert.False(regular[0].YCbCr420Supported); Assert.True(regular[1].YCbCr420Supported);
        Assert.Equal(17, regular[1].YCbCr420CapabilityMap!.BitIndex);
        Assert.All(modes.Where(static mode => mode.Encoding == "hdmi-vic"), static mode => Assert.Null(mode.YCbCr420CapabilityMap));
        payload[^1] = 0x04;
        AssertHdmiUnknownAndRaw(Cta(payload));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(4)]
    public void HdmiLinkMetadataNeverFiltersDeclaredModes(int latencyBytes)
    {
        foreach (var clock in new byte[] { 0, 1, 68, 255 })
        {
            var payload = HdmiPayload([1, 2, 3, 4], latencyBytes); payload[5] = 0xF9; payload[6] = clock;
            var modes = Capability(Cta(HdmiData(payload))).Modes;
            Assert.Equal(4, modes.Length); Assert.All(modes, static mode => Assert.Null(mode.Timing));
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void HdmiMissingOuiOrPhysicalAddressDoesNotBecomeSuccessfulNoMode(int length)
    {
        var bytes = Cta([0x41, 16, .. HdmiData(new byte[] { 3, 12, 0, 16, 0 }[..length])]);
        AssertHdmiUnknownAndRaw(bytes);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void HdmiOnlyExactOui000c03IsSupported(int variant)
    {
        var payload = HdmiPayload([1]);
        if (variant == 0) { payload[0] = 0; payload[2] = 3; }
        if (variant == 1) payload[1] = 13;
        if (variant == 2) { payload[0] = 0xD8; payload[1] = 0x5D; payload[2] = 0xC4; }
        AssertHdmiUnknownAndRaw(Cta([0x41, 16, .. HdmiData(payload)]));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    [InlineData(255)]
    public void HdmiUnknownFinalCodeRejectsEarlierValidDeclarations(byte code)
    {
        AssertHdmiUnknownAndRaw(Cta([0x41, 16, .. HdmiData(HdmiPayload([1, code]))]));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(4)]
    public void HdmiEveryDeclaredLatencyVideoAndCodeTruncationRejectsTheWholeValue(int latencyBytes)
    {
        var payload = HdmiPayload([1, 2, 3, 4], latencyBytes);
        // Cuts below8 can be valid old non-video formats; only declared-required suffixes are rejected.
        for (var length = 8; length < payload.Length; length++)
            AssertHdmiUnknownAndRaw(Cta([0x41, 16, .. HdmiData(payload[..length])]));
    }

    [Theory]
    [InlineData(0x80)]
    [InlineData(0x20)]
    [InlineData(0x40)]
    [InlineData(0x60)]
    [InlineData(0xE0)]
    [InlineData(0x01)]
    [InlineData(0x02)]
    [InlineData(0x04)]
    public void Hdmi3dOrUnexplainedVideoFlagsAreUnsupportedWholeValueNotPartial(byte flags)
    {
        var payload = HdmiPayload([1]); payload[8] = flags;
        AssertHdmiUnknownAndRaw(Cta([0x41, 16, .. HdmiData(payload)]));
    }

    [Theory]
    [InlineData(0)] // Uninterpreted deep-color flag bit1.
    [InlineData(1)] // Uninterpreted deep-color flag bit2.
    [InlineData(2)] // Uninterpreted flags byte bit4.
    [InlineData(3)] // I-latency without latency, with video.
    [InlineData(4)] // I-latency without latency, no video.
    [InlineData(5)] // Nonzero3D length even with3D-present flag clear.
    public void HdmiUnexplainedFlagsAndAny3dPayloadRemainUnknownWithoutCallingThemInvalidHdmi(int variant)
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
        AssertHdmiUnknownAndRaw(Cta([0x41, 16, .. HdmiData(payload)]));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void HdmiUnknownExtraPayloadAndParentCollectionOverrunAreNotSilentlyDropped(int variant)
    {
        var payload = HdmiPayload([1]);
        if (variant == 0) payload = [.. payload, 0];
        if (variant == 1) payload = [.. payload, 255];
        if (variant == 2) { payload = new byte[31]; new byte[] { 3, 12, 0, 16, 0 }.CopyTo(payload, 0); }
        var data = HdmiData(payload);
        if (variant == 3) data[0] = 0x7F;
        AssertHdmiUnknownAndRaw(Cta([0x41, 16, .. data]));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void HdmiValidModesBeforeBadHfTailOrGlobalReferenceNeverPublishPrefix(int variant)
    {
        var bytes = HdmiHfChain();
        switch (variant)
        {
            case 0: bytes[383] ^= 1; break;
            case 1: Array.Resize(ref bytes, 256); break;
            case 2: bytes[256] = 0x40; Checksums(bytes); break;
            case 3: Array.Clear(bytes, 256, 128); SetCta(bytes, 2, [0x41, 16, 0xE2, 0x0F, 2]); Checksums(bytes); break;
            case 4: Array.Resize(ref bytes, 512); SetCta(bytes, 3, []); Checksums(bytes); break;
            default: Array.Clear(bytes, 256, 128); SetCta(bytes, 2, [0x63, 3, 12, 0]); Checksums(bytes); break;
        }
        AssertHdmiUnknownAndRaw(bytes);
    }

    [Fact]
    public void HdmiExplicitModesUseCompleteHfChainBeyondBaseFlagAndPreserveTailSource()
    {
        var modes = Capability(HdmiHfChain()).Modes;
        Assert.Equal(2, modes.Length);
        Assert.Equal(("hdmi-vic", 1u, 18u, 1u), (modes[0].Encoding, modes[0].BlockIndex, modes[0].ByteOffset, modes[0].Code!.Value));
        Assert.Equal(("cta-vic", 2u, 5u, 16u), (modes[1].Encoding, modes[1].BlockIndex, modes[1].ByteOffset, modes[1].Code!.Value));
    }

    private static byte[] HdmiPayload(byte[] codes, int latencyBytes = 0)
    {
        var payload = new byte[checked(10 + latencyBytes + codes.Length)];
        new byte[] { 3, 12, 0, 16, 0 }.CopyTo(payload, 0);
        payload[7] = latencyBytes switch { 2 => 0xA0, 4 => 0xE0, _ => 0x20 };
        payload[checked(9 + latencyBytes)] = checked((byte)(codes.Length << 5));
        codes.CopyTo(payload, checked(10 + latencyBytes)); return payload;
    }

    private static byte[] HdmiData(byte[] payload) => [checked((byte)(0x60 + payload.Length)), .. payload];

    private static byte[] HdmiHfChain()
    {
        var bytes = Base(2); bytes[126] = 1;
        SetCta(bytes, 1, [0xE2, 0x78, 2, .. HdmiData(HdmiPayload([1]))]);
        SetCta(bytes, 2, [0x41, 16]); return Checked(bytes);
    }

    private static void AssertHdmiUnknownAndRaw(byte[] bytes)
    {
        var read = WindowsMonitorEdidReader.CreateReadResult(bytes);
        Assert.True(read.Complete); Assert.NotNull(read.Capabilities); Assert.Null(read.Capabilities.ModeCapability);
        Assert.Equal(bytes, Convert.FromBase64String(read.RawEdidDeclaredBase64!));
    }
}
