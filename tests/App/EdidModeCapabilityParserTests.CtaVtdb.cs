using ResourceManager.ReadWrite.Windows.Devices.Display;

namespace Resource_Manager_APP.Tests;

public sealed partial class EdidModeCapabilityParserTests
{
    [Theory]
    [InlineData(148499, 148500000, 60, 1)]
    [InlineData(148349, 148350000, 1978, 33)]
    public void CtaTypeVIIHasExactIntegerClockRateAndCtaSource(uint encoded, ulong clock, ulong numerator, ulong denominator)
    {
        var timing = DidTiming(encoded); var bytes = Cta(CtaVII(timing));
        var mode = Assert.Single(Capability(bytes).Modes);
        Assert.Equal("cta-vtdb-vii", mode.Encoding); Assert.Equal(1u, mode.BlockIndex); Assert.Equal(7u, mode.ByteOffset);
        Assert.Equal((byte)2, mode.Revision); Assert.Equal((byte)2, mode.ContainerFlags); Assert.Equal((byte)3, mode.ContainerRevision);
        Assert.Equal(timing, mode.RawDescriptor.ToArray()); Assert.Equal((1920u, 1080u), (mode.Width!.Value, mode.Height!.Value));
        Assert.Equal(clock, mode.Timing!.PixelClockHz); Assert.Equal(new EdidModeRate(numerator, denominator), mode.Timing.FrameRate);
        Assert.Null(mode.Timing.FieldRate); Assert.Null(mode.DscPassThrough); Assert.Null(mode.Preferred); Assert.Null(mode.Native);
        Assert.Null(mode.NominalVerticalRateHz); Assert.Null(mode.Code); Assert.Null(mode.YCbCr420Only);
        bytes[128 + 7] = 0; Assert.Equal(timing, mode.RawDescriptor.ToArray());
    }

    [Theory]
    [InlineData(0, 1000)]
    [InlineData(16777215, 16777216000)]
    public void CtaTypeVIIClockUsesFullUnsignedTwentyFourBitsAndPlusOne(uint encoded, ulong clock)
    {
        var d = DidTiming(encoded);
        WriteMinusOne(d, 4, 1); WriteMinusOne(d, 6, 2); WriteMinusOne(d, 8, 1); WriteMinusOne(d, 10, 1);
        WriteMinusOne(d, 12, 1); WriteMinusOne(d, 14, 2); WriteMinusOne(d, 16, 1); WriteMinusOne(d, 18, 1);
        var timing = Assert.Single(Capability(Cta(CtaVII(d))).Modes).Timing!;
        Assert.Equal(clock, timing.PixelClockHz); Assert.Equal(new EdidModeRate(clock, 9), timing.FrameRate);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CtaTypeVIIRevisionTwoHighBitIs420AndDoesNotInventDscOrPreferred(bool enabled)
    {
        var d = DidTiming(148499); d[3] = enabled ? (byte)0x84 : (byte)4;
        var mode = Assert.Single(Capability(Cta(CtaVII(d))).Modes);
        Assert.Equal(enabled, mode.YCbCr420Encoding); Assert.Equal(enabled ? true : (bool?)null, mode.YCbCr420Supported);
        Assert.Null(mode.DscPassThrough); Assert.Null(mode.Preferred); Assert.Null(mode.YCbCr420CapabilityMap);
        Assert.Equal(d[3], mode.Timing!.Flags);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    public void CtaTypeVIIKnownAspectCodesRemainOriginalFlags(byte aspect)
    {
        var d = DidTiming(148499); d[3] = aspect;
        var mode = Assert.Single(Capability(Cta(CtaVII(d))).Modes);
        Assert.Equal(aspect, mode.Timing!.Flags); Assert.Equal(d, mode.RawDescriptor.ToArray());
        Assert.Equal(1920u, mode.Width); Assert.Equal(1080u, mode.Height);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(32)]
    [InlineData(64)]
    public void CtaTypeVIIKnownStereoDeclarationIsPreservedWithoutGeneratingOtherModes(byte stereo)
    {
        var d = DidTiming(148499); d[3] = (byte)(4 | stereo);
        var mode = Assert.Single(Capability(Cta(CtaVII(d))).Modes);
        Assert.Equal(d[3], mode.Timing!.Flags); Assert.Equal(d, mode.RawDescriptor.ToArray());
    }

    [Theory]
    [InlineData(9)]
    [InlineData(15)]
    [InlineData(100)]
    public void CtaTypeVIIReservedBodyDeclarationsMakeWholeCapabilityUnknown(byte flags)
    {
        var d = DidTiming(148499); d[3] = flags;
        AssertCtaVIIUnknown(Cta([0x41, 16, .. CtaVII(d)]));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void CtaTypeVIISyncPolarityIsTakenFromItsActualEncodedHighBits(bool h, bool v)
    {
        var d = DidTiming(148499); d[9] = h ? (byte)128 : (byte)0; d[17] = v ? (byte)128 : (byte)0;
        var timing = Assert.Single(Capability(Cta(CtaVII(d))).Modes).Timing!;
        Assert.Equal(h, timing.PositiveHorizontalSync); Assert.Equal(v, timing.PositiveVerticalSync);
        Assert.Equal(88u, timing.HorizontalSyncOffset); Assert.Equal(4u, timing.VerticalSyncOffset);
    }

    [Fact]
    public void CtaTypeVIIInterlaceKeepsEncodedGeometryAndDistinctExactFrameAndFieldRates()
    {
        var d = CtaVIIInterlaced();
        var mode = Assert.Single(Capability(Cta(CtaVII(d))).Modes);
        Assert.True(mode.Interlaced); Assert.Equal(1080u, mode.Timing!.VerticalActive); Assert.Equal(45u, mode.Timing.VerticalBlanking);
        Assert.Equal(new EdidModeRate(30, 1), mode.Timing.FrameRate); Assert.Equal(new EdidModeRate(60, 1), mode.Timing.FieldRate);
        Assert.Equal(d, mode.RawDescriptor.ToArray()); Assert.Null(mode.DscPassThrough);
    }

    [Theory]
    [InlineData(12, 1079)]
    [InlineData(16, 3)]
    [InlineData(18, 9)]
    [InlineData(14, 44)]
    public void CtaTypeVIIInterlaceRejectsEvenFrameTotalsAndPreservesOddSyncComponents(int offset, uint value)
    {
        var d = CtaVIIInterlaced(); WriteMinusOne(d, offset, value);
        if (offset is 12 or 14) AssertCtaVIIUnknown(Cta(CtaVII(d)));
        else
        {
            var timing = Assert.Single(Capability(Cta(CtaVII(d))).Modes).Timing!;
            Assert.Equal(new EdidModeRate(30, 1), timing.FrameRate);
            Assert.Equal(new EdidModeRate(60, 1), timing.FieldRate);
            Assert.Equal(value, offset == 16 ? timing.VerticalSyncOffset : timing.VerticalSyncWidth);
        }
    }

    [Theory]
    [InlineData(6, 100)]
    [InlineData(14, 8)]
    public void CtaTypeVIISyncCannotExceedItsDeclaredBlanking(int offset, uint value)
    {
        var d = DidTiming(148499); WriteMinusOne(d, offset, value);
        AssertCtaVIIUnknown(Cta(CtaVII(d)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(10)]
    [InlineData(18)]
    [InlineData(34)]
    [InlineData(66)]
    [InlineData(130)]
    public void CtaTypeVIIUnverifiedRevisionFlagsOrStrideAreUnknownRatherThanAParsedPrefix(byte header)
        => AssertCtaVIIUnknown(Cta(CtaVII(DidTiming(148499), header)));

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(19)]
    [InlineData(20)]
    [InlineData(22)]
    [InlineData(29)]
    public void CtaTypeVIIRequiresItsEntireExactTwentyOneByteBody(int length)
    {
        var payload = new byte[length]; if (length > 0) payload[0] = 2;
        var available = Math.Min(20, Math.Max(0, length - 1)); DidTiming(148499).AsSpan(0, available).CopyTo(payload.AsSpan(length > 0 ? 1 : 0));
        AssertCtaVIIUnknown(Cta([checked((byte)(0xE0 | (1 + length))), 0x22, .. payload]));
    }

    [Fact]
    public void CtaTypeVIIDeclaredDataBlockExtentCannotCrossTheCtaDetailedOffset()
    {
        var bytes = Cta(CtaVII(DidTiming(148499))); bytes[130]--;
        AssertCtaVIIUnknown(Checked(bytes));
    }

    [Fact]
    public void CtaTypeVIILastBadTimingInAnotherCtaBlockRejectsTheEntirePendingList()
    {
        var bytes = Base(2); SetCta(bytes, 1, CtaVII(DidTiming(148499)));
        var bad = DidTiming(148499); bad[3] = 15; SetCta(bytes, 2, CtaVII(bad));
        AssertCtaVIIUnknown(Checked(bytes));
    }

    [Fact]
    public void CtaTypeVIIDuplicateDeclarationsRetainEveryOriginalSourcePosition()
    {
        var timing = DidTiming(148499); var bytes = Cta([.. CtaVII(timing), .. CtaVII(timing)]);
        var modes = Capability(bytes).Modes; Assert.Equal(2, modes.Length);
        Assert.Equal(new uint[] { 7, 30 }, modes.Select(mode => mode.ByteOffset).ToArray());
        Assert.All(modes, mode => { Assert.Equal("cta-vtdb-vii", mode.Encoding); Assert.Equal(timing, mode.RawDescriptor.ToArray()); });
        var later = Base(2); SetCta(later, 1, CtaVII(timing)); SetCta(later, 2, CtaVII(timing));
        Assert.Equal(new uint[] { 1, 2 }, Capability(later).Modes.Select(mode => mode.BlockIndex).ToArray());
    }

    [Fact]
    public void CtaTypeVIIIsOutsideTheGlobalOrdinarySvdCapabilityMapDomain()
    {
        var bytes = Base(2); SetCta(bytes, 1, [.. CtaVII(DidTiming(148499)), 0x41, 16]);
        SetCta(bytes, 2, [0x41, 4, 0xE2, 0x0F, 2]);
        var modes = Capability(bytes).Modes;
        var typeVII = Assert.Single(modes.Where(mode => mode.Encoding == "cta-vtdb-vii"));
        Assert.True(typeVII.YCbCr420Encoding); Assert.Null(typeVII.YCbCr420CapabilityMap);
        Assert.False(Assert.Single(modes.Where(mode => mode.Code == 16)).YCbCr420Supported);
        Assert.True(Assert.Single(modes.Where(mode => mode.Code == 4)).YCbCr420Supported);
        bytes[256 + 8] = 4; AssertCtaVIIUnknown(Checked(bytes)); // Bit2 is not a third ordinary SVD.
    }

    [Fact]
    public void CtaTypeVIIIsNotAnEighteenByteDtdInTheGlobalNativeCount()
    {
        var bytes = Cta(CtaVII(DidTiming(148499))); Dtd().CopyTo(bytes, 54); bytes[131] = 1;
        var modes = Capability(bytes).Modes;
        Assert.True(Assert.Single(modes.Where(mode => mode.Encoding == "edid-detailed")).Native);
        Assert.Null(Assert.Single(modes.Where(mode => mode.Encoding == "cta-vtdb-vii")).Native);
        bytes[131] = 2; AssertCtaVIIUnknown(Checked(bytes));
    }

    [Fact]
    public void CtaTypeVIIAndOriginalDisplayIdTypeVIIKeepTheirDistinctContainerMeaning()
    {
        var bytes = Base(2); SetCta(bytes, 1, CtaVII(DidTiming(148499)));
        SetDisplayId(bytes, 2, 0x20, 0x0A, DidTiming(148499), 0, false);
        var modes = Capability(bytes).Modes;
        var cta = Assert.Single(modes.Where(mode => mode.Encoding == "cta-vtdb-vii"));
        var did = Assert.Single(modes.Where(mode => mode.Encoding == "displayid-vii"));
        Assert.Equal(cta.Timing, did.Timing); Assert.Null(cta.DscPassThrough); Assert.True(did.DscPassThrough);
        Assert.Equal((byte)3, cta.ContainerRevision); Assert.Equal((byte)0x20, did.ContainerRevision);
        Assert.Equal((byte)2, cta.ContainerFlags); Assert.Equal((byte)0x0A, did.ContainerFlags);
        Assert.Equal(7u, cta.ByteOffset); Assert.Equal(8u, did.ByteOffset);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CtaTypeVIIFullHfOverrideChainAndHdmiModesRemainIndependent(bool badLastChecksum)
    {
        var bytes = Base(2); bytes[126] = 1;
        SetCta(bytes, 1, [0xE2, 0x78, 2, .. CtaVII(DidTiming(148499))]);
        SetCta(bytes, 2, [0x6B, 3, 12, 0, 16, 0, 0x20, 0, 0x20, 0, 0x20, 1]);
        Checked(bytes);
        if (badLastChecksum) { bytes[^1]++; AssertCtaVIIUnknown(bytes); return; }
        var modes = Capability(bytes).Modes;
        var typeVII = Assert.Single(modes.Where(mode => mode.Encoding == "cta-vtdb-vii")); Assert.Equal(10u, typeVII.ByteOffset);
        var hdmi = Assert.Single(modes.Where(mode => mode.Encoding == "hdmi-vic")); Assert.Equal(1u, hdmi.Code); Assert.Null(hdmi.Timing);
        Assert.Equal(new EdidModeRate(60, 1), typeVII.Timing!.FrameRate);
    }

    [Fact]
    public void CtaTypeVIINonzeroTailAndUnknownFollowingTimingDoNotPublishAPrefix()
    {
        var bytes = Cta(CtaVII(DidTiming(148499))); bytes[128 + 126] = 1; AssertCtaVIIUnknown(Checked(bytes));
        AssertCtaVIIUnknown(Cta([.. CtaVII(DidTiming(148499)), 0xE1, 0x23]));
    }

    private static byte[] CtaVII(byte[] timing, byte header = 2) => [0xF6, 0x22, header, .. timing];

    private static byte[] CtaVIIInterlaced()
    {
        var d = DidTiming(74249); d[3] = 0x14; WriteMinusOne(d, 14, 45); WriteMinusOne(d, 18, 10); return d;
    }

    private static void AssertCtaVIIUnknown(byte[] bytes)
    {
        var read = WindowsMonitorEdidReader.CreateReadResult(bytes);
        Assert.True(read.Complete); Assert.NotNull(read.Capabilities); Assert.Null(read.Capabilities.ModeCapability);
        Assert.Equal(bytes, Convert.FromBase64String(read.RawEdidDeclaredBase64!));
    }
}
