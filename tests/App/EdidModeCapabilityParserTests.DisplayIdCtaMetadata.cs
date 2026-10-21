using System.Text.Json;
using ResourceManager.ReadWrite.Windows.Devices.Display;

namespace Resource_Manager_APP.Tests;

public sealed partial class EdidModeCapabilityParserTests
{
    [Fact]
    public void DisplayIdNonModeCtaIndependentLiteralKeepsOriginalTypeXWithoutFakeCtaHeader()
    {
        var bytes = Convert.FromBase64String("AP///////wAAAAAAAAAAAAAAAQS1AAAAAAAAAAAAAAAAAAAAAAABAQEBAQEBAQEBAQEBAQEBAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAATtwIBgCACoABhJ/BzcEO4EADCMJBwfjBgUB4wWAAGoAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAkA==");
        var read = WindowsMonitorEdidReader.CreateReadResult(bytes);
        Assert.True(read.Complete);
        var capability = Assert.IsType<EdidModeCapability>(read.Capabilities!.ModeCapability);
        var mode = Assert.Single(capability.Modes);
        Assert.Equal((1u, 8u, "displayid-x", (byte)0), (mode.BlockIndex, mode.ByteOffset, mode.Encoding, mode.Revision));
        Assert.Equal((1920u, 1080u, 60u), (mode.Width!.Value, mode.Height!.Value, mode.NominalVerticalRateHz!.Value));
        Assert.Equal((byte?)0x20, mode.ContainerRevision); Assert.Equal((byte?)0, mode.ContainerFlags);
        Assert.Equal(Convert.FromHexString("127F0737043B"), mode.RawDescriptor.ToArray());
        Assert.Equal((byte)2, mode.CvtParameters!.ReducedBlankingVersion);
        Assert.True(mode.CvtParameters.AlternativeRefresh1000Over1001Supported);
        Assert.False(mode.Interlaced); Assert.Null(mode.Timing); Assert.Null(mode.Native); Assert.Null(mode.Preferred);
        Assert.Null(mode.DscPassThrough); Assert.Null(mode.YCbCr420CapabilityMap);
        Assert.Null(capability.CtaFlags); Assert.Empty(capability.SelectionDeclarations); Assert.Empty(capability.Ranges);
        Assert.Equal(bytes, Convert.FromBase64String(read.RawEdidDeclaredBase64!));
        bytes[136] = 0;
        Assert.Equal(Convert.FromHexString("127F0737043B"), mode.RawDescriptor.ToArray());
    }

    [Theory]
     [InlineData(0x20)]
    public void DisplayIdNonModeCtaBothContainerVersionsPreserveExistingMode(byte version)
    {
        var metadata = Convert.FromHexString("23090707E3060501E3058000");
        var expected = Capability(TypeXSection(version, TypeXData(0, Convert.FromHexString("127F0737043B"))));
        var actual = Capability(DidCtaMetadataWithTypeX(version, metadata));
        Assert.Equal(JsonSerializer.Serialize(expected), JsonSerializer.Serialize(actual));
    }

    [Theory]
    [InlineData(1)] [InlineData(4)] [InlineData(5)]
    public void DisplayIdNonModeCtaUsesExistingOrdinaryTagClassification(byte tag)
    {
        byte[] metadata = tag switch
        {
            1 => [0x23, 0x09, 0x07, 0x07],
            4 => [0x83, 1, 0, 0],
            5 => [0xB1, .. new byte[17]],
            _ => throw new InvalidOperationException()
        };
        Assert.Empty(Capability(Cta(metadata)).Modes);
        Assert.Single(Capability(DidCtaMetadataWithTypeX(0x20, metadata)).Modes);
    }

    [Theory]
    [InlineData(0x00)] [InlineData(0x05)] [InlineData(0x06)] [InlineData(0x07)]
    [InlineData(0x11)] [InlineData(0x12)] [InlineData(0x13)] [InlineData(0x14)]
    [InlineData(0x20)] [InlineData(0x21)]
    public void DisplayIdNonModeCtaUsesExistingExtendedTagClassification(byte extendedTag)
    {
        byte[] metadata = [0xE2, extendedTag, 0];
        Assert.Empty(Capability(Cta(metadata)).Modes);
        var mode = Assert.Single(Capability(DidCtaMetadataWithTypeX(0x20, metadata)).Modes);
        Assert.Equal("displayid-x", mode.Encoding); Assert.Null(mode.Native);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    [InlineData(5)] [InlineData(6)] [InlineData(7)] [InlineData(8)]
    public void DisplayIdNonModeCtaNeverSkipsModeOrReferenceBlocksAfterGoodMetadata(int variant)
    {
        byte[] unsupported = variant switch
        {
            0 => [0xE2, 0x0E, 0], // Reserved VIC0 remains unknown; the original valid VIC16 now has a positive regression.
            1 => [0x63, 0x03, 0x0C, 0],
            2 => [0xE2, 0x0F, 1],
            3 => [0xE2, 0x0D, 16],
            4 => [0xE2, 0x08, 0],
            5 => [0xE2, 0x22, 0],
            6 => [0xE2, 0x23, 1],
            7 => [0xE2, 0x2A, 0],
            _ => [0xC1, 0]
        };
        var bytes = DidCtaMetadataWithTypeX(0x20, [0x23, 0x09, 0x07, 0x07, .. unsupported]);
        Dtd().CopyTo(bytes, 54); Checksums(bytes);
        AssertTypeXUnknown(bytes);
    }

    [Theory]
    [InlineData(1)] [InlineData(7)] [InlineData(8)] [InlineData(16)]
    [InlineData(32)] [InlineData(64)] [InlineData(128)] [InlineData(255)]
    public void DisplayIdNonModeCtaUnverifiedWrapperFlagsRemainWholeUnknown(byte flags)
        => AssertTypeXUnknown(DidCtaMetadataWithTypeX(0x20, [0x23, 0x09, 0x07, 0x07], flags));

    [Theory]
     [InlineData(0x12)]
    public void DisplayIdNonModeCtaLegacyContainersRemainOutsideThisSlice(byte version)
    {
        var bytes = Base(1); Dtd().CopyTo(bytes, 54);
        Did8Section(bytes, 1, version, DidCtaMetadataData([0x23, 0x09, 0x07, 0x07]), 0, false);
        AssertTypeXUnknown(Checked(bytes));
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    [InlineData(4)] [InlineData(5)] [InlineData(6)]
    public void DisplayIdNonModeCtaInnerExtentEmptyAndUnverifiedPaddingNeverLeakAPrefix(int variant)
    {
        byte[] metadata = variant switch
        {
            0 => [0x23, 0x09, 0x07],
            1 => [0xE0],
            2 => [0x23, 0x09, 0x07, 0x07, 0],
            3 => [],
            4 => [0xFF, 0x06],
            5 => [0x23, 0x09, 0x07, 0x07, 0x21],
            _ => [1, 0]
        };
        AssertTypeXUnknown(DidCtaMetadataWithTypeX(0x20, metadata));
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public void DisplayIdNonModeCtaStillRequiresOuterExtentAndBothChecksums(int variant)
    {
        var bytes = DidCtaMetadataWithTypeX(0x20, [0x23, 0x09, 0x07, 0x07]);
        if (variant == 0) { bytes[144]++; DisplayIdChecksum(bytes, 1); }
        else if (variant == 1) bytes[128 + 5 + bytes[130]] ^= 1;
        else bytes[255] ^= 1;
        if (variant != 2) Checksums(bytes);
        AssertTypeXUnknown(bytes);
    }

    [Fact]
    public void DisplayIdNonModeCtaNaturalMaximumCollectionIsNotTruncated()
    {
        var metadata = new List<byte>();
        for (var i = 0; i < 27; i++) metadata.AddRange([0x23, 0x09, 0x07, 0x07]);
        var bytes = DidCtaMetadataWithTypeX(0x20, metadata.ToArray());
        Assert.Equal(120, bytes[130]); Assert.Single(Capability(bytes).Modes);
        bytes[128 + 5 + 9 + 3 + metadata.Count - 1] = 0xFF;
        // Audio detail bytes do not become timing declarations or get independently validated here.
        DisplayIdChecksum(bytes, 1); Assert.Single(Capability(bytes).Modes);
        bytes[128 + 5 + 9 + 3 + metadata.Count - 4] = 0x24;
        DisplayIdChecksum(bytes, 1); AssertTypeXUnknown(Checked(bytes));
    }

    [Fact]
    public void DisplayIdNonModeCtaDoesNotAddModesOrAffectOrdinaryCtaReferenceOrdinals()
    {
        var bytes = Base(3);
        SetCta(bytes, 1, [0xE2, 0x0F, 0x02, 0x41, 16]);
        Did8Section(bytes, 2, 0x20, DidCtaMetadataData([0xE3, 0x06, 5, 1]), 0, false);
        SetCta(bytes, 3, [0x41, 4]);
        var capability = Capability(bytes); Assert.Equal(2, capability.Modes.Length);
        Assert.Equal(new uint?[] { 16, 4 }, capability.Modes.Select(mode => mode.Code));
        Assert.False(capability.Modes[0].YCbCr420Supported); Assert.True(capability.Modes[1].YCbCr420Supported);
        Assert.Equal(17, capability.Modes[1].YCbCr420CapabilityMap!.BitIndex);
        Assert.Equal((byte?)0, capability.CtaFlags); Assert.Empty(capability.SelectionDeclarations);
    }

    [Fact]
    public void DisplayIdNonModeCtaMultipleWrappersPreserveOnlyTheOriginalMode()
    {
        var bytes = TypeXSection(0x20, [.. TypeXData(0, Convert.FromHexString("127F0737043B")),
            .. DidCtaMetadataData([0x23, 9, 7, 7]), .. DidCtaMetadataData([0xE3, 6, 5, 1])]);
        var mode = Assert.Single(Capability(bytes).Modes); Assert.Equal(8u, mode.ByteOffset);
        Assert.Equal((byte?)0, mode.ContainerFlags); Assert.Equal((byte?)0x20, mode.ContainerRevision);
        Assert.Null(Capability(bytes).CtaFlags);
    }

    [Fact]
    public void DisplayIdNonModeCtaContinuationAndLateFailureKeepWholeChainSemantics()
    {
        var bytes = Base(2);
        Did8Section(bytes, 1, 0x20, DidCtaMetadataData([0x23, 9, 7, 7]), 1, false);
        Did8Section(bytes, 2, 0x20, TypeXData(0, Convert.FromHexString("127F0737043B")), 0, true);
        var mode = Assert.Single(Capability(bytes).Modes); Assert.Equal((2u, 8u), (mode.BlockIndex, mode.ByteOffset));
        bytes[256 + 4] = 1; DisplayIdChecksum(bytes, 2); AssertTypeXUnknown(Checked(bytes));
    }

    [Fact]
    public void DisplayIdNonModeCtaOnlyCollectionIsKnownEmptyWithoutSyntheticCtaFlags()
    {
        var bytes = TypeXSection(0x20, DidCtaMetadataData([0x23, 9, 7, 7, 0xE3, 6, 5, 1]));
        var capability = Capability(bytes); Assert.Empty(capability.Modes); Assert.Empty(capability.Ranges);
        Assert.Null(capability.CtaFlags); Assert.Empty(capability.SelectionDeclarations);
    }

    private static byte[] DidCtaMetadataData(byte[] metadata, byte flags = 0)
        => [0x81, flags, checked((byte)metadata.Length), .. metadata];

    private static byte[] DidCtaMetadataWithTypeX(byte version, byte[] metadata, byte flags = 0)
        => TypeXSection(version, [.. TypeXData(0, Convert.FromHexString("127F0737043B")), .. DidCtaMetadataData(metadata, flags)]);
}
