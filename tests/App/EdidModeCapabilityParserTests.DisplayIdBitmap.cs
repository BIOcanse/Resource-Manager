using System.Text.Json;
using ResourceManager.ReadWrite.Windows.Devices.Display;

namespace Resource_Manager_APP.Tests;

public sealed partial class EdidModeCapabilityParserTests
{
    // Independent expected literals: accepted DMT fixture and fixed MIT CTA facts.
    // These tests never call the production code-domain lookup to calculate expectations.
    [Theory]
    [InlineData(1, 640, 350, false, false)]
    [InlineData(2, 640, 400, false, false)]
    [InlineData(3, 720, 400, false, false)]
    [InlineData(4, 640, 480, false, false)]
    [InlineData(5, 640, 480, false, false)]
    [InlineData(6, 640, 480, false, false)]
    [InlineData(7, 640, 480, false, false)]
    [InlineData(8, 800, 600, false, false)]
    [InlineData(9, 800, 600, false, false)]
    [InlineData(10, 800, 600, false, false)]
    [InlineData(11, 800, 600, false, false)]
    [InlineData(12, 800, 600, false, false)]
    [InlineData(13, 800, 600, false, true)]
    [InlineData(14, 848, 480, false, false)]
    [InlineData(15, 1024, 768, true, false)]
    [InlineData(16, 1024, 768, false, false)]
    [InlineData(17, 1024, 768, false, false)]
    [InlineData(18, 1024, 768, false, false)]
    [InlineData(19, 1024, 768, false, false)]
    [InlineData(20, 1024, 768, false, true)]
    [InlineData(21, 1152, 864, false, false)]
    [InlineData(22, 1280, 768, false, true)]
    [InlineData(23, 1280, 768, false, false)]
    [InlineData(24, 1280, 768, false, false)]
    [InlineData(25, 1280, 768, false, false)]
    [InlineData(26, 1280, 768, false, true)]
    [InlineData(27, 1280, 800, false, true)]
    [InlineData(28, 1280, 800, false, false)]
    [InlineData(29, 1280, 800, false, false)]
    [InlineData(30, 1280, 800, false, false)]
    [InlineData(31, 1280, 800, false, true)]
    [InlineData(32, 1280, 960, false, false)]
    [InlineData(33, 1280, 960, false, false)]
    [InlineData(34, 1280, 960, false, true)]
    [InlineData(35, 1280, 1024, false, false)]
    [InlineData(36, 1280, 1024, false, false)]
    [InlineData(37, 1280, 1024, false, false)]
    [InlineData(38, 1280, 1024, false, true)]
    [InlineData(39, 1360, 768, false, false)]
    [InlineData(40, 1360, 768, false, true)]
    [InlineData(41, 1400, 1050, false, true)]
    [InlineData(42, 1400, 1050, false, false)]
    [InlineData(43, 1400, 1050, false, false)]
    [InlineData(44, 1400, 1050, false, false)]
    [InlineData(45, 1400, 1050, false, true)]
    [InlineData(46, 1440, 900, false, true)]
    [InlineData(47, 1440, 900, false, false)]
    [InlineData(48, 1440, 900, false, false)]
    [InlineData(49, 1440, 900, false, false)]
    [InlineData(50, 1440, 900, false, true)]
    [InlineData(51, 1600, 1200, false, false)]
    [InlineData(52, 1600, 1200, false, false)]
    [InlineData(53, 1600, 1200, false, false)]
    [InlineData(54, 1600, 1200, false, false)]
    [InlineData(55, 1600, 1200, false, false)]
    [InlineData(56, 1600, 1200, false, true)]
    [InlineData(57, 1680, 1050, false, true)]
    [InlineData(58, 1680, 1050, false, false)]
    [InlineData(59, 1680, 1050, false, false)]
    [InlineData(60, 1680, 1050, false, false)]
    [InlineData(61, 1680, 1050, false, true)]
    [InlineData(62, 1792, 1344, false, false)]
    [InlineData(63, 1792, 1344, false, false)]
    [InlineData(64, 1792, 1344, false, true)]
    [InlineData(65, 1856, 1392, false, false)]
    [InlineData(66, 1856, 1392, false, false)]
    [InlineData(67, 1856, 1392, false, true)]
    [InlineData(68, 1920, 1200, false, true)]
    [InlineData(69, 1920, 1200, false, false)]
    [InlineData(70, 1920, 1200, false, false)]
    [InlineData(71, 1920, 1200, false, false)]
    [InlineData(72, 1920, 1200, false, true)]
    [InlineData(73, 1920, 1440, false, false)]
    [InlineData(74, 1920, 1440, false, false)]
    [InlineData(75, 1920, 1440, false, true)]
    [InlineData(76, 2560, 1600, false, true)]
    [InlineData(77, 2560, 1600, false, false)]
    [InlineData(78, 2560, 1600, false, false)]
    [InlineData(79, 2560, 1600, false, false)]
    [InlineData(80, 2560, 1600, false, true)]
    public void DisplayIdBitmapEveryDmtBitRetainsIndependentGeometry(uint code, uint width, uint height,
        bool interlaced, bool reduced)
    {
        foreach (var version in new byte[] { 0x12 })
        {
            var payload = BitmapSingleBit(code);
            var bytes = BitmapEdid(version, BitmapData(7, 0, payload));
            var mode = Assert.Single(Capability(bytes).Modes);
            AssertBitmapSource(mode, 7, version, 1, 8, checked((int)code - 1), payload);
            Assert.Equal(code, mode.Code);
            Assert.Equal((width, height), (mode.Width!.Value, mode.Height!.Value));
            Assert.Equal(interlaced, mode.Interlaced);
            Assert.Equal(reduced, mode.ReducedBlanking);
            bytes[136] ^= 255;
            Assert.Equal(payload, mode.RawDescriptor.ToArray());
        }
    }

    [Theory]
    [InlineData(1, 640, 480, 60, false)]
    [InlineData(2, 720, 480, 60, false)]
    [InlineData(3, 720, 480, 60, false)]
    [InlineData(4, 1280, 720, 60, false)]
    [InlineData(5, 1920, 1080, 60, true)]
    [InlineData(6, 720, 480, 60, true)]
    [InlineData(7, 720, 480, 60, true)]
    [InlineData(8, 720, 240, 60, false)]
    [InlineData(9, 720, 240, 60, false)]
    [InlineData(10, 2880, 480, 60, true)]
    [InlineData(11, 2880, 480, 60, true)]
    [InlineData(12, 2880, 240, 60, false)]
    [InlineData(13, 2880, 240, 60, false)]
    [InlineData(14, 1440, 480, 60, false)]
    [InlineData(15, 1440, 480, 60, false)]
    [InlineData(16, 1920, 1080, 60, false)]
    [InlineData(17, 720, 576, 50, false)]
    [InlineData(18, 720, 576, 50, false)]
    [InlineData(19, 1280, 720, 50, false)]
    [InlineData(20, 1920, 1080, 50, true)]
    [InlineData(21, 720, 576, 50, true)]
    [InlineData(22, 720, 576, 50, true)]
    [InlineData(23, 720, 288, 50, false)]
    [InlineData(24, 720, 288, 50, false)]
    [InlineData(25, 2880, 576, 50, true)]
    [InlineData(26, 2880, 576, 50, true)]
    [InlineData(27, 2880, 288, 50, false)]
    [InlineData(28, 2880, 288, 50, false)]
    [InlineData(29, 1440, 576, 50, false)]
    [InlineData(30, 1440, 576, 50, false)]
    [InlineData(31, 1920, 1080, 50, false)]
    [InlineData(32, 1920, 1080, 24, false)]
    [InlineData(33, 1920, 1080, 25, false)]
    [InlineData(34, 1920, 1080, 30, false)]
    [InlineData(35, 2880, 480, 60, false)]
    [InlineData(36, 2880, 480, 60, false)]
    [InlineData(37, 2880, 576, 50, false)]
    [InlineData(38, 2880, 576, 50, false)]
    [InlineData(39, 1920, 1080, 50, true)]
    [InlineData(40, 1920, 1080, 100, true)]
    [InlineData(41, 1280, 720, 100, false)]
    [InlineData(42, 720, 576, 100, false)]
    [InlineData(43, 720, 576, 100, false)]
    [InlineData(44, 720, 576, 100, true)]
    [InlineData(45, 720, 576, 100, true)]
    [InlineData(46, 1920, 1080, 120, true)]
    [InlineData(47, 1280, 720, 120, false)]
    [InlineData(48, 720, 480, 120, false)]
    [InlineData(49, 720, 480, 120, false)]
    [InlineData(50, 720, 480, 120, true)]
    [InlineData(51, 720, 480, 120, true)]
    [InlineData(52, 720, 576, 200, false)]
    [InlineData(53, 720, 576, 200, false)]
    [InlineData(54, 720, 576, 200, true)]
    [InlineData(55, 720, 576, 200, true)]
    [InlineData(56, 720, 480, 240, false)]
    [InlineData(57, 720, 480, 240, false)]
    [InlineData(58, 720, 480, 240, true)]
    [InlineData(59, 720, 480, 240, true)]
    [InlineData(60, 1280, 720, 24, false)]
    [InlineData(61, 1280, 720, 25, false)]
    [InlineData(62, 1280, 720, 30, false)]
    [InlineData(63, 1920, 1080, 120, false)]
    [InlineData(64, 1920, 1080, 100, false)]
    public void DisplayIdBitmapEveryCtaBitRetainsIndependentGeometryAndNominal(uint code, uint width,
        uint height, uint nominal, bool interlaced)
    {
        foreach (var version in new byte[] { 0x12 })
        {
            var payload = BitmapSingleBit(code);
            var mode = Assert.Single(Capability(BitmapEdid(version, BitmapData(8, 0, payload))).Modes);
            AssertBitmapSource(mode, 8, version, 1, 8, checked((int)code - 1), payload);
            Assert.Equal(code, mode.Code);
            Assert.Equal((width, height, nominal),
                (mode.Width!.Value, mode.Height!.Value, mode.NominalVerticalRateHz!.Value));
            Assert.Equal(interlaced, mode.Interlaced);
            Assert.Null(mode.ReducedBlanking);
        }
    }

    [Theory]
    [InlineData(4, 60u)]
    [InlineData(7, 85u)]
    [InlineData(17, 70u)]
    [InlineData(15, null)]
    [InlineData(80, null)]
    public void DisplayIdBitmapDmtKnownNominalAndUnknownRateStayDistinct(uint code, uint? nominal)
    {
        var mode = Assert.Single(Capability(BitmapEdid(0x12, BitmapData(7, 0, BitmapSingleBit(code)))).Modes);
        Assert.Equal(nominal, mode.NominalVerticalRateHz);
        Assert.Null(mode.Timing);
    }

    [Theory]
    [InlineData(7, 1)]
    [InlineData(7, 2)]
    [InlineData(7, 3)]
    [InlineData(7, 4)]
    [InlineData(7, 5)]
    [InlineData(7, 6)]
    [InlineData(7, 7)]
    [InlineData(7, 8)]
    [InlineData(7, 9)]
    [InlineData(7, 10)]
    [InlineData(8, 1)]
    [InlineData(8, 2)]
    [InlineData(8, 3)]
    [InlineData(8, 4)]
    [InlineData(8, 5)]
    [InlineData(8, 6)]
    [InlineData(8, 7)]
    [InlineData(8, 8)]
    public void DisplayIdBitmapEveryPositiveLengthConsumesOnlyItsProvidedBits(byte tag, int length)
    {
        foreach (var version in new byte[] { 0x12 })
        {
            var payload = new byte[length];
            Assert.Empty(Capability(BitmapEdid(version, BitmapData(tag, 0, payload))).Modes);
            payload[^1] = 128;
            var last = Assert.Single(Capability(BitmapEdid(version, BitmapData(tag, 0, payload))).Modes);
            Assert.Equal(checked((uint)(length * 8)), last.Code);
            AssertBitmapSource(last, tag, version, 1, 8, length * 8 - 1, payload);
            Array.Fill(payload, (byte)255);
            var modes = Capability(BitmapEdid(version, BitmapData(tag, 0, payload))).Modes;
            Assert.Equal(length * 8, modes.Length);
            for (var bit = 0; bit < modes.Length; bit++)
            {
                Assert.Equal(checked((uint)(bit + 1)), modes[bit].Code);
                AssertBitmapSource(modes[bit], tag, version, 1, 8, bit, payload);
            }
        }
    }

    [Fact]
    public void DisplayIdBitmapIndependentLiteralAndFormerUnknownShortBlockAreNowFullySupported()
    {
        var bytes = Convert.FromBase64String("AP///////wAAAAAAAAAAAAAAAQS1AAAAAAAAAAAAAAAAAAAAAAABAQEBAQEBAQEBAQEBAQEBAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAATtwEggCAAcAAQEIAAGBUQAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAkA==");
        Assert.Equal(bytes, BitmapEdid(0x12, [7, 0, 1, 1, 8, 0, 1, 0x81]));
        var modes = Capability(bytes).Modes;
        Assert.Equal(new uint?[] { 1, 1, 8 }, modes.Select(mode => mode.Code));
        AssertBitmapSource(modes[0], 7, 0x12, 1, 8, 0, [1]);
        AssertBitmapSource(modes[1], 8, 0x12, 1, 12, 0, [0x81]);
        AssertBitmapSource(modes[2], 8, 0x12, 1, 12, 7, [0x81]);
        var formerUnknown = Assert.Single(Capability(BitmapEdid(0x12, [7, 0, 1, 1])).Modes);
        Assert.Equal(1u, formerUnknown.Code);
        AssertBitmapSource(formerUnknown, 7, 0x12, 1, 8, 0, [1]);
    }

    [Fact]
    public void DisplayIdBitmapBlocksAndContinuationsPreserveOrderDuplicatesAndActualRawLengths()
    {
        var bytes = Base(2);
        BitmapSection(bytes, 1, 0x12, [7, 0, 1, 8, 8, 0, 1, 0x81, 7, 0, 1, 8], 1, false);
        BitmapSection(bytes, 2, 0x12, [8, 0, 1, 2], 0, true);
        var modes = Capability(bytes).Modes;
        Assert.Equal(new uint?[] { 4, 1, 8, 4, 2 }, modes.Select(mode => mode.Code));
        Assert.Equal(new uint[] { 1, 1, 1, 1, 2 }, modes.Select(mode => mode.BlockIndex));
        Assert.Equal(new uint[] { 8, 12, 12, 16, 8 }, modes.Select(mode => mode.ByteOffset));
        Assert.Equal(new int?[] { 3, 0, 7, 3, 1 }, modes.Select(mode => mode.BitIndex));
        Assert.Equal(new[] { "displayid-dmt-bitmap", "displayid-cta-bitmap", "displayid-cta-bitmap",
            "displayid-dmt-bitmap", "displayid-cta-bitmap" }, modes.Select(mode => mode.Encoding));
        Assert.All(modes, mode => Assert.Single(mode.RawDescriptor));
    }

    [Theory]
    [InlineData(7, 10, 9, 720)]
    [InlineData(8, 8, 10, 640)]
    public void DisplayIdBitmapNaturalSectionCapacityDoesNotTruncateOrDeduplicate(byte tag,
        int length, int blocks, int expected)
    {
        var data = new List<byte>();
        for (var index = 0; index < blocks; index++)
            data.AddRange(BitmapData(tag, 0, Enumerable.Repeat((byte)255, length).ToArray()));
        var modes = Capability(BitmapEdid(0x12, data.ToArray())).Modes;
        Assert.Equal(expected, modes.Length);
        for (var index = 0; index < blocks; index++)
        {
            var group = modes.Skip(index * length * 8).Take(length * 8).ToArray();
            Assert.Equal(Enumerable.Range(1, length * 8).Select(value => (uint?)value), group.Select(mode => mode.Code));
            Assert.All(group, mode => Assert.Equal(checked((uint)(8 + index * (length + 3))), mode.ByteOffset));
        }
    }

    [Theory]
    [InlineData(7)]
    [InlineData(8)]
    public void DisplayIdBitmapAllNonzeroHeadersRejectTheWholeValue(byte tag)
    {
        foreach (var version in new byte[] { 0x12 })
            for (var header = 1; header <= 255; header++)
                AssertBitmapUnknown(BitmapEdid(version, BitmapData(tag, checked((byte)header), [1])));
    }

    [Theory]
    [InlineData(7, 0)]
    [InlineData(8, 0)]
    [InlineData(7, 11)]
    [InlineData(8, 9)]
    [InlineData(7, 118)]
    [InlineData(8, 118)]
    public void DisplayIdBitmapZeroOrExcessLengthDoesNotPublishEvenWithOnlyZeroTail(byte tag, int length)
    {
        var payload = new byte[length];
        if (length != 0) payload[0] = 1;
        AssertBitmapUnknown(BitmapEdid(0x12, BitmapData(tag, 0, payload)));
    }

    [Theory]
    [InlineData(7, 0x20)]
    [InlineData(8, 0x20)]
    public void DisplayIdBitmapLegacyTagsDoNotExtendTwoXContainers(byte tag, byte version)
        => AssertBitmapUnknown(BitmapEdid(version, BitmapData(tag, 0, [1])));

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
    public void DisplayIdBitmapMalformedTailAndChecksumsNeverPublishGoodPrefix(int variant)
    {
        var bytes = BitmapEdid(0x12, [7, 0, 1, 8]);
        Dtd().CopyTo(bytes, 54);
        if (variant == 0) { bytes[137] ^= 1; Checked(bytes); } // Inner checksum only is bad.
        if (variant == 1) { Checked(bytes); bytes[^1] ^= 1; } // Outer checksum only is bad.
        if (variant == 2) { bytes[135] = 2; DisplayIdChecksum(bytes, 1); Checked(bytes); }
        if (variant == 3) { bytes[150] = 1; Checked(bytes); } // Nonzero container padding.
        if (variant == 4) { BitmapSection(bytes, 1, 0x12, [7, 0, 1, 8, 0x14, 0, 1, 0], 0, false); Checked(bytes); }
        if (variant == 5) { bytes[126] = 2; Checked(bytes); }
        if (variant == 6) { Array.Resize(ref bytes, 384); Checked(bytes); }
        if (variant == 7) { bytes[131] = 0; DisplayIdChecksum(bytes, 1); Checked(bytes); }
        if (variant == 8) { bytes[129] = 0x11; DisplayIdChecksum(bytes, 1); Checked(bytes); }
        AssertBitmapUnknown(bytes);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void DisplayIdBitmapMalformedContinuationRejectsTheFirstSection(int variant)
    {
        var bytes = Base(2);
        BitmapSection(bytes, 1, 0x12, [7, 0, 1, 8], 1, false);
        BitmapSection(bytes, 2, 0x12, [8, 0, 1, 1], 0, true);
        if (variant == 0) bytes[132] = 0;
        if (variant == 1) bytes[257] = 0x13;
        if (variant == 2) bytes[259] = 2;
        if (variant == 3) bytes[260] = 1;
        if (variant == 4) bytes[262] = 1;
        DisplayIdChecksum(bytes, 1); DisplayIdChecksum(bytes, 2); Checked(bytes);
        if (variant == 5) Array.Resize(ref bytes, 256);
        AssertBitmapUnknown(bytes);
    }

    [Fact]
    public void DisplayIdBitmapMixedCtaReferencesRemainInTheirOriginalDomains()
    {
        var bytes = Base(3); Dtd().CopyTo(bytes, 54);
        SetCta(bytes, 1, [0xE4, 0x0D, 129, 145, 146, .. T8T7(148499), 0x41, 16, 0xE2, 0x0F, 1]);
        BitmapSection(bytes, 2, 0x12, [7, 0, 1, 8, 8, 0, 1, 0x80], 0, false);
        SetCta(bytes, 3, [0xEA, 0x2A, 0x20, 0x9B, 0x7F, 7, 0x37, 4, 0x3B, 0x58, 1]);
        var capability = Capability(bytes);
        var references = Assert.Single(capability.SelectionDeclarations).References;
        Assert.Equal(new[] { "edid-detailed", "cta-vtdb-vii", "cta-vtdb-x" }, references.Select(item => item.TargetEncoding));
        Assert.Equal(new uint[] { 0, 1, 3 }, references.Select(item => item.Target!.BlockIndex));
        Assert.Equal(new uint[] { 54, 12, 7 }, references.Select(item => item.Target!.ByteOffset));
        var bitmaps = capability.Modes.Where(mode => mode.Encoding.EndsWith("-bitmap", StringComparison.Ordinal)).ToArray();
        Assert.Equal(new uint?[] { 4, 8 }, bitmaps.Select(mode => mode.Code));
        Assert.All(bitmaps, mode => Assert.Null(mode.YCbCr420CapabilityMap));
        Assert.True(Assert.Single(capability.Modes.Where(mode => mode.Encoding == "cta-vic")).YCbCr420Supported);
        var badMap = Base(2);
        SetCta(badMap, 1, [0x41, 16, 0xE2, 0x0F, 2]);
        BitmapSection(badMap, 2, 0x12, [8, 0, 1, 1], 0, false);
        AssertBitmapUnknown(Checked(badMap));
    }

    [Theory]
    [InlineData(129)]
    [InlineData(145)]
    [InlineData(254)]
    public void DisplayIdBitmapCannotSatisfyMissingOrUnsupportedCtaSelection(byte svr)
    {
        var bytes = Base(2); SetCta(bytes, 1, [0xE2, 0x0D, svr]);
        BitmapSection(bytes, 2, 0x12, [7, 0, 1, 1, 8, 0, 1, 1], 0, false);
        AssertBitmapUnknown(Checked(bytes));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void DisplayIdBitmapHfOverrideRequiresTheCompleteUnmodifiedTail(int variant)
    {
        var bytes = Base(2); bytes[126] = 1;
        SetCta(bytes, 1, [0xE2, 0x78, 2]);
        BitmapSection(bytes, 2, 0x12, [7, 0, 1, 1, 8, 0, 1, 1], 0, false);
        Checked(bytes);
        if (variant == 0)
        {
            var modes = Capability(bytes).Modes;
            Assert.Equal(new uint?[] { 1, 1 }, modes.Select(mode => mode.Code));
            Assert.All(modes, mode => Assert.Equal(2u, mode.BlockIndex));
            return;
        }
        if (variant == 1) Array.Resize(ref bytes, 256);
        if (variant == 2) Array.Resize(ref bytes, 512);
        if (variant == 3) bytes[^1] ^= 1;
        AssertBitmapUnknown(bytes);
    }

    [Fact]
    public void DisplayIdBitmapTypedJsonRoundTripKeepsFullRawAndRelativeBitPositionsByValue()
    {
        var original = Capability(BitmapEdid(0x12, [7, 0, 2, 8, 0x40, 8, 0, 1, 0x81]));
        var restored = JsonSerializer.Deserialize<EdidModeCapability>(JsonSerializer.Serialize(original))!;
        Assert.Equal(original.Modes.Length, restored.Modes.Length);
        for (var index = 0; index < original.Modes.Length; index++)
        {
            var expected = original.Modes[index]; var actual = restored.Modes[index];
            Assert.Equal((expected.BlockIndex, expected.ByteOffset, expected.Encoding, expected.Revision,
                expected.Code, expected.BitIndex), (actual.BlockIndex, actual.ByteOffset, actual.Encoding,
                actual.Revision, actual.Code, actual.BitIndex));
            Assert.Equal(expected.RawDescriptor.ToArray(), actual.RawDescriptor.ToArray());
            Assert.Equal((expected.Width, expected.Height, expected.NominalVerticalRateHz, expected.Interlaced,
                expected.ReducedBlanking), (actual.Width, actual.Height, actual.NominalVerticalRateHz,
                actual.Interlaced, actual.ReducedBlanking));
            Assert.Equal(expected.ContainerFlags, actual.ContainerFlags);
            Assert.Equal(expected.ContainerRevision, actual.ContainerRevision);
            Assert.Null(actual.Timing);
        }
    }

    private static byte[] BitmapSingleBit(uint code)
    {
        var bit = checked((int)code - 1); var payload = new byte[bit / 8 + 1];
        payload[bit / 8] = checked((byte)(1 << (bit % 8))); return payload;
    }

    private static byte[] BitmapData(byte tag, byte flags, byte[] payload)
        => [tag, flags, checked((byte)payload.Length), .. payload];

    private static byte[] BitmapEdid(byte version, byte[] data)
    {
        var bytes = Base(1); BitmapSection(bytes, 1, version, data, 0, false); return Checked(bytes);
    }

    private static void BitmapSection(byte[] bytes, int block, byte version, byte[] data,
        byte continuations, bool continuation)
    {
        var offset = checked(block * 128);
        bytes[offset] = 0x70; bytes[offset + 1] = version; bytes[offset + 2] = checked((byte)data.Length);
        bytes[offset + 3] = continuation ? (byte)0 : (byte)2; bytes[offset + 4] = continuations;
        data.CopyTo(bytes, checked(offset + 5)); DisplayIdChecksum(bytes, block);
    }

    private static void AssertBitmapSource(EdidModeDeclaration mode, byte tag, byte version,
        uint block, uint offset, int bit, byte[] raw)
    {
        Assert.Equal(tag == 7 ? "displayid-dmt-bitmap" : "displayid-cta-bitmap", mode.Encoding);
        Assert.Equal((block, offset, bit), (mode.BlockIndex, mode.ByteOffset, mode.BitIndex!.Value));
        Assert.Equal(raw, mode.RawDescriptor.ToArray());
        Assert.Equal((byte)0, mode.Revision); Assert.Equal((byte)0, mode.ContainerFlags);
        Assert.Equal(version, mode.ContainerRevision);
        Assert.Null(mode.Native); Assert.Null(mode.Preferred); Assert.Null(mode.Timing);
        Assert.Null(mode.HorizontalPixelRepetition); Assert.Null(mode.DscPassThrough);
        Assert.Null(mode.YCbCr420Only); Assert.Null(mode.YCbCr420Encoding); Assert.Null(mode.YCbCr420Supported);
        Assert.Null(mode.YCbCr420CapabilityMap); Assert.Null(mode.Formula); Assert.Null(mode.CvtParameters);
    }

    private static void AssertBitmapUnknown(byte[] bytes)
    {
        var read = WindowsMonitorEdidReader.CreateReadResult(bytes);
        Assert.True(read.Complete); Assert.Null(read.Capabilities?.ModeCapability);
        Assert.Equal(bytes, Convert.FromBase64String(read.RawEdidDeclaredBase64!));
    }
}
