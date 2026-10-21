using System.Text.Json;
using ResourceManager.ReadWrite.Windows.Devices.Display;

namespace Resource_Manager_APP.Tests;

public sealed partial class EdidModeCapabilityParserTests
{
    [Theory]
    [InlineData(0x12, 0)]
    [InlineData(0x12, 1)]
    public void DisplayIdIIIBothLegacyVersionsAndHeadersPreserveIndependentDeclarations(byte version, byte header)
    {
        var input = Did3Edid(version, header, [0x84, 0xEF, 0x3B, 0x14, 0xEF, 0xBB, 0x08, 0, 0, 0x97, 0xFF, 0xFF]);
        var modes = Capability(input).Modes;
        Assert.Equal(4, modes.Length);
        AssertDid3(modes[0], version, header, 8, 1920, 60, false, true, false, new(4, 16, 9), [0x84, 0xEF, 0x3B]);
        AssertDid3(modes[1], version, header, 11, 1920, 60, true, false, true, new(4, 16, 9), [0x14, 0xEF, 0xBB]);
        AssertDid3(modes[2], version, header, 14, 8, 1, false, false, false, new(8, null, null), [8, 0, 0]);
        AssertDid3(modes[3], version, header, 17, 2048, 128, true, true, true, new(7, 256, 135), [0x97, 0xFF, 0xFF]);
        input[136] ^= 1;
        Assert.Equal(new byte[] { 0x84, 0xEF, 0x3B }, modes[0].RawDescriptor.ToArray());
    }

    [Theory]
    [InlineData(0, 1u, 1u)]
    [InlineData(1, 5u, 4u)]
    [InlineData(2, 4u, 3u)]
    [InlineData(3, 15u, 9u)]
    [InlineData(4, 16u, 9u)]
    [InlineData(5, 16u, 10u)]
    [InlineData(6, 64u, 27u)]
    [InlineData(7, 256u, 135u)]
    [InlineData(8, null, null)]
    public void DisplayIdIIIAspectIsOriginalDeclarationNotIntegerTruncatedHeight(byte code, uint? horizontal, uint? vertical)
    {
        var mode = Assert.Single(Capability(Did3Edid(0x12, 0, [code, 0, 0])).Modes);
        Assert.Equal(new EdidAspectRatioDeclaration(code, horizontal, vertical), mode.AspectRatioDeclared);
        Assert.Equal(8u, mode.Width); Assert.Equal(1u, mode.NominalVerticalRateHz);
        Assert.Null(mode.Height); Assert.Null(mode.Timing);
    }

    [Fact]
    public void DisplayIdIIIAllScanPreferredAlgorithmAndRateBoundariesAreDeclaredWithoutClocks()
    {
        for (var algorithm = 0; algorithm <= 1; algorithm++)
        for (var preferred = 0; preferred <= 1; preferred++)
        for (var interlaced = 0; interlaced <= 1; interlaced++)
        foreach (var rate in new byte[] { 0, 127 })
        {
            byte[] descriptor = [checked((byte)(algorithm * 16 + preferred * 128)), 0, checked((byte)(interlaced * 128 + rate))];
            var mode = Assert.Single(Capability(Did3Edid(0x12, 1, descriptor)).Modes);
            AssertDid3(mode, 0x12, 1, 8, 8, checked((uint)rate + 1), interlaced != 0, preferred != 0,
                algorithm != 0, new(0, 1, 1), descriptor);
        }
    }

    [Fact]
    public void DisplayIdIIIZeroDescriptorAndRepeatedSectionsKeepOrderAndDoNotUsePaddingOrDeduplication()
    {
        var bytes = Base(2);
        Did8Section(bytes, 1, 0x12, [5, 0, 6, 0, 0, 0, 0x84, 0xEF, 0x3B], 1, false);
        Did8Section(bytes, 2, 0x12, [5, 1, 3, 0x84, 0xEF, 0x3B], 0, true);
        var modes = Capability(bytes).Modes;
        Assert.Equal(3, modes.Length);
        Assert.Equal(new uint[] { 1, 1, 2 }, modes.Select(mode => mode.BlockIndex));
        Assert.Equal(new uint[] { 8, 11, 8 }, modes.Select(mode => mode.ByteOffset));
        Assert.Equal(8u, modes[0].Width); Assert.Equal(1u, modes[0].NominalVerticalRateHz);
        Assert.Equal(modes[1].RawDescriptor.ToArray(), modes[2].RawDescriptor.ToArray());
        Assert.NotEqual(modes[1].Revision, modes[2].Revision);
    }

    [Fact]
    public void DisplayIdIIINaturalSectionExtentDeliversEveryDescriptorAndLastByte()
    {
        var payload = new byte[117];
        for (var i = 0; i < 39; i++) { payload[i * 3] = 4; payload[i * 3 + 1] = checked((byte)i); payload[i * 3 + 2] = checked((byte)i); }
        var modes = Capability(Did3Edid(0x12, 1, payload)).Modes;
        Assert.Equal(39, modes.Length);
        Assert.Equal(122u, modes[^1].ByteOffset); Assert.Equal(312u, modes[^1].Width); Assert.Equal(39u, modes[^1].NominalVerticalRateHz);
        Assert.Equal(payload, modes.SelectMany(mode => mode.RawDescriptor).ToArray());
    }

    [Fact]
    public void DisplayIdIIIAllOtherHeadersReservedAlgorithmsAndAspectsRejectWholeValue()
    {
        for (var header = 2; header <= 255; header++)
            AssertDid8Unknown(Did3Edid(0x12, checked((byte)header), [0x84, 0xEF, 0x3B]));
        for (var algorithm = 2; algorithm < 8; algorithm++)
            AssertDid8Unknown(Did3Edid(0x12, 0, [0x84, 0xEF, 0x3B, checked((byte)(algorithm * 16)), 0, 0]));
        for (var aspect = 9; aspect < 16; aspect++)
            AssertDid8Unknown(Did3Edid(0x12, 1, [0, 0, 0, checked((byte)aspect), 0, 0]));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(5)]
    public void DisplayIdIIINonCompletePayloadDoesNotPublishGoodPrefix(int length)
    {
        var payload = new byte[length]; if (length != 0) payload[0] = 4;
        AssertDid8Unknown(Did3Edid(0x12, 0, payload));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public void DisplayIdIIIBadExtentChecksumAndContinuationNeverPublishEarlierModes(int variant)
    {
        var bytes = Base(2); Dtd().CopyTo(bytes, 54);
        Did8Section(bytes, 1, 0x12, [5, 0, 3, 4, 239, 59], 1, false);
        Did8Section(bytes, 2, 0x12, [5, 1, 3, 0, 0, 0], 0, true); Checked(bytes);
        switch (variant)
        {
            case 0: bytes[135] = 4; DisplayIdChecksum(bytes, 1); Checked(bytes); break; // Extent over payload.
            case 1: bytes[139] ^= 1; Checked(bytes); break; // Inner checksum, outer repaired.
            case 2: bytes[255] ^= 1; break;
            case 3: bytes[260] = 1; DisplayIdChecksum(bytes, 2); Checked(bytes); break; // Continuation count.
            case 4: bytes[259] = 2; DisplayIdChecksum(bytes, 2); Checked(bytes); break; // Continuation product.
            case 5: bytes[257] = 0x13; DisplayIdChecksum(bytes, 2); Checked(bytes); break;
            default: Array.Resize(ref bytes, 256); break;
        }
        AssertDid8Unknown(bytes);
    }

    [Theory]
    [InlineData(0x20)]
    public void DisplayIdIIILegacyTagDoesNotExtendTwoXContainers(byte version)
        => AssertDid8Unknown(Did3Edid(version, 0, [4, 239, 59]));

    [Fact]
    public void DisplayIdIIIDoesNotSatisfyMissingCtaDtdOrVtdbSelectionTargets()
    {
        foreach (var svr in new byte[] { 129, 145 })
        {
            var bytes = Base(2); SetCta(bytes, 1, [0xE2, 0x0D, svr]);
            Did8Section(bytes, 2, 0x12, [5, 0, 3, 0x84, 0xEF, 0x3B], 0, false);
            AssertDid8Unknown(Checked(bytes));
        }
    }

    [Fact]
    public void DisplayIdIIIJsonRetainsAspectByValueAndOriginalConstructorsAndDeconstructors()
    {
        var capability = Capability(Did3Edid(0x12, 0, [4, 239, 59, 8, 0, 0]));
        var restored = JsonSerializer.Deserialize<EdidModeCapability>(JsonSerializer.Serialize(capability))!;
        for (var i = 0; i < 2; i++)
        {
            var expected = capability.Modes[i]; var actual = restored.Modes[i];
            Assert.Equal(expected.AspectRatioDeclared, actual.AspectRatioDeclared);
            Assert.Equal(expected.RawDescriptor.ToArray(), actual.RawDescriptor.ToArray());
            Assert.Equal((expected.Width, expected.Height, expected.Interlaced, expected.NominalVerticalRateHz),
                (actual.Width, actual.Height, actual.Interlaced, actual.NominalVerticalRateHz));
        }
        var legacy = new EdidModeDeclaration(1, 8, "displayid-iii", 0, [4, 239, 59]);
        var (block, offset, encoding, revision, raw) = legacy;
        Assert.Equal((1u, 8u, "displayid-iii", (byte)0), (block, offset, encoding, revision));
        Assert.Equal(3, raw.Length); Assert.Null(legacy.AspectRatioDeclared);
        var parent = new EdidModeCapability([legacy], []); var (modes, ranges) = parent;
        Assert.Single(modes); Assert.Empty(ranges);
    }

    private static byte[] Did3Edid(byte version, byte flags, byte[] payload)
    {
        var bytes = Base(1);
        Did8Section(bytes, 1, version, [5, flags, checked((byte)payload.Length), .. payload], 0, false);
        return Checked(bytes);
    }

    private static void AssertDid3(EdidModeDeclaration mode, byte version, byte header, uint offset,
        uint width, uint nominal, bool interlaced, bool preferred, bool reduced, EdidAspectRatioDeclaration aspect, byte[] raw)
    {
        Assert.Equal((1u, offset, "displayid-iii", header), (mode.BlockIndex, mode.ByteOffset, mode.Encoding, mode.Revision));
        Assert.Equal(header, mode.ContainerFlags); Assert.Equal(version, mode.ContainerRevision);
        Assert.Equal(width, mode.Width); Assert.Equal(nominal, mode.NominalVerticalRateHz);
        Assert.Equal(interlaced, mode.Interlaced); Assert.Equal(preferred, mode.Preferred); Assert.Equal(reduced, mode.ReducedBlanking);
        Assert.Equal("cvt", mode.Formula); Assert.Equal(aspect, mode.AspectRatioDeclared); Assert.Equal(raw, mode.RawDescriptor.ToArray());
        Assert.Null(mode.Height); Assert.Null(mode.Timing); Assert.Null(mode.Code); Assert.Null(mode.Native); Assert.Null(mode.BitIndex);
        Assert.Null(mode.DscPassThrough); Assert.Null(mode.CvtParameters);
        Assert.Null(mode.YCbCr420Encoding); Assert.Null(mode.YCbCr420Supported); Assert.Null(mode.YCbCr420Only); Assert.Null(mode.YCbCr420CapabilityMap);
    }
}
