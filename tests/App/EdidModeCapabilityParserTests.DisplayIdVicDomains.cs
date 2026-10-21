using System.Text.Json;
using ResourceManager.ReadWrite.Windows.Devices.Display;

namespace Resource_Manager_APP.Tests;

public sealed partial class EdidModeCapabilityParserTests
{
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
    [InlineData(65, 1280, 720, 24, false)]
    [InlineData(66, 1280, 720, 25, false)]
    [InlineData(67, 1280, 720, 30, false)]
    [InlineData(68, 1280, 720, 50, false)]
    [InlineData(69, 1280, 720, 60, false)]
    [InlineData(70, 1280, 720, 100, false)]
    [InlineData(71, 1280, 720, 120, false)]
    [InlineData(72, 1920, 1080, 24, false)]
    [InlineData(73, 1920, 1080, 25, false)]
    [InlineData(74, 1920, 1080, 30, false)]
    [InlineData(75, 1920, 1080, 50, false)]
    [InlineData(76, 1920, 1080, 60, false)]
    [InlineData(77, 1920, 1080, 100, false)]
    [InlineData(78, 1920, 1080, 120, false)]
    [InlineData(79, 1680, 720, 24, false)]
    [InlineData(80, 1680, 720, 25, false)]
    [InlineData(81, 1680, 720, 30, false)]
    [InlineData(82, 1680, 720, 50, false)]
    [InlineData(83, 1680, 720, 60, false)]
    [InlineData(84, 1680, 720, 100, false)]
    [InlineData(85, 1680, 720, 120, false)]
    [InlineData(86, 2560, 1080, 24, false)]
    [InlineData(87, 2560, 1080, 25, false)]
    [InlineData(88, 2560, 1080, 30, false)]
    [InlineData(89, 2560, 1080, 50, false)]
    [InlineData(90, 2560, 1080, 60, false)]
    [InlineData(91, 2560, 1080, 100, false)]
    [InlineData(92, 2560, 1080, 120, false)]
    [InlineData(93, 3840, 2160, 24, false)]
    [InlineData(94, 3840, 2160, 25, false)]
    [InlineData(95, 3840, 2160, 30, false)]
    [InlineData(96, 3840, 2160, 50, false)]
    [InlineData(97, 3840, 2160, 60, false)]
    [InlineData(98, 4096, 2160, 24, false)]
    [InlineData(99, 4096, 2160, 25, false)]
    [InlineData(100, 4096, 2160, 30, false)]
    [InlineData(101, 4096, 2160, 50, false)]
    [InlineData(102, 4096, 2160, 60, false)]
    [InlineData(103, 3840, 2160, 24, false)]
    [InlineData(104, 3840, 2160, 25, false)]
    [InlineData(105, 3840, 2160, 30, false)]
    [InlineData(106, 3840, 2160, 50, false)]
    [InlineData(107, 3840, 2160, 60, false)]
    [InlineData(108, 1280, 720, 48, false)]
    [InlineData(109, 1280, 720, 48, false)]
    [InlineData(110, 1680, 720, 48, false)]
    [InlineData(111, 1920, 1080, 48, false)]
    [InlineData(112, 1920, 1080, 48, false)]
    [InlineData(113, 2560, 1080, 48, false)]
    [InlineData(114, 3840, 2160, 48, false)]
    [InlineData(115, 4096, 2160, 48, false)]
    [InlineData(116, 3840, 2160, 48, false)]
    [InlineData(117, 3840, 2160, 100, false)]
    [InlineData(118, 3840, 2160, 120, false)]
    [InlineData(119, 3840, 2160, 100, false)]
    [InlineData(120, 3840, 2160, 120, false)]
    [InlineData(121, 5120, 2160, 24, false)]
    [InlineData(122, 5120, 2160, 25, false)]
    [InlineData(123, 5120, 2160, 30, false)]
    [InlineData(124, 5120, 2160, 48, false)]
    [InlineData(125, 5120, 2160, 50, false)]
    [InlineData(126, 5120, 2160, 60, false)]
    [InlineData(127, 5120, 2160, 100, false)]
    [InlineData(193, 5120, 2160, 120, false)]
    [InlineData(194, 7680, 4320, 24, false)]
    [InlineData(195, 7680, 4320, 25, false)]
    [InlineData(196, 7680, 4320, 30, false)]
    [InlineData(197, 7680, 4320, 48, false)]
    [InlineData(198, 7680, 4320, 50, false)]
    [InlineData(199, 7680, 4320, 60, false)]
    [InlineData(200, 7680, 4320, 100, false)]
    [InlineData(201, 7680, 4320, 120, false)]
    [InlineData(202, 7680, 4320, 24, false)]
    [InlineData(203, 7680, 4320, 25, false)]
    [InlineData(204, 7680, 4320, 30, false)]
    [InlineData(205, 7680, 4320, 48, false)]
    [InlineData(206, 7680, 4320, 50, false)]
    [InlineData(207, 7680, 4320, 60, false)]
    [InlineData(208, 7680, 4320, 100, false)]
    [InlineData(209, 7680, 4320, 120, false)]
    [InlineData(210, 10240, 4320, 24, false)]
    [InlineData(211, 10240, 4320, 25, false)]
    [InlineData(212, 10240, 4320, 30, false)]
    [InlineData(213, 10240, 4320, 48, false)]
    [InlineData(214, 10240, 4320, 50, false)]
    [InlineData(215, 10240, 4320, 60, false)]
    [InlineData(216, 10240, 4320, 100, false)]
    [InlineData(217, 10240, 4320, 120, false)]
    [InlineData(218, 4096, 2160, 100, false)]
    [InlineData(219, 4096, 2160, 120, false)]
    public void DisplayIdOriginalCtaVicFactsPreserveFullCodeAndNeverUseVdbNativeBits(uint code, uint width, uint height, uint rate, bool interlaced)
    {
        foreach (var legacy in new[] { false, true })
        foreach (var wide in legacy ? new[] { false } : new[] { false, true })
        foreach (var version in legacy ? new byte[] { 0x12 } : new byte[] { 0x20 })
        {
            var mode = Assert.Single(Capability(DidVicEdid(legacy, version, (byte)(legacy ? 0x41 : wide ? 0x48 : 0x40), code)).Modes);
            Assert.Equal(code, mode.Code); Assert.Equal((width, height, rate, interlaced), (mode.Width!.Value, mode.Height!.Value, mode.NominalVerticalRateHz!.Value, mode.Interlaced!.Value));
            AssertDidVicMetadata(mode, legacy, version, 1, wide, code, 8);
        }
    }

    [Theory]
    [InlineData(1, 3840, 30)]
    [InlineData(2, 3840, 25)]
    [InlineData(3, 3840, 24)]
    [InlineData(4, 4096, 24)]
    public void DisplayIdOriginalHdmiFourCodeLookupKeepsOriginalIdentityAndNoVsdbWrapping(uint code, uint width, uint rate)
    {
        foreach (var legacy in new[] { false, true })
        foreach (var wide in legacy ? new[] { false } : new[] { false, true })
        foreach (var version in legacy ? new byte[] { 0x12 } : new byte[] { 0x20 })
        {
            var mode = Assert.Single(Capability(DidVicEdid(legacy, version, (byte)(legacy ? 0x81 : wide ? 0x88 : 0x80), code)).Modes);
            Assert.Equal((width, 2160u, rate, false), (mode.Width!.Value, mode.Height!.Value, mode.NominalVerticalRateHz!.Value, mode.Interlaced!.Value));
            AssertDidVicMetadata(mode, legacy, version, 2, wide, code, 8);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(128)]
    [InlineData(129)]
    [InlineData(144)]
    [InlineData(192)]
    [InlineData(220)]
    [InlineData(255)]
    [InlineData(257)]
    [InlineData(449)]
    [InlineData(65535)]
    public void DisplayIdCtaFullOriginalDomainRejectsUnknownLastCodeWithoutLowByteAlias(uint code)
    {
        AssertDid8Unknown(DidVicEdid(false, 0x20, 0x48, 16, code));
        if (code <= 255) AssertDid4Unknown(DidVicEdid(true, 0x12, 0x41, 16, code));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    [InlineData(95)]
    [InlineData(255)]
    [InlineData(257)]
    [InlineData(260)]
    [InlineData(65535)]
    public void DisplayIdHdmiFullOriginalDomainRejectsCtaAndLowByteAliases(uint code)
    {
        AssertDid8Unknown(DidVicEdid(false, 0x20, 0x88, 1, code));
        if (code <= 255) AssertDid4Unknown(DidVicEdid(true, 0x12, 0x81, 1, code));
    }

    [Theory]
    [InlineData(0x41, true)]
    [InlineData(0x81, true)]
    [InlineData(0x40, false)]
    [InlineData(0x48, false)]
    [InlineData(0x80, false)]
    [InlineData(0x88, false)]
    public void DisplayIdSixVerifiedHeadersKeepOrderedDuplicatesAndIndependentRawCopies(byte header, bool legacy)
    {
        var hdmi = (header >> 6) == 2; uint a = hdmi ? 1u : 16u; uint b = hdmi ? 4u : 193u;
        var version = legacy ? (byte)0x12 : (byte)0x20; var wide = !legacy && (header & 8) != 0;
        var bytes = DidVicEdid(legacy, version, header, a, b, a); var modes = Capability(bytes).Modes;
        Assert.Equal(new uint?[] { a, b, a }, modes.Select(mode => mode.Code));
        for (var i = 0; i < modes.Length; i++) AssertDidVicMetadata(modes[i], legacy, version, hdmi ? 2 : 1, wide, i == 1 ? b : a, (uint)(8 + i * (wide ? 2 : 1)));
        var json = JsonSerializer.Serialize(Capability(bytes)); var roundtrip = JsonSerializer.Deserialize<EdidModeCapability>(json)!;
        Assert.Equal(json, JsonSerializer.Serialize(roundtrip));
        bytes[136] = 0; Assert.Equal((byte)a, modes[0].RawDescriptor[0]);
    }

    [Fact]
    public void DisplayIdCodeDomainsDoNotBorrowWideFlagOrMixVersionsOrAcceptEmptyAndOddTails()
    {
        foreach (var header in new byte[] { 0x49, 0x89, 0xC1 }) AssertDid4Unknown(Did4Edid(0x12, header, [1, 4]));
        foreach (var header in new byte[] { 0x41, 0x49, 0x81, 0x89, 0xC0, 0xC8 }) AssertDid8Unknown(Did8Edid(0x20, header, [1, 0]));
        foreach (var header in new byte[] { 0x48, 0x88 }) AssertDid8Unknown(Did8Edid(0x20, header, [1, 0, 4]));
        foreach (var header in new byte[] { 0x41, 0x81 }) AssertDid4Unknown(Did4Edid(0x12, header, []));
        foreach (var header in new byte[] { 0x40, 0x48, 0x80, 0x88 }) AssertDid8Unknown(Did8Edid(0x20, header, []));
        AssertDid8Unknown(Did8Edid(0x12, 0x40, [16])); AssertDid4Unknown(Did4Edid(0x20, 0x41, [16]));
    }

    [Fact]
    public void DisplayIdOriginalDomainsKeepSeparateEquivalentModesAndRejectBrokenWholeChain()
    {
        var bytes = Base(1);
        Did8Section(bytes, 1, 0x20, [.. Did8Data(0x40, [95]), .. Did8Data(0x80, [1]), .. Did8Data(0, [4])], 0, false);
        var modes = Capability(Checked(bytes)).Modes; Assert.Equal(3, modes.Length);
        Assert.Equal(new[] { "displayid-cta-vic", "displayid-hdmi-vic", "displayid-dmt-code" }, modes.Select(mode => mode.Encoding));
        Assert.Equal(new uint?[] { 95, 1, 4 }, modes.Select(mode => mode.Code));
        Assert.Equal((modes[0].Width, modes[0].Height, modes[0].NominalVerticalRateHz), (modes[1].Width, modes[1].Height, modes[1].NominalVerticalRateHz));
        var badChecksum = bytes.ToArray(); badChecksum[140] ^= 1; AssertDid8Unknown(badChecksum);
        bytes[140] = 0; DisplayIdChecksum(bytes, 1); AssertDid8Unknown(Checked(bytes));
    }

    private static void AssertDidVicMetadata(EdidModeDeclaration mode, bool legacy, byte version, int domain, bool wide, uint code, uint offset)
    {
        Assert.Equal((legacy ? "displayid-iv-" : "displayid-") + (domain == 1 ? "cta-vic" : "hdmi-vic"), mode.Encoding);
        Assert.Equal(code, mode.Code); Assert.Equal(1u, mode.BlockIndex); Assert.Equal(offset, mode.ByteOffset);
        Assert.Equal(legacy ? (byte)1 : (byte)0, mode.Revision); Assert.Equal(version, mode.ContainerRevision);
        Assert.Equal((byte)((domain << 6) | (legacy ? 1 : wide ? 8 : 0)), mode.ContainerFlags);
        Assert.Equal(Did8Code(code, wide), mode.RawDescriptor.ToArray());
        Assert.Null(mode.Timing); Assert.Null(mode.Native); Assert.Null(mode.Preferred); Assert.Null(mode.ReducedBlanking);
        Assert.Null(mode.YCbCr420Supported); Assert.Null(mode.YCbCr420Only); Assert.Null(mode.YCbCr420CapabilityMap);
        Assert.Null(mode.Formula); Assert.Null(mode.CvtParameters);
    }
    private static byte[] DidVicEdid(bool legacy, byte version, byte header, params uint[] codes)
    {
        var wide = !legacy && (header & 8) != 0;
        var payload = codes.SelectMany(code => Did8Code(code, wide)).ToArray();
        return legacy ? Did4Edid(version, header, payload) : Did8Edid(version, header, payload);
    }
}
