using ResourceManager.ReadWrite.Windows.Devices.Display;

namespace Resource_Manager_APP.Tests;

public sealed partial class EdidModeCapabilityParserTests
{
    [Theory]
    [InlineData(1, 1, 640, 480, 60, false)]
    [InlineData(0x81, 1, 640, 480, 60, false)]
    [InlineData(5, 5, 1920, 1080, 60, true)]
    [InlineData(65, 65, 1280, 720, 24, false)]
    [InlineData(0xC1, 193, 5120, 2160, 120, false)]
    [InlineData(219, 219, 4096, 2160, 120, false)]
    public void CtaVicRangeAndNativeBitAreDecodedWithoutMaskingHighCodes(byte svd, uint vic,
        uint width, uint height, uint rate, bool interlaced)
    {
        var bytes = Cta([0x41, svd]);
        var mode = Assert.Single(Capability(bytes).Modes);
        Assert.Equal(vic, mode.Code); Assert.Equal(width, mode.Width!.Value); Assert.Equal(height, mode.Height!.Value);
        Assert.Equal(rate, mode.NominalVerticalRateHz!.Value); Assert.Equal(interlaced, mode.Interlaced);
        Assert.Equal(svd == 0x81, mode.Native); Assert.Equal(1u, mode.BlockIndex); Assert.Equal(5u, mode.ByteOffset);
        Assert.Null(mode.Timing); Assert.Equal(new byte[] { svd }, mode.RawDescriptor.ToArray());
    }

    [Fact]
    public void EveryAssignedBasicVicIsAcceptedAndReservedCodesAreNotAliases()
    {
        var codes = Enumerable.Range(1, 127).Concat(Enumerable.Range(193, 27)).Select(static value => checked((byte)value)).ToArray();
        foreach (var code in codes)
            Assert.Equal((uint)code, Assert.Single(Capability(Cta([0x41, code])).Modes).Code);
        foreach (var code in new byte[] { 0, 128, 220, 255 })
            Assert.Null(EdidParser.Parse(Cta([0x41, code]))!.ModeCapability);
    }

    [Fact]
    public void RepeatedSameResolutionAndCodesRemainIndependentDeclarations()
    {
        var modes = Capability(Cta([0x44, 2, 3, 16, 16])).Modes;
        Assert.Equal(4, modes.Length);
        Assert.Equal(new uint?[] { 2, 3, 16, 16 }, modes.Select(static mode => mode.Code));
        Assert.Equal(new uint[] { 5, 6, 7, 8 }, modes.Select(static mode => mode.ByteOffset));
        Assert.Equal(modes[0].Width!.Value, modes[1].Width!.Value); Assert.Equal(modes[0].Height!.Value, modes[1].Height!.Value);
    }

    [Fact]
    public void CtaPixelRepeatedModesPreserveLogicalWidthAndSampleFactor()
    {
        var mode = Assert.Single(Capability(Cta([0x41, 6])).Modes);
        Assert.Equal(720u, mode.Width!.Value); Assert.Equal(480u, mode.Height!.Value);
        Assert.Equal(2u, mode.HorizontalPixelRepetition); Assert.True(mode.Interlaced);
    }

    [Fact]
    public void Cta420MapUsesAllVdbOrdinalsEvenWhenMapPrecedesThem()
    {
        var bytes = Cta([0xE2, 0x0F, 0x02, 0x41, 16, 0x41, 4, 0xE2, 0x0E, 97]);
        var modes = Capability(bytes).Modes;
        Assert.Equal(3, modes.Length);
        Assert.Equal(16u, modes[0].Code); Assert.False(modes[0].YCbCr420Supported);
        Assert.Equal(4u, modes[1].Code); Assert.True(modes[1].YCbCr420Supported);
        Assert.False(modes[1].YCbCr420Only);
        Assert.Equal(4u, modes[1].YCbCr420CapabilityMap!.ByteOffset);
        Assert.Equal(17, modes[1].YCbCr420CapabilityMap!.BitIndex);
        Assert.Equal(new byte[] { 0xE2, 0x0F, 0x02 }, modes[1].YCbCr420CapabilityMap!.RawDescriptor.ToArray());
        Assert.Equal(97u, modes[2].Code); Assert.True(modes[2].YCbCr420Only); Assert.True(modes[2].YCbCr420Supported);
        Assert.Null(modes[2].Native);
    }

    [Fact]
    public void Empty420MapMeansAllSvdAcrossTheEdidRatherThanOnlyItsCtaBlock()
    {
        var bytes = Base(2); SetCta(bytes, 1, [0x41, 4, 0xE1, 0x0F]); SetCta(bytes, 2, [0x41, 16]);
        var modes = Capability(bytes).Modes;
        Assert.True(modes[0].YCbCr420Supported); Assert.True(modes[1].YCbCr420Supported);
        Assert.Null(modes[0].YCbCr420CapabilityMap!.BitIndex);
    }

    [Fact]
    public void CtaBitmapReferencesCanSelectAnOrdinalOutsideTheirOwnExtension()
    {
        var bytes = Base(2); SetCta(bytes, 1, [0x41, 4]); SetCta(bytes, 2, [0xE2, 0x0F, 0x02, 0x41, 16]);
        var modes = Capability(bytes).Modes;
        Assert.False(modes[0].YCbCr420Supported); Assert.True(modes[1].YCbCr420Supported);
        Assert.Equal(2u, modes[0].YCbCr420CapabilityMap!.BlockIndex);
        Assert.Equal(17, modes[1].YCbCr420CapabilityMap!.BitIndex);
        bytes = Base(2); SetCta(bytes, 1, [0x41, 4]); SetCta(bytes, 2, [0xE2, 0x0E, 4]);
        Checksums(bytes); Assert.Null(EdidParser.Parse(bytes)!.ModeCapability);
    }

    [Fact]
    public void CtaBitmapBeyondSixtyFourSvdIsNotClippedToADriverBitset()
    {
        var payload = new List<byte>();
        foreach (var count in new[] { 31, 31, 3 })
        {
            payload.Add(checked((byte)(0x40 | count)));
            payload.AddRange(Enumerable.Repeat((byte)4, count));
        }
        payload.AddRange([0xEA, 0x0F, 0, 0, 0, 0, 0, 0, 0, 0, 1]);
        var modes = Capability(Cta(payload.ToArray())).Modes;
        Assert.Equal(65, modes.Length);
        Assert.All(modes.Take(64), static mode => Assert.False(mode.YCbCr420Supported));
        Assert.True(modes[64].YCbCr420Supported); Assert.Equal(80, modes[64].YCbCr420CapabilityMap!.BitIndex);
    }

    [Fact]
    public void CtaNativeDetailedCountReferencesActualDetailedDeclarations()
    {
        var bytes = Cta([0x41, 16]); bytes[131] = 1; Dtd().CopyTo(bytes, 134);
        var modes = Capability(bytes).Modes;
        Assert.Equal(2, modes.Length); Assert.True(modes[1].Native);
        Assert.Equal(6u, modes[1].ByteOffset); Assert.Equal(new EdidModeRate(60, 1), modes[1].Timing!.FrameRate);
        bytes[131] = 2; Checksums(bytes); Assert.Null(EdidParser.Parse(bytes)!.ModeCapability);
    }

    [Fact]
    public void CtaNativeDtdOrdinalIncludesBaseDtdBeforeExtensionDtd()
    {
        var bytes = Cta([]); Dtd().CopyTo(bytes, 54); Dtd().CopyTo(bytes, 132); bytes[131] = 1;
        var modes = Capability(bytes).Modes;
        Assert.Equal(2, modes.Length); Assert.True(modes[0].Native); Assert.False(modes[1].Native);
        bytes[131] = 2; modes = Capability(bytes).Modes;
        Assert.All(modes, static mode => Assert.True(mode.Native));
        bytes[131] = 3; Checksums(bytes); Assert.Null(EdidParser.Parse(bytes)!.ModeCapability);
    }

    [Fact]
    public void CtaHeaderMustAgreeAcrossExtensionsAndNativeCountMayReferOnlyToBaseDtd()
    {
        var bytes = Base(2); Dtd().CopyTo(bytes, 54); SetCta(bytes, 1, []); SetCta(bytes, 2, []);
        bytes[131] = 1; bytes[259] = 1;
        Assert.True(Assert.Single(Capability(bytes).Modes).Native);
        bytes[259] = 0; Checksums(bytes); Assert.Null(EdidParser.Parse(bytes)!.ModeCapability);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public void MalformedReferencesOrUnsupportedModeBlocksRejectWholeCapability(int variant)
    {
        var payload = variant switch
        {
            0 => new byte[] { 0x41, 4, 0xE2, 0x0F, 0x02 }, // Map bit beyond one SVD.
            1 => [0x41, 4, 0xE1, 0x0F, 0xE1, 0x0F], // Duplicate map.
            2 => [0x41, 4, 0xE2, 0x0E, 4], // 420-only conflicts with ordinary SVD.
            3 => [0x41, 4, 0x63, 3, 12, 0], // HDMI VSDB not harmless metadata.
            4 => [0x41, 4, 0xE1, 0x22], // Advanced timing block.
            5 => [0x41, 4, 0xC0], // VFDB.
            _ => [0x41, 4, 0xE1, 0x7D] // Unknown extended tag.
        };
        Assert.Null(EdidParser.Parse(Cta(payload))!.ModeCapability);
    }

    [Fact]
    public void HfExtensionCountOverrideIsUnknownEvenIfBasePrefixIsComplete()
    {
        var bytes = Cta([0xE2, 0x78, 2, 0x41, 4]);
        var read = WindowsMonitorEdidReader.CreateReadResult(bytes);
        Assert.Null(read.Capabilities!.ModeCapability);
        Assert.Equal(bytes, Convert.FromBase64String(read.RawEdidDeclaredBase64!));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    public void UnverifiedCtaRevisionDoesNotPretendToBeRevisionThree(byte revision)
    {
        var bytes = Cta([0x41, 16]); bytes[129] = revision; Checksums(bytes);
        Assert.Null(EdidParser.Parse(bytes)!.ModeCapability);
    }

    [Fact]
    public void ExplicitNonTimingMetadataDoesNotSuppressModesButUnknownExtensionsDo()
    {
        var bytes = Cta([0x41, 16, 0x23, 9, 7, 7, 0xE3, 6, 0x0C, 1]);
        var parsed = EdidParser.Parse(bytes)!;
        Assert.Single(parsed.ModeCapability!.Modes); Assert.Equal("HDR10 / PQ / HLG", parsed.HdrFormats);
        bytes[128] = 0x40; Checksums(bytes); Assert.Null(EdidParser.Parse(bytes)!.ModeCapability);
    }

    [Theory]
    [InlineData(127)]
    [InlineData(255)]
    public void AnyChecksumFailureRejectsAllModeDeclarationsWithoutErasingRaw(int offset)
    {
        var bytes = Cta([0x41, 16]); bytes[offset] ^= 1;
        var read = WindowsMonitorEdidReader.CreateReadResult(bytes);
        Assert.Null(read.Capabilities!.ModeCapability); Assert.Equal(bytes, Convert.FromBase64String(read.RawEdidDeclaredBase64!));
    }

    [Fact]
    public void BlockMapValidatesTagsInsteadOfDroppingUndeclaredOrMissingTail()
    {
        var bytes = Base(2); bytes[128] = 0xF0; bytes[129] = 0x02; SetCta(bytes, 2, [0x41, 16]);
        Assert.Single(Capability(bytes).Modes);
        bytes[129] = 0x70; Checksums(bytes); Assert.Null(EdidParser.Parse(bytes)!.ModeCapability);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void CtaBoundsAndDetailedTailCannotBeSilentlyClipped(int variant)
    {
        var bytes = Cta([0x41, 16]);
        switch (variant)
        {
            case 0: bytes[130] = 5; break; // VDB extends past collection.
            case 1: bytes[130] = 128; break;
            case 2: bytes[254] = 1; break; // Garbage after DTD/padding.
            default: bytes[130] = 0; break; // Offset0 with nonzero data is not an empty collection.
        }
        Checksums(bytes); Assert.Null(EdidParser.Parse(bytes)!.ModeCapability);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    public void ActualEmptyCtaCollectionIsSuccessfulEmpty(byte end)
    {
        var bytes = Cta([]); bytes[130] = end;
        var capability = Capability(bytes); Assert.Empty(capability.Modes); Assert.Empty(capability.Ranges);
    }

    [Theory]
    [InlineData(0x12, 0)]
    [InlineData(0x12, 1)]
    [InlineData(0x20, 0)]
    [InlineData(0x20, 1)]
    [InlineData(0x20, 2)]
    public void DisplayIdVersionsUseTheCorrectClockUnitAndPreserveTiming(byte version, byte revision)
    {
        var typeVII = version >= 0x20;
        var bytes = DisplayId(version, revision, DidTiming(14849));
        var mode = Assert.Single(Capability(bytes).Modes);
        Assert.Equal((1920u, 1080u), (mode.Width!.Value, mode.Height!.Value));
        Assert.Equal(typeVII ? "displayid-vii" : "displayid-i", mode.Encoding);
        Assert.Equal(typeVII ? 14_850_000UL : 148_500_000UL, mode.Timing!.PixelClockHz);
        Assert.Equal(new EdidModeRate(typeVII ? 6UL : 60UL, 1), mode.Timing.FrameRate);
        Assert.Equal(1u, mode.BlockIndex); Assert.Equal(8u, mode.ByteOffset);
        Assert.True(mode.Timing.PositiveHorizontalSync); Assert.True(mode.Timing.PositiveVerticalSync);
        Assert.Equal(revision, mode.Revision);
    }

    [Fact]
    public void DisplayIdSeventhRevisionTwoReinterpretsBitSevenWithoutInventing420OnlyOrPreferred()
    {
        var bytes = DisplayId(0x20, 2, DidTiming(148499));
        var mode = Assert.Single(Capability(bytes).Modes);
        Assert.Equal(new EdidModeRate(60, 1), mode.Timing!.FrameRate);
        Assert.True(mode.YCbCr420Encoding); Assert.True(mode.YCbCr420Supported);
        Assert.Null(mode.YCbCr420Only); Assert.Null(mode.Preferred);
        var old = Assert.Single(Capability(DisplayId(0x20, 1, DidTiming(148499))).Modes);
        Assert.True(old.Preferred); Assert.Null(old.YCbCr420Encoding);
    }

    [Fact]
    public void DisplayIdInterlaceUsesFullFrameActiveAndDistinctFieldRate()
    {
        var d = DidTiming(74249); d[3] |= 16;
        WriteMinusOne(d, 14, 45); WriteMinusOne(d, 16, 4); d[17] |= 128; WriteMinusOne(d, 18, 10);
        var mode = Assert.Single(Capability(DisplayId(0x20, 0, d)).Modes);
        Assert.True(mode.Interlaced); Assert.Equal(1080u, mode.Timing!.VerticalActive);
        Assert.Equal(new EdidModeRate(30, 1), mode.Timing.FrameRate);
        Assert.Equal(new EdidModeRate(60, 1), mode.Timing.FieldRate);
    }

    [Fact]
    public void DisplayIdTwoSectionsAreOneCompleteDeclarationChain()
    {
        var bytes = Base(2); SetDisplayId(bytes, 1, 0x20, 0, DidTiming(148499), 1, false);
        SetDisplayId(bytes, 2, 0x20, 0, DidTiming(148499), 0, true);
        var modes = Capability(bytes).Modes;
        Assert.Equal(new uint[] { 1, 2 }, modes.Select(static mode => mode.BlockIndex));
        bytes[260] = 1; DisplayIdChecksum(bytes, 2); Checksums(bytes);
        Assert.Null(EdidParser.Parse(bytes)!.ModeCapability);
        bytes[260] = 0; bytes[257] = 0x21; DisplayIdChecksum(bytes, 2); Checksums(bytes);
        Assert.Null(EdidParser.Parse(bytes)!.ModeCapability);
    }

    [Fact]
    public void DisplayParametersAreKnownNonTimingAndDoNotRequireWindowsQualifyingDescriptorSemantics()
    {
        var bytes = Base(1); var offset = 128; bytes[offset] = 0x70; bytes[offset + 1] = 0x20;
        bytes[offset + 2] = 55; bytes[offset + 3] = 2;
        bytes[offset + 5] = 0x21; bytes[offset + 6] = 0; bytes[offset + 7] = 29;
        bytes[offset + 37] = 0x22; bytes[offset + 38] = 0; bytes[offset + 39] = 20;
        DidTiming(148499).CopyTo(bytes, offset + 40); DisplayIdChecksum(bytes, 1);
        Assert.Single(Capability(bytes).Modes);
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
    public void DisplayIdMalformedOrUnverifiedTailRejectsEarlierBaseModes(int variant)
    {
        var bytes = DisplayId(0x20, 0, DidTiming(148499)); Dtd().CopyTo(bytes, 54);
        switch (variant)
        {
            case 0: bytes[132] = 1; break; // Missing continuation.
            case 1: bytes[135] = 21; break; // Non-integral descriptor length.
            case 2: bytes[134] = 0x10; break; // 21-byte stride: unverified extended fields, not 20-byte loop.
            case 3: bytes[134] = 3; break; // Future revision.
            case 4: bytes[133] = 0x24; break; // Unsupported formula timing.
            case 5: bytes[129] = 0x30; break; // Future container.
            case 6: bytes[130] = 122; break; // Section overflow.
            case 7: bytes[180] = 1; break; // Nonzero section padding.
            default: bytes[133] = 0x7E; break; // Vendor-specific block cannot be assumed harmless.
        }
        if (variant != 6) DisplayIdChecksum(bytes, 1);
        Checksums(bytes); Assert.Null(EdidParser.Parse(bytes)!.ModeCapability);
    }

    [Fact]
    public void DisplayIdInnerChecksumMustPassEvenWhenOuterEdidChecksumIsValid()
    {
        var bytes = DisplayId(0x20, 0, DidTiming(148499)); bytes[156] ^= 1;
        Checksums(bytes); Assert.Null(EdidParser.Parse(bytes)!.ModeCapability);
    }

    [Theory]
    [InlineData(0x12, 0x02, 1)]
    [InlineData(0x12, 0x0C, 13)]
    [InlineData(0x12, 0x0D, 6)]
    [InlineData(0x12, 0x0E, 1)]
    [InlineData(0x12, 0x0F, 10)]
    [InlineData(0x20, 0x26, 9)]
    [InlineData(0x20, 0x29, 16)]
    [InlineData(0x20, 0x2E, 6)]
    public void ExplicitNonTimingDisplayIdWhitelistIsNotConfusedWithUnsupportedTiming(byte version, byte tag, byte length)
    {
        var bytes = Base(1); var offset = 128;
        bytes[offset] = 0x70; bytes[offset + 1] = version; bytes[offset + 2] = checked((byte)(3 + length));
        bytes[offset + 3] = 2; bytes[offset + 5] = tag; bytes[offset + 7] = length;
        DisplayIdChecksum(bytes, 1);
        var capability = Capability(bytes); Assert.Empty(capability.Modes); Assert.Empty(capability.Ranges);
    }

    [Fact]
    public void ValidExtendedSeventhStrideIsExplicitlyUnsupportedRatherThanParsedAsTwentyBytes()
    {
        var bytes = DisplayId(0x20, 0x11, [.. DidTiming(148499), 0]);
        Assert.Null(EdidParser.Parse(bytes)!.ModeCapability);
    }

    [Fact]
    public void DisplayIdInterlacePreservesOddSyncWidthAndDeclaredFullFrame()
    {
        var d = DidTiming(74249); d[3] |= 16;
        var timing = Assert.Single(Capability(DisplayId(0x20, 0, d)).Modes).Timing!;
        Assert.Equal(5u, timing.VerticalSyncWidth);
        Assert.Equal(new EdidModeRate(30, 1), timing.FrameRate);
        Assert.Equal(new EdidModeRate(60, 1), timing.FieldRate);
    }

    [Fact]
    public void DisplayIdActualEmptySectionIsNotConfusedWithMissingContinuation()
    {
        var bytes = Base(1); bytes[128] = 0x70; bytes[129] = 0x20; bytes[131] = 2;
        DisplayIdChecksum(bytes, 1);
        var capability = Capability(bytes); Assert.Empty(capability.Modes);
        bytes[132] = 1; DisplayIdChecksum(bytes, 1); Checksums(bytes);
        Assert.Null(EdidParser.Parse(bytes)!.ModeCapability);
    }

    [Fact]
    public void NonEmptyImmutableModesSurviveJsonWithoutConvertingNominalVicIntoExactRate()
    {
        var parsed = EdidParser.Parse(Cta([0x41, 16]))!;
        var roundtrip = System.Text.Json.JsonSerializer.Deserialize<DeviceTopologyEdidCapabilities>(
            System.Text.Json.JsonSerializer.Serialize(parsed))!;
        var mode = Assert.Single(roundtrip.ModeCapability!.Modes);
        Assert.Equal(16u, mode.Code); Assert.Equal(60u, mode.NominalVerticalRateHz!.Value);
        Assert.Null(mode.Timing); Assert.Equal(new byte[] { 16 }, mode.RawDescriptor.ToArray());
    }

    private static byte[] Cta(byte[] payload)
    {
        var bytes = Base(1); SetCta(bytes, 1, payload); return Checked(bytes);
    }

    private static void SetCta(byte[] bytes, int index, byte[] payload)
    {
        var offset = index * 128; bytes[offset] = 2; bytes[offset + 1] = 3;
        bytes[offset + 2] = checked((byte)(4 + payload.Length)); payload.CopyTo(bytes, offset + 4);
    }

    private static byte[] DisplayId(byte version, byte revision, byte[] timing)
    {
        var bytes = Base(1); SetDisplayId(bytes, 1, version, revision, timing, 0, false); return Checked(bytes);
    }

    private static void SetDisplayId(byte[] bytes, int index, byte version, byte revision, byte[] timing,
        byte continuations, bool continuation)
    {
        var offset = index * 128; bytes[offset] = 0x70; bytes[offset + 1] = version;
        bytes[offset + 2] = checked((byte)(3 + timing.Length)); bytes[offset + 3] = continuation ? (byte)0 : (byte)2;
        bytes[offset + 4] = version == 0x20 ? (byte)0 : continuations;
        bytes[offset + 5] = version >= 0x20 ? (byte)0x22 : (byte)3;
        bytes[offset + 6] = revision; bytes[offset + 7] = checked((byte)timing.Length);
        timing.CopyTo(bytes, offset + 8); DisplayIdChecksum(bytes, index);
    }

    private static void DisplayIdChecksum(byte[] bytes, int index)
    {
        var offset = index * 128; var end = 5 + bytes[offset + 2]; bytes[offset + end] = 0;
        var sum = 0; for (var relative = 1; relative < end; relative++) sum += bytes[offset + relative];
        bytes[offset + end] = unchecked((byte)-sum);
    }

    private static byte[] DidTiming(uint encodedClock)
    {
        var bytes = new byte[20]; bytes[0] = (byte)(encodedClock & 255); bytes[1] = (byte)((encodedClock >> 8) & 255);
        bytes[2] = checked((byte)(encodedClock >> 16)); bytes[3] = 0x84;
        WriteMinusOne(bytes, 4, 1920); WriteMinusOne(bytes, 6, 280); WriteMinusOne(bytes, 8, 88); bytes[9] |= 128;
        WriteMinusOne(bytes, 10, 44); WriteMinusOne(bytes, 12, 1080); WriteMinusOne(bytes, 14, 45);
        WriteMinusOne(bytes, 16, 4); bytes[17] |= 128; WriteMinusOne(bytes, 18, 5); return bytes;
    }

    private static void WriteMinusOne(byte[] bytes, int offset, uint value)
    {
        value = checked(value - 1); bytes[offset] = (byte)(value & 255); bytes[offset + 1] = checked((byte)(value >> 8));
    }
}
