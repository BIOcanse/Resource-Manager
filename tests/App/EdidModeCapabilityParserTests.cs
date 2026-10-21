using System.Text.Json;
using ResourceManager.ReadWrite.Windows.Devices.Display;

namespace Resource_Manager_APP.Tests;

public sealed partial class EdidModeCapabilityParserTests
{
    [Fact]
    public void CompleteEmptyIsDifferentFromInvalidOrIncompleteAndDoesNotEraseRaw()
    {
        var bytes = Base();
        var empty = Capability(bytes);
        Assert.Empty(empty.Modes); Assert.Empty(empty.Ranges);
        bytes[126] = 1; Checksums(bytes);
        var read = WindowsMonitorEdidReader.CreateReadResult(bytes);
        Assert.Null(read.Capabilities!.ModeCapability);
        Assert.Equal(10u, read.Capabilities.BitsPerColorChannel);
        Assert.Equal(bytes, Convert.FromBase64String(read.RawEdidDeclaredBase64!));
    }

    [Fact]
    public void BytesBeyondTheDeclaredChainAreNotSilentlyDropped()
    {
        var bytes = Base(1); bytes[126] = 0; Checksums(bytes);
        Assert.Null(EdidParser.Parse(bytes)!.ModeCapability);
        Assert.Equal(bytes, Convert.FromBase64String(WindowsMonitorEdidReader.CreateReadResult(bytes).RawEdidDeclaredBase64!));
    }

    [Fact]
    public void EstablishedBitsGiveNominalDeclarationsWithoutInventedPixelClocks()
    {
        var bytes = Base(); bytes[35] = 0xFF; bytes[36] = 0xFF; bytes[37] = 0x80;
        var modes = Capability(bytes).Modes;
        Assert.Equal(17, modes.Length);
        Assert.Equal((720u, 400u, 70u), (modes[0].Width!.Value, modes[0].Height!.Value, modes[0].NominalVerticalRateHz!.Value));
        Assert.Equal((1152u, 870u, 75u), (modes[16].Width!.Value, modes[16].Height!.Value, modes[16].NominalVerticalRateHz!.Value));
        Assert.True(modes[11].Interlaced); Assert.Equal(87u, modes[11].NominalVerticalRateHz!.Value);
        Assert.All(modes, static mode => Assert.Null(mode.Timing));
        Assert.Equal(35u, modes[0].ByteOffset); Assert.Equal(7, modes[0].BitIndex);
    }

    [Theory]
    [InlineData(0, 1280)]
    [InlineData(1, 1280)]
    [InlineData(2, 1280)]
    [InlineData(3, 800)]
    [InlineData(4, 800)]
    public void StandardAspectZeroUsesDeclaredEdidRevision(int revision, uint height)
    {
        var bytes = Base(revision: (byte)revision); bytes[38] = 129; bytes[39] = 0;
        var mode = Assert.Single(Capability(bytes).Modes);
        Assert.Equal(1280u, mode.Width!.Value); Assert.Equal(height, mode.Height!.Value);
        Assert.Equal(60u, mode.NominalVerticalRateHz!.Value); Assert.Equal(0x8100u, mode.Code);
        Assert.Null(mode.Timing);
    }

    [Theory]
    [InlineData(0x00, 160)]
    [InlineData(0x40, 192)]
    [InlineData(0xC0, 144)]
    public void StandardByteOneDefines256PixelWidthWhileOnlyOneOneIsUnused(byte second, uint height)
    {
        var bytes = Base(); bytes[38] = 1; bytes[39] = second;
        var mode = Assert.Single(Capability(bytes).Modes);
        Assert.Equal(256u, mode.Width!.Value); Assert.Equal(height, mode.Height!.Value);
        Assert.Equal(60u, mode.NominalVerticalRateHz!.Value);
        Assert.Equal((uint)(0x0100 | second), mode.Code); Assert.Null(mode.Timing);
        bytes[39] = 1;
        Assert.Empty(Capability(bytes).Modes);
    }

    [Fact]
    public void FourDetailedSlotsKeepIdenticalResolutionWithDifferentExactRatesAndOriginalBytes()
    {
        var bytes = Base();
        for (var index = 0; index < 4; index++) Dtd().CopyTo(bytes, 54 + 18 * index);
        bytes[72] = 0xF3; bytes[73] = 0x39; // 14835 * 10kHz, not an exact 60000/1001 clock.
        bytes[24] = 2;
        var modes = Capability(bytes).Modes;
        Assert.Equal(4, modes.Length);
        Assert.Equal(new uint[] { 54, 72, 90, 108 }, modes.Select(static mode => mode.ByteOffset));
        Assert.Equal(new EdidModeRate(60, 1), modes[0].Timing!.FrameRate);
        Assert.Equal(new EdidModeRate(1978, 33), modes[1].Timing!.FrameRate);
        Assert.Equal(148_500_000UL, modes[0].Timing!.PixelClockHz);
        Assert.Equal((1920u, 1080u), (modes[0].Width!.Value, modes[0].Height!.Value));
        Assert.Equal(280u, modes[0].Timing!.HorizontalBlanking);
        Assert.Equal(88u, modes[0].Timing!.HorizontalSyncOffset);
        Assert.Equal(44u, modes[0].Timing!.HorizontalSyncWidth);
        Assert.Equal(45u, modes[0].Timing!.VerticalBlanking);
        Assert.True(modes[0].Preferred); Assert.Null(modes[1].Preferred);
        Assert.Null(modes[0].Timing!.FieldRate);
        var captured = modes[0].RawDescriptor.ToArray(); bytes[54] = 0;
        Assert.Equal(Dtd(), captured); Assert.Equal(Dtd(), modes[0].RawDescriptor.ToArray());
    }

    [Fact]
    public void DetailedInterlaceSeparatesThirtyFramesFromSixtyFields()
    {
        var bytes = Base(); Dtd(interlaced: true).CopyTo(bytes, 54);
        var mode = Assert.Single(Capability(bytes).Modes);
        Assert.True(mode.Interlaced); Assert.Equal(1080u, mode.Height!.Value);
        Assert.Equal(540u, mode.Timing!.VerticalActive);
        Assert.Equal(new EdidModeRate(30, 1), mode.Timing.FrameRate);
        Assert.Equal(new EdidModeRate(60, 1), mode.Timing.FieldRate);
        Assert.Equal(2u, mode.Timing.VerticalSyncOffset); Assert.Equal(5u, mode.Timing.VerticalSyncWidth);
    }

    [Fact]
    public void AdditionalStandardAndEstablishedThreeRemainSeparateWithDmtAndReducedBlankingCodes()
    {
        var bytes = Base(); Descriptor(bytes, 54, 0xFA);
        for (var i = 59; i < 71; i += 2) { bytes[i] = 1; bytes[i + 1] = 1; }
        bytes[71] = 0x0A;
        bytes[59] = 129; bytes[60] = 0x80; // 1280x1024 nominal60.
        Descriptor(bytes, 72, 0xF7); bytes[77] = 0x0A; bytes[78] = 0x80; bytes[79] = 0xC0;
        var modes = Capability(bytes).Modes;
        Assert.Equal(4, modes.Length);
        Assert.Equal("edid-additional-standard", modes[0].Encoding);
        Assert.Equal((1280u, 1024u), (modes[0].Width!.Value, modes[0].Height!.Value));
        Assert.Equal(1u, modes[1].Code); Assert.Equal((640u, 350u), (modes[1].Width!.Value, modes[1].Height!.Value));
        Assert.Equal(22u, modes[2].Code); Assert.True(modes[2].ReducedBlanking);
        Assert.Equal(23u, modes[3].Code); Assert.False(modes[3].ReducedBlanking);
        Assert.Equal(72u, modes[2].ByteOffset); Assert.Equal(63, modes[2].BitIndex);
        Assert.All(modes, static mode => Assert.Null(mode.Timing));
    }

    [Fact]
    public void AdditionalStandardUsesTheSame256PixelEncodingAndRequiredTerminator()
    {
        var bytes = Base(); Descriptor(bytes, 54, 0xFA);
        for (var i = 59; i < 71; i += 2) { bytes[i] = 1; bytes[i + 1] = 1; }
        bytes[59] = 1; bytes[60] = 0; bytes[71] = 0x0A;
        var mode = Assert.Single(Capability(bytes).Modes);
        Assert.Equal("edid-additional-standard", mode.Encoding); Assert.Equal(59u, mode.ByteOffset);
        Assert.Equal((256u, 160u), (mode.Width!.Value, mode.Height!.Value)); Assert.Equal(0x0100u, mode.Code);
    }

    [Theory]
    [InlineData(0x00)]
    [InlineData(0x09)]
    [InlineData(0x0B)]
    [InlineData(0xFF)]
    public void ReservedEstablishedThreeRevisionRejectsWholeChecksumValidCapability(byte revision)
    {
        var bytes = Base(); Dtd().CopyTo(bytes, 54); Descriptor(bytes, 72, 0xF7);
        bytes[77] = revision; bytes[78] = 0x80; Checksums(bytes);
        Assert.Equal(0, bytes.Sum(static value => (int)value) & 255);
        var read = WindowsMonitorEdidReader.CreateReadResult(bytes);
        Assert.Null(read.Capabilities!.ModeCapability);
        Assert.Equal(bytes, Convert.FromBase64String(read.RawEdidDeclaredBase64!));
    }

    [Theory]
    [InlineData(12)]
    [InlineData(13)]
    [InlineData(14)]
    [InlineData(15)]
    [InlineData(16)]
    [InlineData(17)]
    public void NonzeroEstablishedThreeReservedTailRejectsWholeChecksumValidCapability(int relative)
    {
        var bytes = Base(); Dtd().CopyTo(bytes, 54); Descriptor(bytes, 72, 0xF7);
        bytes[77] = 0x0A; bytes[78] = 0x80; bytes[72 + relative] = 1; Checksums(bytes);
        Assert.Equal(0, bytes.Sum(static value => (int)value) & 255);
        Assert.Null(EdidParser.Parse(bytes)!.ModeCapability);
    }

    [Theory]
    [InlineData(0x00)]
    [InlineData(0x09)]
    [InlineData(0x0B)]
    [InlineData(0xFF)]
    public void ReservedAdditionalStandardTerminatorRejectsWholeChecksumValidCapability(byte terminator)
    {
        var bytes = Base(); Dtd().CopyTo(bytes, 54); Descriptor(bytes, 72, 0xFA);
        for (var i = 77; i < 89; i += 2) { bytes[i] = 1; bytes[i + 1] = 1; }
        bytes[77] = 129; bytes[78] = 0x80; bytes[89] = terminator; Checksums(bytes);
        Assert.Equal(0, bytes.Sum(static value => (int)value) & 255);
        Assert.Null(EdidParser.Parse(bytes)!.ModeCapability);
    }

    [Fact]
    public void AllFortyFourEstablishedThreeBitsAreDeliveredWithoutInventedTimings()
    {
        var bytes = Base(); Descriptor(bytes, 54, 0xF7); bytes[59] = 0x0A;
        for (var offset = 60; offset < 65; offset++) bytes[offset] = 255;
        bytes[65] = 0xF0;
        var modes = Capability(bytes).Modes; Assert.Equal(44, modes.Length);
        Assert.Equal(1u, modes[0].Code); Assert.Equal(74u, modes[43].Code);
        Assert.Equal((1920u, 1440u, 75u), (modes[43].Width!.Value, modes[43].Height!.Value, modes[43].NominalVerticalRateHz!.Value));
        Assert.All(modes, static mode => Assert.Null(mode.Timing));
    }

    [Fact]
    public void CvtCodesKeepBothExplicitSixtyHzFormulaDeclarationsWithoutExpandingTimings()
    {
        var bytes = Base(); Descriptor(bytes, 54, 0xF8); bytes[59] = 1;
        new byte[] { 0x1B, 0x24, 0x29 }.CopyTo(bytes, 60);
        var modes = Capability(bytes).Modes;
        Assert.Equal(2, modes.Length);
        Assert.All(modes, static mode =>
        {
            Assert.Equal((1920u, 1080u, 60u), (mode.Width!.Value, mode.Height!.Value, mode.NominalVerticalRateHz!.Value));
            Assert.Equal("cvt", mode.Formula); Assert.Null(mode.Timing);
        });
        Assert.False(modes[0].ReducedBlanking); Assert.False(modes[0].Preferred);
        Assert.True(modes[1].ReducedBlanking); Assert.True(modes[1].Preferred);
        Assert.Equal(60u, modes[0].ByteOffset); Assert.Equal(new byte[] { 0x1B, 0x24, 0x29 }, modes[1].RawDescriptor.ToArray());
    }

    [Theory]
    [InlineData(0, "gtf-default")]
    [InlineData(1, "range-only")]
    [InlineData(2, "gtf-secondary")]
    [InlineData(4, "cvt")]
    public void RangeFormulaAndIntegerUnitsArePreservedWithoutEnumeratingModes(byte kind, string formula)
    {
        var bytes = Base(); Range(bytes, 54, kind);
        var capability = Capability(bytes); Assert.Empty(capability.Modes);
        var range = Assert.Single(capability.Ranges);
        Assert.Equal(formula, range.Formula);
        Assert.Equal((48u, 144u, 30_000u, 160_000u), (range.MinimumVerticalRateHz, range.MaximumVerticalRateHz,
            range.MinimumHorizontalFrequencyHz, range.MaximumHorizontalFrequencyHz));
        Assert.Equal(kind == 4 ? 599_500_000UL : 600_000_000UL, range.MaximumPixelClockHz);
        Assert.Equal(18, range.RawDescriptor.Length);
        if (kind == 2) Assert.Equal(new EdidSecondaryGtfCurve(60_000, 80, 600, 128, 40), range.SecondaryGtf);
        if (kind == 4)
        {
            Assert.Equal((byte)0x11, range.Cvt!.Version); Assert.Equal(1920u, range.Cvt.MaximumActivePixelsPerLine);
            Assert.True(range.Cvt.StandardBlanking); Assert.True(range.Cvt.ReducedBlanking);
            Assert.Equal(60u, range.Cvt.PreferredVerticalRateHz);
        }
    }

    [Fact]
    public void Edid14RangeOffsetFlagsAdd255WithoutLegacyDoublingHeuristics()
    {
        var bytes = Base(); Range(bytes, 54, 1); bytes[58] = 15;
        var range = Assert.Single(Capability(bytes).Ranges);
        Assert.Equal(303u, range.MinimumVerticalRateHz); Assert.Equal(399u, range.MaximumVerticalRateHz);
        Assert.Equal(285_000u, range.MinimumHorizontalFrequencyHz); Assert.Equal(415_000u, range.MaximumHorizontalFrequencyHz);
    }

    [Theory]
    [InlineData(35, 1)] // Reserved manufacturer timing.
    [InlineData(38, 0)] // Bad standard code, not 01/01.
    [InlineData(69, 1)] // Unsupported border.
    [InlineData(56, 1)] // Invalid DTD active size combined with geometry below.
    public void InvalidSourceNeverPublishesEarlierValidPrefix(int offset, byte value)
    {
        var bytes = Base(); Dtd().CopyTo(bytes, 54); bytes[35] = 0x80;
        if (offset == 35) bytes[37] = 1;
        else if (offset == 56) { bytes[56] = 0; bytes[58] &= 15; }
        else bytes[offset] = value;
        Checksums(bytes);
        Assert.Null(EdidParser.Parse(bytes)!.ModeCapability);
    }

    [Theory]
    [InlineData(0xF7)]
    [InlineData(0xF8)]
    public void Edid13DoesNotInventEdid14DescriptorSemantics(byte tag)
    {
        var bytes = Base(revision: 3); Descriptor(bytes, 54, tag); bytes[59] = 1;
        Checksums(bytes); Assert.Null(EdidParser.Parse(bytes)!.ModeCapability);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void UnsupportedOrInvalidBaseFormulaCannotLeakAnEarlierDetailedMode(int variant)
    {
        var bytes = Base(); Dtd().CopyTo(bytes, 54);
        switch (variant)
        {
            case 0: Range(bytes, 72, 3); break;
            case 1: Range(bytes, 72, 1); bytes[77] = 145; break; // Minimum > maximum.
            case 2: Descriptor(bytes, 72, 0xF8); bytes[77] = 1; break; // Empty first CVT code.
            case 3: Descriptor(bytes, 72, 0xF8); bytes[77] = 2; break; // Unknown CVT code version.
            default: Descriptor(bytes, 72, 0x0F); break; // Manufacturer-specific descriptor.
        }
        Checksums(bytes); Assert.Null(EdidParser.Parse(bytes)!.ModeCapability);
    }

    [Fact]
    public void IndependentOriginalCapabilitiesAndLegacyPositionalConstructorRemainCompatible()
    {
        var bytes = Base(); Descriptor(bytes, 54, 0xFC); bytes[59] = (byte)'A';
        var parsed = EdidParser.Parse(Checked(bytes))!;
        Assert.Equal("A", parsed.ProductName); Assert.NotNull(parsed.ModeCapability);
        var old = new DeviceTopologyEdidCapabilities("EDID 1.4", null, null, null, "Undefined", null, null, null, true, null);
        var (_, _, _, _, _, _, _, _, _, _, _, _) = old;
        Assert.Null(old.ModeCapability);
        var roundtrip = JsonSerializer.Deserialize<DeviceTopologyEdidCapabilities>(JsonSerializer.Serialize(parsed))!;
        Assert.NotNull(roundtrip.ModeCapability); Assert.Empty(roundtrip.ModeCapability.Modes);
        var legacy = JsonSerializer.Deserialize<DeviceTopologyEdidCapabilities>("{\"Version\":\"EDID 1.4\",\"DigitalInterface\":\"Undefined\",\"IsDigital\":true}")!;
        Assert.Null(legacy.ModeCapability);
    }

    private static byte[] Base(int extensions = 0, byte revision = 4)
    {
        var bytes = new byte[checked((extensions + 1) * 128)];
        new byte[] { 0, 255, 255, 255, 255, 255, 255, 0 }.CopyTo(bytes, 0);
        bytes[18] = 1; bytes[19] = revision; bytes[20] = 0xB5; bytes[126] = checked((byte)extensions);
        for (var offset = 38; offset < 54; offset += 2) { bytes[offset] = 1; bytes[offset + 1] = 1; }
        return Checked(bytes);
    }

    private static byte[] Dtd(bool interlaced = false)
        => interlaced
            ? [0x01, 0x1D, 0x80, 0x18, 0x71, 0x1C, 0x16, 0x20, 0x58, 0x2C, 0x25, 0, 0, 0, 0, 0, 0, 0x9E]
            : [0x02, 0x3A, 0x80, 0x18, 0x71, 0x38, 0x2D, 0x40, 0x58, 0x2C, 0x45, 0, 0, 0, 0, 0, 0, 0x1E];

    private static void Descriptor(byte[] bytes, int offset, byte tag) => bytes[offset + 3] = tag;

    private static void Range(byte[] bytes, int offset, byte kind)
    {
        Descriptor(bytes, offset, 0xFD); bytes[offset + 5] = 48; bytes[offset + 6] = 144;
        bytes[offset + 7] = 30; bytes[offset + 8] = 160; bytes[offset + 9] = 60; bytes[offset + 10] = kind;
        if (kind == 2)
            new byte[] { 0, 30, 80, 0x58, 2, 128, 40 }.CopyTo(bytes, offset + 11);
        if (kind == 4)
            new byte[] { 0x11, 8, 240, 0xF8, 0x38, 0xF0, 60 }.CopyTo(bytes, offset + 11);
    }

    private static EdidModeCapability Capability(byte[] bytes)
    {
        var parsed = EdidParser.Parse(Checked(bytes)); Assert.NotNull(parsed);
        Assert.NotNull(parsed.ModeCapability); return parsed.ModeCapability;
    }

    private static byte[] Checked(byte[] bytes) { Checksums(bytes); return bytes; }

    private static void Checksums(byte[] bytes)
    {
        for (var block = 0; block < bytes.Length / 128; block++)
        {
            var offset = block * 128; bytes[offset + 127] = 0;
            var sum = 0; for (var i = 0; i < 127; i++) sum += bytes[offset + i];
            bytes[offset + 127] = unchecked((byte)-sum);
        }
    }
}
