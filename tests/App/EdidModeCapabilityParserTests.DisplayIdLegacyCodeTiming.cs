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
    public void DisplayIdIVAllAssignedDmtCodesKeepByteIdentityAndIndependentGeometry(uint code, uint width, uint height, bool interlaced, bool reduced)
    {
        foreach (var version in new byte[] { 0x12 })
        {
            var bytes = Did4Edid(version, 1, [checked((byte)code)]); var mode = Assert.Single(Capability(bytes).Modes);
            Assert.Equal("displayid-iv-dmt-code", mode.Encoding); Assert.Equal(code, mode.Code);
            Assert.Equal((width, height), (mode.Width!.Value, mode.Height!.Value)); Assert.Equal(interlaced, mode.Interlaced); Assert.Equal(reduced, mode.ReducedBlanking);
            Assert.Equal((1u, 8u), (mode.BlockIndex, mode.ByteOffset)); Assert.Equal(new byte[] { (byte)code }, mode.RawDescriptor.ToArray());
            Assert.Equal((byte)1, mode.Revision); Assert.Equal((byte)1, mode.ContainerFlags); Assert.Equal(version, mode.ContainerRevision);
            Assert.Null(mode.Native); Assert.Null(mode.Preferred); Assert.Null(mode.Timing); Assert.Null(mode.DscPassThrough);
            Assert.Null(mode.YCbCr420Only); Assert.Null(mode.YCbCr420Encoding); Assert.Null(mode.YCbCr420Supported); Assert.Null(mode.YCbCr420CapabilityMap);
            Assert.Null(mode.BitIndex); Assert.Null(mode.Formula); Assert.Null(mode.CvtParameters);
            bytes[136] = 0; Assert.Equal(new byte[] { (byte)code }, mode.RawDescriptor.ToArray());
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
    public void DisplayIdIVNominalFactsDoNotInventFractionalRatesOrClock(uint code, uint? rate)
    {
        var mode = Assert.Single(Capability(Did4Edid(0x12, 1, [checked((byte)code)])).Modes);
        Assert.Equal(rate, mode.NominalVerticalRateHz); Assert.Null(mode.Timing);
    }

    [Fact]
    public void DisplayIdIVIndependent256ByteLiteralPreserves04And58RawAndOffsets()
    {
        var bytes = Convert.FromBase64String("AP///////wAAAAAAAAAAAAAAAQS1AAAAAAAAAAAAAAAAAAAAAAABAQEBAQEBAQEBAQEBAQEBAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAATtwEgUCAAYBAgRYggAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAkA==");
        Assert.Equal(bytes, Did4Edid(0x12, 1, [4, 88]));
        var modes = Capability(bytes).Modes; Assert.Equal(2, modes.Length);
        Assert.Equal(new uint?[] { 4, 88 }, modes.Select(mode => mode.Code)); Assert.Equal(new uint[] { 8, 9 }, modes.Select(mode => mode.ByteOffset));
        Assert.Equal((640u, 480u, 60u), (modes[0].Width!.Value, modes[0].Height!.Value, modes[0].NominalVerticalRateHz!.Value));
        Assert.Equal((4096u, 2160u), (modes[1].Width!.Value, modes[1].Height!.Value)); Assert.True(modes[1].ReducedBlanking);
        Assert.Null(modes[1].NominalVerticalRateHz); Assert.Equal(new byte[] { 4 }, modes[0].RawDescriptor.ToArray()); Assert.Equal(new byte[] { 88 }, modes[1].RawDescriptor.ToArray());
        var read = WindowsMonitorEdidReader.CreateReadResult(bytes); Assert.Equal(bytes, Convert.FromBase64String(read.RawEdidDeclaredBase64!));
    }

    [Fact]
    public void DisplayIdIVNeverCombinesTwoByteCodesOrBorrowsTypeVIIIWidthFlag()
    {
        var modes = Capability(Did4Edid(0x12, 1, [4, 1])).Modes;
        Assert.Equal(new uint?[] { 4, 1 }, modes.Select(mode => mode.Code)); Assert.All(modes, mode => Assert.Single(mode.RawDescriptor));
        AssertDid4Unknown(Did4Edid(0x12, 9, [4, 1]));
        AssertDid4Unknown(Did4Edid(0x12, 1, [4, 0])); // Second byte is unknown code0, not a wide low/high pair.
    }

    [Theory]
    [InlineData(0)]
    [InlineData(89)]
    [InlineData(255)]
    public void DisplayIdIVUnknownLastCodeRejectsEarlierGoodDeclarations(byte code)
        => AssertDid4Unknown(Did4Edid(0x12, 1, [4, code]));

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(17)]
    [InlineData(33)]
    [InlineData(41)]
    [InlineData(73)]
    [InlineData(137)]
    [InlineData(193)]
    [InlineData(201)]
    [InlineData(255)]
    public void DisplayIdIVUnverifiedRevisionFlagsAndOtherCodeDomainsRemainWholeUnknown(byte flags)
        => AssertDid4Unknown(Did4Edid(0x12, flags, [4]));

    [Fact]
    public void DisplayIdIVEmptyListRemainsOutsideVerifiedSupportRange()
        => AssertDid4Unknown(Did4Edid(0x12, 1, []));

    [Theory]
    [InlineData(0x20)]
    public void DisplayIdIVLegacyTagDoesNotExtendTwoXContainers(byte version)
        => AssertDid4Unknown(Did4Edid(version, 1, [4]));

    [Fact]
    public void DisplayIdIVNaturalMaximum118ByteListIsNotTruncatedOrDeduplicated()
    {
        var modes = Capability(Did4Edid(0x12, 1, Enumerable.Repeat((byte)4, 118).ToArray())).Modes;
        Assert.Equal(118, modes.Length);
        for (var index = 0; index < 118; index++)
        {
            Assert.Equal(4u, modes[index].Code); Assert.Equal((uint)(8 + index), modes[index].ByteOffset); Assert.Single(modes[index].RawDescriptor);
        }
    }

    [Fact]
    public void DisplayIdIVAllEightyEightCodesAndContinuationPositionsArePreserved()
    {
        var bytes = Base(2);
        Did4Section(bytes, 1, 0x12, Did4Data(1, Enumerable.Range(1, 80).Select(code => (byte)code).ToArray()), 1, false);
        Did4Section(bytes, 2, 0x12, Did4Data(1, Enumerable.Range(81, 8).Select(code => (byte)code).ToArray()), 0, true);
        var modes = Capability(bytes).Modes; Assert.Equal(88, modes.Length);
        for (var index = 0; index < 88; index++)
        {
            Assert.Equal((uint)(index + 1), modes[index].Code); Assert.Equal((uint)(index < 80 ? 1 : 2), modes[index].BlockIndex);
            Assert.Equal((uint)(8 + (index < 80 ? index : index - 80)), modes[index].ByteOffset);
        }
    }

    [Fact]
    public void DisplayIdIVDuplicateCodesAndSeparateSourcesAreNotMerged()
    {
        var bytes = Base(2);
        Did4Section(bytes, 1, 0x12, [.. Did4Data(1, [4, 4]), .. Did4Data(1, [4])], 1, false);
        Did4Section(bytes, 2, 0x12, Did4Data(1, [4]), 0, true);
        var modes = Capability(bytes).Modes; Assert.Equal(4, modes.Length);
        Assert.Equal(new uint[] { 1, 1, 1, 2 }, modes.Select(mode => mode.BlockIndex));
        Assert.Equal(new uint[] { 8, 9, 13, 8 }, modes.Select(mode => mode.ByteOffset)); Assert.All(modes, mode => Assert.Equal(4u, mode.Code));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void DisplayIdIVBadExtentInnerChecksumOrPaddingDoesNotLeaveBasePrefix(int variant)
    {
        var bytes = Did4Edid(0x12, 1, [4, 88]); Dtd().CopyTo(bytes, 54);
        if (variant == 0) bytes[135] = 3;
        if (variant == 1) bytes[130] = 122;
        if (variant == 2) bytes[138] ^= 1;
        if (variant == 3) bytes[140] = 1;
        if (variant is 0 or 3) DisplayIdChecksum(bytes, 1);
        Checked(bytes); AssertDid4Unknown(bytes);
    }

    [Fact]
    public void DisplayIdIVOuterChecksumFailureIsNotRepairedByUnknownAssertion()
    {
        var bytes = Did4Edid(0x12, 1, [4]); bytes[^1] ^= 1; AssertDid4Unknown(bytes);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public void DisplayIdIVMalformedOrMixedVersionContinuationRejectsFirstSection(int variant)
    {
        var bytes = Base(2); Did4Section(bytes, 1, 0x12, Did4Data(1, [4]), 1, false);
        Did4Section(bytes, 2, 0x12, Did4Data(1, [88]), 0, true);
        if (variant == 0) bytes[132] = 0;
        if (variant == 1) bytes[257] = 0x13;
        if (variant == 2) bytes[259] = 2;
        if (variant == 3) bytes[260] = 1;
        if (variant == 4) bytes[264] = 89;
        if (variant == 6) { bytes[257] = 0x20; bytes[261] = 0x23; bytes[262] = 0; }
        DisplayIdChecksum(bytes, 1); DisplayIdChecksum(bytes, 2); Checked(bytes);
        if (variant == 5) Array.Resize(ref bytes, 256);
        AssertDid4Unknown(bytes);
    }

    [Fact]
    public void DisplayIdIVUnknownFinalSubblockDoesNotPublishGoodPrefix()
    {
        var bytes = Base(1); Dtd().CopyTo(bytes, 54);
        Did4Section(bytes, 1, 0x12, [.. Did4Data(1, [4]), 0x14, 0, 1, 1], 0, false); AssertDid4Unknown(Checked(bytes));
    }

    [Fact]
    public void DisplayIdIVKeepsExistingTypeIAndSeparateTypeVIIIBehavior()
    {
        var bytes = Base(1);
        Did4Section(bytes, 1, 0x12, [3, 0, 20, .. DidTiming(14849), .. Did4Data(1, [4])], 0, false);
        var modes = Capability(bytes).Modes; Assert.Equal(2, modes.Length); Assert.Equal("displayid-i", modes[0].Encoding);
        Assert.NotNull(modes[0].Timing); Assert.True(modes[0].Preferred); Assert.Null(modes[1].Timing); Assert.Null(modes[1].Preferred);
        var twoX = Base(1); Did4Section(twoX, 1, 0x20, [0x23, 8, 4, 4, 0, 88, 0], 0, false);
        var eighth = Capability(twoX).Modes; Assert.Equal(2, eighth.Length);
        Assert.All(eighth, mode => { Assert.Equal("displayid-dmt-code", mode.Encoding); Assert.Equal((byte)0, mode.Revision); Assert.Equal(2, mode.RawDescriptor.Length); });
    }

    [Fact]
    public void DisplayIdIVDoesNotConsumeCtaDtdT7T10OrCapabilityMapDomains()
    {
        var bytes = Base(3); Dtd().CopyTo(bytes, 54);
        SetCta(bytes, 1, [0xE4, 0x0D, 129, 145, 146, .. T8T7(148499), 0x41, 16, 0xE2, 0x0F, 1]);
        Did4Section(bytes, 2, 0x12, Did4Data(1, [16]), 0, false);
        SetCta(bytes, 3, [0xEA, 0x2A, 0x20, 0x9B, 0x7F, 7, 0x37, 4, 0x3B, 0x58, 1]);
        var capability = Capability(bytes); var references = Assert.Single(capability.SelectionDeclarations).References;
        Assert.Equal(new[] { "edid-detailed", "cta-vtdb-vii", "cta-vtdb-x" }, references.Select(reference => reference.TargetEncoding));
        Assert.Equal(new uint[] { 0, 1, 3 }, references.Select(reference => reference.Target!.BlockIndex));
        Assert.Equal(new uint[] { 54, 12, 7 }, references.Select(reference => reference.Target!.ByteOffset));
        var legacy = Assert.Single(capability.Modes.Where(mode => mode.Encoding == "displayid-iv-dmt-code"));
        Assert.Equal((1024u, 768u), (legacy.Width!.Value, legacy.Height!.Value)); Assert.Null(legacy.YCbCr420CapabilityMap);
        Assert.True(Assert.Single(capability.Modes.Where(mode => mode.Encoding == "cta-vic")).YCbCr420Supported);
    }

    [Theory]
    [InlineData(129)]
    [InlineData(145)]
    [InlineData(254)]
    public void DisplayIdIVCannotResolveMissingOrUnsupportedCtaSelection(byte svr)
    {
        var bytes = Base(2); SetCta(bytes, 1, [0xE2, 0x0D, svr]); Did4Section(bytes, 2, 0x12, Did4Data(1, [4]), 0, false);
        AssertDid4Unknown(Checked(bytes));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void DisplayIdIVHfEffectiveLengthRequiresCompleteTail(int variant)
    {
        var bytes = Base(2); bytes[126] = 1; SetCta(bytes, 1, [0xE2, 0x78, 2]); Did4Section(bytes, 2, 0x12, Did4Data(1, [4, 88]), 0, false); Checked(bytes);
        if (variant == 0)
        {
            var modes = Capability(bytes).Modes; Assert.Equal(new uint[] { 2, 2 }, modes.Select(mode => mode.BlockIndex));
            Assert.Equal(new uint[] { 8, 9 }, modes.Select(mode => mode.ByteOffset)); return;
        }
        if (variant == 1) Array.Resize(ref bytes, 256);
        if (variant == 2) Array.Resize(ref bytes, 512);
        if (variant == 3) bytes[^1] ^= 1;
        AssertDid4Unknown(bytes);
    }

    [Fact]
    public void DisplayIdIVTypedJsonRoundtripPreservesRawAndNullableFactsByValue()
    {
        var parsed = EdidParser.Parse(Did4Edid(0x12, 1, [4, 88]))!;
        var restored = JsonSerializer.Deserialize<DeviceTopologyEdidCapabilities>(JsonSerializer.Serialize(parsed))!;
        var expected = parsed.ModeCapability!.Modes; var actual = restored.ModeCapability!.Modes; Assert.Equal(expected.Length, actual.Length);
        for (var index = 0; index < actual.Length; index++)
        {
            Assert.Equal((expected[index].Code, expected[index].Width, expected[index].Height, expected[index].NominalVerticalRateHz),
                (actual[index].Code, actual[index].Width, actual[index].Height, actual[index].NominalVerticalRateHz));
            Assert.Equal((expected[index].BlockIndex, expected[index].ByteOffset, expected[index].Encoding, expected[index].Revision),
                (actual[index].BlockIndex, actual[index].ByteOffset, actual[index].Encoding, actual[index].Revision));
            Assert.Equal(expected[index].RawDescriptor.ToArray(), actual[index].RawDescriptor.ToArray());
            Assert.Equal(expected[index].ContainerFlags, actual[index].ContainerFlags); Assert.Equal(expected[index].ContainerRevision, actual[index].ContainerRevision);
        }
    }

    private static byte[] Did4Data(byte flags, byte[] codes) => [6, flags, checked((byte)codes.Length), .. codes];
    private static byte[] Did4Edid(byte version, byte flags, byte[] codes)
    {
        var bytes = Base(1); Did4Section(bytes, 1, version, Did4Data(flags, codes), 0, false); return Checked(bytes);
    }
    private static void Did4Section(byte[] bytes, int block, byte version, byte[] data, byte continuations, bool continuation)
    {
        var offset = checked(block * 128); bytes[offset] = 0x70; bytes[offset + 1] = version;
        bytes[offset + 2] = checked((byte)data.Length); bytes[offset + 3] = continuation ? (byte)0 : (byte)2;
        bytes[offset + 4] = continuations; data.CopyTo(bytes, checked(offset + 5)); DisplayIdChecksum(bytes, block);
    }
    private static void AssertDid4Unknown(byte[] bytes)
    {
        var read = WindowsMonitorEdidReader.CreateReadResult(bytes);
        Assert.True(read.Complete); Assert.NotNull(read.Capabilities); Assert.Null(read.Capabilities.ModeCapability);
        Assert.Equal(bytes, Convert.FromBase64String(read.RawEdidDeclaredBase64!));
    }
}
