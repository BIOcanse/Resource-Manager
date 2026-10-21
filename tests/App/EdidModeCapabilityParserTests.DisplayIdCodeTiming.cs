using System.Text.Json;
using ResourceManager.ReadWrite.Windows.Devices.Display;

namespace Resource_Manager_APP.Tests;

public sealed partial class EdidModeCapabilityParserTests
{
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
    [InlineData(81, 1366, 768, false, false)]
    [InlineData(82, 1920, 1080, false, false)]
    [InlineData(83, 1600, 900, false, true)]
    [InlineData(84, 2048, 1152, false, true)]
    [InlineData(85, 1280, 720, false, false)]
    [InlineData(86, 1366, 768, false, true)]
    [InlineData(87, 4096, 2160, false, true)]
    [InlineData(88, 4096, 2160, false, true)]
    public void DisplayIdVIIIAllAssignedDmtCodesKeepExactGeometryScanAndSource(uint code, uint width, uint height, bool interlaced, bool reduced)
    {
        foreach (var version in new byte[] { 0x20 })
        foreach (var flags in new byte[] { 0, 8 })
        {
            var raw = Did8Code(code, flags == 8);
            var bytes = Did8Edid(version, flags, raw); var mode = Assert.Single(Capability(bytes).Modes);
            Assert.Equal("displayid-dmt-code", mode.Encoding); Assert.Equal(code, mode.Code);
            Assert.Equal((width, height), (mode.Width!.Value, mode.Height!.Value));
            Assert.Equal(interlaced, mode.Interlaced); Assert.Equal(reduced, mode.ReducedBlanking);
            Assert.Equal((1u, 8u), (mode.BlockIndex, mode.ByteOffset)); Assert.Equal(raw, mode.RawDescriptor.ToArray());
            Assert.Equal((byte)0, mode.Revision); Assert.Equal(flags, mode.ContainerFlags); Assert.Equal(version, mode.ContainerRevision);
            Assert.Null(mode.Timing); Assert.Null(mode.DscPassThrough); Assert.Null(mode.Preferred); Assert.Null(mode.Native);
            Assert.Null(mode.YCbCr420Encoding); Assert.Null(mode.YCbCr420Only); Assert.Null(mode.YCbCr420Supported);
            Assert.Null(mode.YCbCr420CapabilityMap); Assert.Null(mode.Formula); Assert.Null(mode.CvtParameters); Assert.Null(mode.BitIndex);
            bytes[136] = 0; Assert.Equal(raw, mode.RawDescriptor.ToArray());
        }
    }

    [Theory]
    [InlineData(4, 60u)]
    [InlineData(7, 85u)]
    [InlineData(17, 70u)]
    [InlineData(82, 60u)]
    [InlineData(15, null)]
    [InlineData(88, null)]
    [InlineData(87, null)]
    [InlineData(13, null)]
    public void DisplayIdVIIINominalRatesDoNotInventExactOrRoundedRates(uint code, uint? rate)
    {
        var mode = Assert.Single(Capability(Did8Edid(0x20, 8, Did8Code(code, true))).Modes);
        Assert.Equal(rate, mode.NominalVerticalRateHz); Assert.Null(mode.Timing);
    }

    [Fact]
    public void DisplayIdVIIIIndependentWide04005800KeepsFullSourcesAndNoCtaMetadata()
    {
        var bytes = Convert.FromBase64String("AP///////wAAAAAAAAAAAAAAAQS1AAAAAAAAAAAAAAAAAAAAAAABAQEBAQEBAQEBAQEBAQEBAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAATtwIAcCACMIBAQAWABMAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAkA==");
        Assert.Equal(bytes, Did8Edid(0x20, 8, [4, 0, 0x58, 0]));
        var modes = Capability(bytes).Modes; Assert.Equal(2, modes.Length);
        Assert.Equal(new uint?[] { 4, 88 }, modes.Select(mode => mode.Code));
        Assert.Equal(new uint[] { 8, 10 }, modes.Select(mode => mode.ByteOffset));
        Assert.Equal((640u, 480u, 60u), (modes[0].Width!.Value, modes[0].Height!.Value, modes[0].NominalVerticalRateHz!.Value));
        Assert.Equal((4096u, 2160u), (modes[1].Width!.Value, modes[1].Height!.Value));
        Assert.False(modes[0].ReducedBlanking); Assert.True(modes[1].ReducedBlanking); Assert.Null(modes[1].NominalVerticalRateHz);
        Assert.Equal(new byte[] { 4, 0 }, modes[0].RawDescriptor.ToArray()); Assert.Equal(new byte[] { 88, 0 }, modes[1].RawDescriptor.ToArray());
        var read = WindowsMonitorEdidReader.CreateReadResult(bytes);
        Assert.Equal(bytes, Convert.FromBase64String(read.RawEdidDeclaredBase64!));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(89)]
    [InlineData(255)]
    [InlineData(256)]
    [InlineData(260)]
    [InlineData(338)]
    [InlineData(65284)]
    [InlineData(65535)]
    public void DisplayIdVIIIUnknownFullIdRejectsGoodPrefixWithoutLowByteAlias(uint code)
    {
        AssertDid8Unknown(Did8Edid(0x20, 8, [4, 0, .. Did8Code(code, true)]));
        if (code <= 255) AssertDid8Unknown(Did8Edid(0x20, 0, [4, checked((byte)code)]));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(7)]
    [InlineData(9)]
    [InlineData(16)]
    [InlineData(24)]
    [InlineData(32)]
    [InlineData(40)]
    [InlineData(192)]
    [InlineData(200)]
    [InlineData(255)]
    public void DisplayIdVIIIUnverifiedRevisionTypeAndCtaLikeFlagsRemainWholeUnknown(byte flags)
        => AssertDid8Unknown(Did8Edid(0x20, flags, Did8Code(4, (flags & 8) != 0)));

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(117)]
    public void DisplayIdVIIIWideOddTailDoesNotPublishEarlierCodes(int length)
    {
        var codes = new byte[length]; for (var i = 0; i < length; i += 2) codes[i] = 4;
        AssertDid8Unknown(Did8Edid(0x20, 8, codes));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(8)]
    public void DisplayIdVIIIEmptyListIsOutsideVerifiedScopeNotAnObservedEmptyCapability(byte flags)
        => AssertDid8Unknown(Did8Edid(0x20, flags, []));

    [Theory]
    [InlineData(0x12)]
    public void DisplayIdVIIITag23DoesNotExtendLegacyContainerVersions(byte version)
        => AssertDid8Unknown(Did8Edid(version, 0, [4]));

    [Theory]
    [InlineData(false, 118)]
    [InlineData(true, 59)]
    public void DisplayIdVIIINaturalFullSectionKeepsEveryCodeWithoutSoftwareCap(bool wide, int count)
    {
        var codes = new List<byte>(); for (var i = 0; i < count; i++) codes.AddRange(Did8Code(4, wide));
        var modes = Capability(Did8Edid(0x20, wide ? (byte)8 : (byte)0, codes.ToArray())).Modes;
        Assert.Equal(count, modes.Length);
        for (var i = 0; i < count; i++)
        {
            Assert.Equal(4u, modes[i].Code); Assert.Equal((uint)(8 + i * (wide ? 2 : 1)), modes[i].ByteOffset);
            Assert.Equal(wide ? 2 : 1, modes[i].RawDescriptor.Length);
        }
    }

    [Fact]
    public void DisplayIdVIIIAllEightyEightCodesKeepContinuationOrderAndPositions()
    {
        var bytes = Base(2);
        Did8Section(bytes, 1, 0x20, Did8Data(8, Enumerable.Range(1, 59).SelectMany(code => Did8Code((uint)code, true)).ToArray()), 1, false);
        Did8Section(bytes, 2, 0x20, Did8Data(8, Enumerable.Range(60, 29).SelectMany(code => Did8Code((uint)code, true)).ToArray()), 0, true);
        var modes = Capability(bytes).Modes; Assert.Equal(88, modes.Length);
        for (var i = 0; i < 88; i++)
        {
            Assert.Equal((uint)(i + 1), modes[i].Code);
            Assert.Equal((uint)(i < 59 ? 1 : 2), modes[i].BlockIndex);
            Assert.Equal((uint)(8 + (i < 59 ? i : i - 59) * 2), modes[i].ByteOffset);
        }
    }

    [Fact]
    public void DisplayIdVIIIDuplicatesFromSeparateHeadersAndSectionsAreNotMerged()
    {
        var bytes = Base(2);
        Did8Section(bytes, 1, 0x20, [.. Did8Data(0, [4, 4]), .. Did8Data(8, [4, 0])], 1, false);
        Did8Section(bytes, 2, 0x20, Did8Data(0, [4]), 0, true);
        var modes = Capability(bytes).Modes; Assert.Equal(4, modes.Length);
        Assert.Equal(new uint[] { 1, 1, 1, 2 }, modes.Select(mode => mode.BlockIndex));
        Assert.Equal(new uint[] { 8, 9, 13, 8 }, modes.Select(mode => mode.ByteOffset));
        Assert.Equal(new[] { 1, 1, 2, 1 }, modes.Select(mode => mode.RawDescriptor.Length));
        Assert.All(modes, mode => Assert.Equal(4u, mode.Code));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void DisplayIdVIIIEntryOrContainerExtentAndChecksumRejectWholeGoodBasePrefix(int variant)
    {
        var bytes = Did8Edid(0x20, 8, [4, 0, 88, 0]); Dtd().CopyTo(bytes, 54);
        if (variant == 0) bytes[135] = 6; // Declared code payload exceeds actual section extent.
        if (variant == 1) bytes[130] = 122; // Exceeds the original DisplayID section boundary.
        if (variant == 2) bytes[140] ^= 1; // Inner checksum broken, even after outer checksum is recomputed.
        if (variant == 3) bytes[142] = 1; // Nonzero byte after inner checksum.
        if (variant is 0 or 3) DisplayIdChecksum(bytes, 1);
        Checked(bytes); AssertDid8Unknown(bytes);
    }

    [Fact]
    public void DisplayIdVIIIOuterChecksumFailureIsNotRepairedByTheTestAssertion()
    {
        var bytes = Did8Edid(0x20, 0, [4]); bytes[^1] ^= 1;
        AssertDid8Unknown(bytes);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void DisplayIdVIIIMalformedContinuationNeverPublishesFirstSection(int variant)
    {
        var bytes = Base(2);
        Did8Section(bytes, 1, 0x20, Did8Data(0, [4]), 1, false);
        Did8Section(bytes, 2, 0x20, Did8Data(0, [88]), 0, true);
        if (variant == 0) bytes[131] = 0;
        if (variant == 1) bytes[257] = 0x21;
        if (variant == 2) bytes[259] = 2;
        if (variant == 3) bytes[260] = 1;
        if (variant == 4) bytes[264] = 89;
        DisplayIdChecksum(bytes, 1); DisplayIdChecksum(bytes, 2); Checked(bytes);
        if (variant == 5) Array.Resize(ref bytes, 256);
        AssertDid8Unknown(bytes);
    }

    [Fact]
    public void DisplayIdVIIILastUnknownSubblockCannotLeaveASuccessfulPrefix()
    {
        var bytes = Base(1); Dtd().CopyTo(bytes, 54);
        Did8Section(bytes, 1, 0x20, [.. Did8Data(0, [4]), 0x24, 0, 1, 0], 0, false);
        AssertDid8Unknown(Checked(bytes));
    }

    [Fact]
    public void DisplayIdVIIIKeepsExistingIAndVIIAndSeparateT8Declarations()
    {
        var bytes = Base(3); SetCta(bytes, 1, T8Data(0x21, [4]));
        SetDisplayId(bytes, 2, 0x20, 2, DidTiming(148499), 1, false);
        Did8Section(bytes, 3, 0x20, Did8Data(8, [4, 0]), 0, true);
        var modes = Capability(bytes).Modes; Assert.Equal(3, modes.Length);
        Assert.Equal(new[] { "cta-dmt-code", "displayid-vii", "displayid-dmt-code" }, modes.Select(mode => mode.Encoding));
        Assert.True(modes[0].YCbCr420Supported); Assert.True(modes[1].YCbCr420Supported); Assert.Null(modes[2].YCbCr420Supported);
        Assert.NotNull(modes[1].Timing); Assert.Null(modes[2].Timing);
        Assert.NotNull(Capability(DisplayId(0x12, 0, DidTiming(14849))).Modes[0].Timing);
    }

    [Fact]
    public void DisplayIdVIIIIsSeparateFromVdbCmdbAndDoesNotConsumeCtaNativeDtdCount()
    {
        var bytes = Base(2); Dtd().CopyTo(bytes, 54);
        SetCta(bytes, 1, [0x41, 16, 0xE2, 0x0F, 1]); bytes[131] = 1;
        Did8Section(bytes, 2, 0x20, Did8Data(0, [16]), 0, false);
        var modes = Capability(bytes).Modes;
        Assert.True(Assert.Single(modes.Where(mode => mode.Encoding == "edid-detailed")).Native);
        Assert.True(Assert.Single(modes.Where(mode => mode.Encoding == "cta-vic")).YCbCr420Supported);
        var mode = Assert.Single(modes.Where(mode => mode.Encoding == "displayid-dmt-code"));
        Assert.Equal((1024u, 768u), (mode.Width!.Value, mode.Height!.Value)); Assert.Null(mode.YCbCr420CapabilityMap); Assert.Null(mode.Native);
        bytes[136] = 2; AssertDid8Unknown(Checked(bytes)); // One SVD cannot supply capability-map bit1.
    }

    [Fact]
    public void DisplayIdVIIIDoesNotConsumeGlobalCtaT7OrT10AndDtdSelectionOrdinals()
    {
        var bytes = Base(3); Dtd().CopyTo(bytes, 54);
        SetCta(bytes, 1, [0xE4, 0x0D, 129, 145, 146, .. T8T7(148499)]);
        Did8Section(bytes, 2, 0x20, Did8Data(0, [4]), 0, false);
        SetCta(bytes, 3, [0xEA, 0x2A, 0x20, 0x9B, 0x7F, 0x07, 0x37, 0x04, 0x3B, 0x58, 0x01]);
        var capability = Capability(bytes); var refs = Assert.Single(capability.SelectionDeclarations).References;
        Assert.Equal(new[] { "edid-detailed", "cta-vtdb-vii", "cta-vtdb-x" }, refs.Select(value => value.TargetEncoding));
        Assert.Equal(new uint[] { 0, 1, 3 }, refs.Select(value => value.Target!.BlockIndex));
        Assert.Equal(new uint[] { 54, 12, 7 }, refs.Select(value => value.Target!.ByteOffset));
        Assert.Equal(4, capability.Modes.Length);
    }

    [Theory]
    [InlineData(129)]
    [InlineData(145)]
    [InlineData(254)]
    public void DisplayIdVIIINeverResolvesAnUnsupportedOrMissingCtaSelectionTarget(byte svr)
    {
        var bytes = Base(2); SetCta(bytes, 1, [0xE2, 0x0D, svr]);
        Did8Section(bytes, 2, 0x20, Did8Data(0, [4]), 0, false);
        AssertDid8Unknown(Checked(bytes));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void DisplayIdVIIIHonorsHfExtendedLengthAndRejectsMissingExtraOrBadTail(int variant)
    {
        var bytes = Base(2); bytes[126] = 1;
        SetCta(bytes, 1, [0xE2, 0x78, 2]); Did8Section(bytes, 2, 0x20, Did8Data(0, [4, 88]), 0, false);
        Checked(bytes);
        if (variant == 0)
        {
            var modes = Capability(bytes).Modes; Assert.Equal(2, modes.Length);
            Assert.Equal(new uint[] { 2, 2 }, modes.Select(mode => mode.BlockIndex));
            Assert.Equal(new uint[] { 8, 9 }, modes.Select(mode => mode.ByteOffset)); return;
        }
        if (variant == 1) Array.Resize(ref bytes, 256);
        if (variant == 2) Array.Resize(ref bytes, 512);
        if (variant == 3) bytes[^1] ^= 1;
        AssertDid8Unknown(bytes);
    }

    [Fact]
    public void DisplayIdVIIITypedJsonRoundtripRetainsValuesWithoutArrayReferenceEquality()
    {
        var parsed = EdidParser.Parse(Did8Edid(0x20, 8, [4, 0, 88, 0]))!;
        var roundtrip = JsonSerializer.Deserialize<DeviceTopologyEdidCapabilities>(JsonSerializer.Serialize(parsed))!;
        var actual = roundtrip.ModeCapability!.Modes; var expected = parsed.ModeCapability!.Modes;
        Assert.Equal(expected.Length, actual.Length);
        for (var i = 0; i < expected.Length; i++)
        {
            Assert.Equal((expected[i].BlockIndex, expected[i].ByteOffset, expected[i].Encoding, expected[i].Revision),
                (actual[i].BlockIndex, actual[i].ByteOffset, actual[i].Encoding, actual[i].Revision));
            Assert.Equal((expected[i].Code, expected[i].Width, expected[i].Height, expected[i].NominalVerticalRateHz),
                (actual[i].Code, actual[i].Width, actual[i].Height, actual[i].NominalVerticalRateHz));
            Assert.Equal(expected[i].RawDescriptor.ToArray(), actual[i].RawDescriptor.ToArray());
            Assert.Equal(expected[i].ContainerFlags, actual[i].ContainerFlags); Assert.Equal(expected[i].ContainerRevision, actual[i].ContainerRevision);
        }
    }

    private static byte[] Did8Code(uint code, bool wide) => wide
        ? [checked((byte)(code & 255)), checked((byte)(code >> 8))] : [checked((byte)code)];
    private static byte[] Did8Data(byte flags, byte[] codes) => [0x23, flags, checked((byte)codes.Length), .. codes];
    private static byte[] Did8Edid(byte version, byte flags, byte[] codes)
    {
        var bytes = Base(1); Did8Section(bytes, 1, version, Did8Data(flags, codes), 0, false); return Checked(bytes);
    }
    private static void Did8Section(byte[] bytes, int block, byte version, byte[] data, byte continuations, bool continuation)
    {
        var offset = checked(block * 128); bytes[offset] = 0x70; bytes[offset + 1] = version;
        bytes[offset + 2] = checked((byte)data.Length); bytes[offset + 3] = continuation ? (byte)0 : (byte)2;
        bytes[offset + 4] = version == 0x20 ? (byte)0 : continuations;
        data.CopyTo(bytes, checked(offset + 5)); DisplayIdChecksum(bytes, block);
    }
    private static void AssertDid8Unknown(byte[] bytes)
    {
        var read = WindowsMonitorEdidReader.CreateReadResult(bytes);
        Assert.True(read.Complete); Assert.NotNull(read.Capabilities); Assert.Null(read.Capabilities.ModeCapability);
        Assert.Equal(bytes, Convert.FromBase64String(read.RawEdidDeclaredBase64!));
    }
}
