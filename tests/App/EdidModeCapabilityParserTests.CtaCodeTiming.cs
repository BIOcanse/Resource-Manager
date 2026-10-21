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
    public void CtaTypeVIIIAllAssignedDmtCodesMatchIndependentGeometryAndScan(uint code, uint width, uint height, bool interlaced, bool reduced)
    {
        foreach (var header in new byte[] { 0x01, 0x09, 0x21, 0x29 })
        {
            var raw = T8WireCode(code, (header & 8) != 0);
            var bytes = Cta(T8Data(header, raw)); var mode = Assert.Single(Capability(bytes).Modes);
            Assert.Equal("cta-dmt-code", mode.Encoding); Assert.Equal(code, mode.Code);
            Assert.Equal((width, height), (mode.Width!.Value, mode.Height!.Value)); Assert.Equal(interlaced, mode.Interlaced); Assert.Equal(reduced, mode.ReducedBlanking);
            Assert.Equal((1u, 7u), (mode.BlockIndex, mode.ByteOffset)); Assert.Equal(raw, mode.RawDescriptor.ToArray());
            Assert.Equal((byte)1, mode.Revision); Assert.Equal(header, mode.ContainerFlags); Assert.Equal((byte)3, mode.ContainerRevision);
            Assert.Equal((header & 0x20) != 0 ? true : (bool?)null, mode.YCbCr420Supported);
            Assert.Null(mode.YCbCr420Encoding); Assert.Null(mode.YCbCr420Only); Assert.Null(mode.YCbCr420CapabilityMap);
            Assert.Null(mode.Timing); Assert.Null(mode.DscPassThrough); Assert.Null(mode.Preferred); Assert.Null(mode.Native);
            bytes[128 + 7] = 0; Assert.Equal(raw, mode.RawDescriptor.ToArray());
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
    public void CtaTypeVIIINominalIntegersAreOnlyExplicitFactsNotRoundedClockRates(uint code, uint? rate)
    {
        var mode = Assert.Single(Capability(Cta(T8Data(1, T8WireCode(code, false)))).Modes);
        Assert.Equal(rate, mode.NominalVerticalRateHz); Assert.Null(mode.Timing);
    }

    [Fact]
    public void CtaTypeVIIIInterlacedAndFractionalDmtCodesDoNotInventFrameRateOrSixtyHertz()
    {
        var modes = Capability(Cta(T8Data(1, [0x0F, 0x58]))).Modes;
        Assert.True(modes[0].Interlaced); Assert.Equal((1024u, 768u), (modes[0].Width!.Value, modes[0].Height!.Value));
        Assert.Null(modes[0].NominalVerticalRateHz); Assert.Null(modes[0].Timing);
        Assert.False(modes[1].Interlaced); Assert.True(modes[1].ReducedBlanking);
        Assert.Equal((4096u, 2160u), (modes[1].Width!.Value, modes[1].Height!.Value));
        Assert.Null(modes[1].NominalVerticalRateHz); Assert.Null(modes[1].Timing);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(89)]
    [InlineData(255)]
    public void CtaTypeVIIIUnknownByteCodeMakesWholeFieldUnknownAfterAGoodCode(uint code)
    {
        AssertT8Unknown(Cta(T8Data(1, [4, checked((byte)code)])));
        AssertT8Unknown(Cta(T8Data(9, [4, 0, .. T8WireCode(code, true)])));
    }

    [Theory]
    [InlineData(256)]
    [InlineData(260)]
    [InlineData(338)]
    [InlineData(65284)]
    [InlineData(65535)]
    public void CtaTypeVIIIUnassignedFullSixteenBitCodeNeverAliasesItsLowByte(uint code)
    {
        AssertT8Unknown(Cta(T8Data(9, [4, 0, .. T8WireCode(code, true)])));
        AssertT8Unknown(Cta(T8Data(0x29, [4, 0, .. T8WireCode(code, true)])));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(17)]
    [InlineData(25)]
    [InlineData(49)]
    [InlineData(57)]
    [InlineData(65)]
    [InlineData(129)]
    [InlineData(193)]
    [InlineData(249)]
    [InlineData(255)]
    public void CtaTypeVIIIUnverifiedRevisionTypeOrReservedFlagIsNotSilentlyAccepted(byte header)
        => AssertT8Unknown(Cta(T8Data(header, T8WireCode(4, (header & 8) != 0))));

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(27)]
    [InlineData(29)]
    public void CtaTypeVIIIWideCodeRemainderRejectsWholeListInsteadOfItsGoodPrefix(int length)
    {
        var codes = new byte[length]; for (var index = 0; index < length; index += 2) codes[index] = 4;
        AssertT8Unknown(Cta(T8Data(9, codes)));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(9)]
    [InlineData(33)]
    [InlineData(41)]
    public void CtaTypeVIIIRequiresAtLeastOneCompleteCode(byte header)
    {
        AssertT8Unknown(Cta(T8Data(header, [])));
        AssertT8Unknown(Cta([0xE1, 0x23])); // No revision/flags header either.
    }

    [Theory]
    [InlineData(false, 29)]
    [InlineData(true, 14)]
    public void CtaTypeVIIINaturalMaximumListsKeepEveryOriginalCode(bool wide, int count)
    {
        var codes = new List<byte>(); for (uint code = 1; code <= count; code++) codes.AddRange(T8WireCode(code, wide));
        var modes = Capability(Cta(T8Data(wide ? (byte)9 : (byte)1, codes.ToArray()))).Modes;
        Assert.Equal(count, modes.Length);
        for (var index = 0; index < count; index++)
        {
            Assert.Equal((uint)(index + 1), modes[index].Code); Assert.Equal((uint)(7 + index * (wide ? 2 : 1)), modes[index].ByteOffset);
            Assert.Equal(T8WireCode((uint)(index + 1), wide), modes[index].RawDescriptor.ToArray());
        }
    }

    [Fact]
    public void CtaTypeVIIIAllEightyEightCodesAcrossBlocksAreNotSoftwareTruncated()
    {
        var bytes = Base(4);
        for (var block = 1; block <= 4; block++)
        {
            var first = 1 + (block - 1) * 29; var count = Math.Min(29, 89 - first);
            SetCta(bytes, block, T8Data(1, Enumerable.Range(first, count).Select(code => checked((byte)code)).ToArray()));
        }
        var modes = Capability(bytes).Modes; Assert.Equal(88, modes.Length);
        for (var index = 0; index < 88; index++)
        {
            Assert.Equal((uint)(index + 1), modes[index].Code); Assert.Equal((uint)(1 + index / 29), modes[index].BlockIndex);
            Assert.Equal((uint)(7 + index % 29), modes[index].ByteOffset);
        }
    }

    [Fact]
    public void CtaTypeVIIIDuplicateCodesKeepOneAndTwoByteSourceDeclarationsAndAcrossBlocks()
    {
        var modes = Capability(Cta([.. T8Data(1, [4, 4]), .. T8Data(9, [4, 0])])).Modes;
        Assert.Equal(new uint[] { 7, 8, 12 }, modes.Select(mode => mode.ByteOffset));
        Assert.Equal(new[] { 1, 1, 2 }, modes.Select(mode => mode.RawDescriptor.Length));
        Assert.All(modes, mode => Assert.Equal(4u, mode.Code));
        var bytes = Base(2); SetCta(bytes, 1, T8Data(1, [4])); SetCta(bytes, 2, T8Data(1, [4]));
        Assert.Equal(new uint[] { 1, 2 }, Capability(bytes).Modes.Select(mode => mode.BlockIndex));
    }

    [Fact]
    public void CtaTypeVIIIIsNotTheSameCodeDomainAsVdbAndDoesNotConsumeCapabilityMapBits()
    {
        var bytes = Cta([0x41, 16, .. T8Data(1, [16]), 0xE2, 0x0F, 1]); var modes = Capability(bytes).Modes;
        var vic = Assert.Single(modes.Where(mode => mode.Encoding == "cta-vic"));
        var dmt = Assert.Single(modes.Where(mode => mode.Encoding == "cta-dmt-code"));
        Assert.Equal(16u, vic.Code); Assert.Equal(16u, dmt.Code);
        Assert.Equal((1920u, 1080u), (vic.Width!.Value, vic.Height!.Value)); Assert.Equal((1024u, 768u), (dmt.Width!.Value, dmt.Height!.Value));
        Assert.True(vic.YCbCr420Supported); Assert.NotNull(vic.YCbCr420CapabilityMap);
        Assert.Null(dmt.YCbCr420Supported); Assert.Null(dmt.YCbCr420CapabilityMap);
        bytes[128 + 12] = 2; AssertT8Unknown(Checked(bytes)); // Only one ordinary SVD exists.
    }

    [Theory]
    [InlineData(0x0D, 129)]
    [InlineData(0x0D, 145)]
    [InlineData(0x0D, 160)]
    [InlineData(0x0D, 254)]
    [InlineData(0x08, 129)]
    [InlineData(0x08, 145)]
    [InlineData(0x08, 160)]
    [InlineData(0x08, 254)]
    public void CtaTypeVIIIIsNotADtdOrT7TargetAndSvr254RemainsExplicitlyUnsupported(byte tag, byte svr)
        => AssertT8Unknown(Cta([.. T8Data(1, [4, 16]), 0xE2, tag, svr]));

    [Fact]
    public void CtaTypeVIIIWithDirectVicSelectionDoesNotPretendTheDmtIsItsTargetMode()
    {
        var capability = Capability(Cta([.. T8Data(1, [16]), 0xE2, 0x0D, 16]));
        Assert.Equal("cta-dmt-code", Assert.Single(capability.Modes).Encoding);
        var reference = Assert.Single(Assert.Single(capability.SelectionDeclarations).References);
        Assert.Equal("cta-vic", reference.TargetEncoding); Assert.Equal(16u, reference.Code); Assert.Null(reference.Target);
    }

    [Fact]
    public void CtaTypeVIIIInsertedBetweenTwoT7BodiesDoesNotChangeTheirVtdbOrdinals()
    {
        var capability = Capability(Cta([.. T8T7(148499), .. T8Data(1, [16]), .. T8T7(148349), 0xE2, 0x0D, 146]));
        Assert.Equal(3, capability.Modes.Length);
        var reference = Assert.Single(Assert.Single(capability.SelectionDeclarations).References);
        Assert.Equal("cta-vtdb-vii", reference.TargetEncoding); Assert.Equal((1u, 34u), (reference.Target!.BlockIndex, reference.Target.ByteOffset));
        Assert.Equal(DidTiming(148349), reference.Target.RawDescriptor.ToArray());
    }

    [Fact]
    public void CtaTypeVIIIDoesNotConsumeEighteenByteNativeDtdCount()
    {
        var bytes = Cta(T8Data(1, [4])); Dtd().CopyTo(bytes, 54); bytes[131] = 1;
        var modes = Capability(bytes).Modes;
        Assert.True(Assert.Single(modes.Where(mode => mode.Encoding == "edid-detailed")).Native);
        Assert.Null(Assert.Single(modes.Where(mode => mode.Encoding == "cta-dmt-code")).Native);
        bytes[131] = 2; AssertT8Unknown(Checked(bytes));
    }

    [Fact]
    public void CtaTypeVIIIDoesNotRewriteOriginalEstablishedThreeMappingOrMergeItsDmtCode()
    {
        var bytes = Cta(T8Data(1, [7])); Descriptor(bytes, 54, 0xF7); bytes[59] = 0x0A; bytes[60] = 0x10;
        var modes = Capability(bytes).Modes;
        Assert.Equal(new[] { "edid-established-iii", "cta-dmt-code" }, modes.Select(mode => mode.Encoding));
        Assert.All(modes, mode => { Assert.Equal(7u, mode.Code); Assert.Equal(85u, mode.NominalVerticalRateHz);
            Assert.Equal((640u, 480u), (mode.Width!.Value, mode.Height!.Value)); Assert.False(mode.ReducedBlanking); });
        Assert.Equal(18, modes[0].RawDescriptor.Length); Assert.Equal(1, modes[1].RawDescriptor.Length);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void CtaTypeVIIIOuterExtentUnknownLaterBodyOrNonzeroTailRejectsWholePendingValue(int variant)
    {
        var bytes = Cta([0x41, 16, .. T8Data(1, [4])]);
        if (variant == 0) bytes[130]--;
        if (variant == 1) bytes[254] = 1;
        if (variant == 2) bytes = Cta([.. T8Data(1, [4]), 0xE1, 0x2A]);
        AssertT8Unknown(Checked(bytes));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void CtaTypeVIIICompleteHfChainKeepsTailCodesAndRejectsEveryBadTail(int variant)
    {
        var bytes = Base(2); bytes[126] = 1;
        SetCta(bytes, 1, [0xE2, 0x78, 2, .. T8Data(1, [4])]); SetCta(bytes, 2, T8Data(1, [82])); Checked(bytes);
        if (variant == 0)
        {
            var modes = Capability(bytes).Modes; Assert.Equal(new uint?[] { 4, 82 }, modes.Select(mode => mode.Code));
            Assert.Equal(new uint[] { 1, 2 }, modes.Select(mode => mode.BlockIndex)); Assert.Equal(new uint[] { 10, 7 }, modes.Select(mode => mode.ByteOffset));
            return;
        }
        if (variant == 1) bytes[^1]++;
        if (variant == 2) { bytes[256 + 7] = 89; Checked(bytes); }
        if (variant == 3) Array.Resize(ref bytes, 256);
        AssertT8Unknown(bytes);
    }

    private static byte[] T8Data(byte header, byte[] codes) => [checked((byte)(0xE2 + codes.Length)), 0x23, header, .. codes];
    private static byte[] T8WireCode(uint code, bool wide) => wide
        ? [checked((byte)(code & 255)), checked((byte)(code >> 8))] : [checked((byte)code)];
    private static byte[] T8T7(uint clock) => [0xF6, 0x22, 2, .. DidTiming(clock)];
    private static void AssertT8Unknown(byte[] bytes)
    {
        var read = WindowsMonitorEdidReader.CreateReadResult(bytes);
        Assert.True(read.Complete); Assert.NotNull(read.Capabilities); Assert.Null(read.Capabilities.ModeCapability);
        Assert.Equal(bytes, Convert.FromBase64String(read.RawEdidDeclaredBase64!));
    }
}
