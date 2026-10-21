using System.Security.Cryptography;
using System.Text.Json;
using ResourceManager.ReadWrite.Windows.Devices.Display;
using ResourceManager.TestFixtures;

namespace Resource_Manager_APP.Tests;

public sealed partial class EdidModeCapabilityParserTests
{
    [Theory]
     [InlineData(0x20)]
    public void DisplayIdEmbeddedColourLiteralKeepsRealContainerAndIndependentExpectedBits(byte version)
    {
        var bytes = DisplayIdCtaColourFixture.Bytes(version);
        Assert.Equal(version == 0x20 ? DisplayIdCtaColourFixture.Sha256Version20 : DisplayIdCtaColourFixture.Sha256Version21,
            Convert.ToHexString(SHA256.HashData(bytes)));
        var read = WindowsMonitorEdidReader.CreateReadResult(bytes);
        var capability = Assert.IsType<EdidModeCapability>(read.Capabilities!.ModeCapability);
        Assert.True(read.Complete); Assert.Null(capability.CtaFlags);
        Assert.Equal(5, capability.Modes.Length); Assert.Empty(capability.Ranges); Assert.Empty(capability.SelectionDeclarations);
        var prior = Assert.Single(Capability(TypeXSection(version, TypeXData(0, Convert.FromHexString("127F0737043B")))).Modes);
        Assert.Equal(JsonSerializer.Serialize(prior), JsonSerializer.Serialize(capability.Modes[0]));
        var modes = capability.Modes.Skip(1).ToArray();
        Assert.Equal(new uint?[] { 16, 65, 193, 97 }, modes.Select(mode => mode.Code));
        Assert.Equal(new uint[] { 18, 19, 20, 23 }, modes.Select(mode => mode.ByteOffset));
        Assert.Equal(new bool?[] { true, false, false, null }, modes.Select(mode => mode.Native));
        Assert.Equal(new bool?[] { true, false, true, true }, modes.Select(mode => mode.YCbCr420Supported));
        Assert.Equal(new bool?[] { false, false, false, true }, modes.Select(mode => mode.YCbCr420Only));
        Assert.All(modes, mode =>
        {
            Assert.Equal(1u, mode.BlockIndex); Assert.Equal((byte)0, mode.Revision);
            Assert.Equal((byte?)0, mode.ContainerFlags); Assert.Equal((byte?)version, mode.ContainerRevision);
            Assert.Null(mode.Timing); Assert.Null(mode.Preferred); Assert.Null(mode.DscPassThrough);
        });
        for (var index = 0; index < 3; index++)
        {
            var mode = modes[index]; Assert.Equal("displayid-cta-svd", mode.Encoding);
            var map = Assert.IsType<EdidModeReference>(mode.YCbCr420CapabilityMap);
            Assert.Equal((1u, 24u, (int?)(16 + index)), (map.BlockIndex, map.ByteOffset, map.BitIndex));
            Assert.Equal(new byte[] { 0xE2, 0x0F, 5 }, map.RawDescriptor.ToArray());
        }
        var only = modes[3]; Assert.Equal("displayid-cta-y420-vic", only.Encoding);
        Assert.Equal((3840u, 2160u, 60u), (only.Width!.Value, only.Height!.Value, only.NominalVerticalRateHz!.Value));
        Assert.Equal(new byte[] { 97 }, only.RawDescriptor.ToArray()); Assert.Null(only.YCbCr420CapabilityMap);
        Assert.Equal(bytes, Convert.FromBase64String(read.RawEdidDeclaredBase64!));
        bytes[154] = 0; bytes[151] = 0;
        Assert.Equal(new byte[] { 0xE2, 0x0F, 5 }, modes[0].YCbCr420CapabilityMap!.RawDescriptor.ToArray());
        Assert.Equal(new byte[] { 97 }, only.RawDescriptor.ToArray());
    }

    [Theory]
     [InlineData(0x20)]
    public void DisplayIdEmbeddedColourOriginalValidOnly420AndCmdbNegativesBecomeKnown(byte version)
    {
        var capability = Capability(DidCtaMetadataWithTypeX(version, [0x23, 9, 7, 7, 0xE2, 0x0E, 16]));
        var only = Assert.Single(capability.Modes.Where(mode => mode.Encoding == "displayid-cta-y420-vic"));
        Assert.Equal(2, capability.Modes.Length); Assert.Equal(16u, only.Code);
        Assert.True(only.YCbCr420Only); Assert.True(only.YCbCr420Supported); Assert.Null(only.Native);
        Assert.Null(only.YCbCr420CapabilityMap); Assert.Null(capability.CtaFlags);
        var mapped = Capability(DidCtaMetadataWithTypeX(version, [0x41, 16, 0xE2, 0x0F, 1]));
        var regular = Assert.Single(mapped.Modes, mode => mode.Encoding == "displayid-cta-svd");
        Assert.Equal(2, mapped.Modes.Length); Assert.Null(mapped.CtaFlags);
        Assert.Equal(16u, regular.Code); Assert.False(regular.Native); Assert.True(regular.YCbCr420Supported);
        Assert.Equal((int?)16, regular.YCbCr420CapabilityMap!.BitIndex);
        Assert.Equal(new byte[] { 0xE2, 0x0F, 1 }, regular.YCbCr420CapabilityMap.RawDescriptor.ToArray());
    }

    [Theory]
    [InlineData(0x20, false)] [InlineData(0x20, true)]
    public void DisplayIdEmbeddedColourMapUsesCombinedRegularDomainInEitherPhysicalOrder(byte version, bool embeddedFirst)
    {
        var modes = Capability(EmbeddedColourMixedChain(version, embeddedFirst,
            [0x41, 0x90, 0xE2, 0x0E, 97, 0xE2, 0x0F, 5], [0x42, 31, 65])).Modes;
        Assert.Equal(embeddedFirst ? new uint?[] { 16, 97, 31, 65 } : new uint?[] { 31, 65, 16, 97 }, modes.Select(mode => mode.Code));
        Assert.True(modes.Single(mode => mode.Code == 16).YCbCr420Supported);
        Assert.True(modes.Single(mode => mode.Code == 31).YCbCr420Supported);
        Assert.False(modes.Single(mode => mode.Code == 65).YCbCr420Supported);
        var map = modes.Single(mode => mode.Code == 16).YCbCr420CapabilityMap!;
        Assert.Equal((embeddedFirst ? 1u : 2u, 13u, (int?)18), (map.BlockIndex, map.ByteOffset, map.BitIndex));
        Assert.Null(modes.Single(mode => mode.Code == 97).YCbCr420CapabilityMap);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(4)]
    public void DisplayIdEmbeddedColourMapRetainsEveryActualOrdinalAndFalse(byte bitmap)
    {
        var modes = Capability(EmbeddedColourMixedChain(0x20, true,
            [0x41, 16, 0xE2, 0x0E, 97, 0xE2, 0x0F, bitmap], [0x42, 31, 65])).Modes;
        Assert.Equal((bitmap & 1) != 0, modes.Single(mode => mode.Code == 31).YCbCr420Supported);
        Assert.Equal((bitmap & 2) != 0, modes.Single(mode => mode.Code == 65).YCbCr420Supported);
        Assert.Equal((bitmap & 4) != 0, modes.Single(mode => mode.Code == 16).YCbCr420Supported);
        Assert.True(modes.Single(mode => mode.Code == 97).YCbCr420Supported);
    }

    [Theory]
     [InlineData(0x20)]
    public void DisplayIdEmbeddedColourMapWithoutOrdinaryCtaDoesNotInventDtdNativeCount(byte version)
    {
        var bytes = TypeXSection(version, DidCtaMetadataData([0x41, 0x90, 0xE2, 0x0F, 1]));
        Dtd().CopyTo(bytes, 54); Checksums(bytes);
        var capability = Capability(bytes); Assert.Null(capability.CtaFlags);
        Assert.Null(capability.Modes.Single(mode => mode.Encoding == "edid-detailed").Native);
        var regular = capability.Modes.Single(mode => mode.Encoding == "displayid-cta-svd");
        Assert.True(regular.Native); Assert.True(regular.YCbCr420Supported);
    }

    [Fact]
    public void DisplayIdEmbeddedColourEmptyMapCoversAllRegularOccurrencesOnly()
    {
        var modes = Capability(DidCtaMetadataWithTypeX(0x20, [0x43, 0x90, 16, 65, 0xE2, 0x0E, 97, 0xE1, 0x0F])).Modes;
        var regular = modes.Where(mode => mode.Encoding == "displayid-cta-svd").ToArray();
        Assert.Equal(3, regular.Length);
        Assert.All(regular, mode => { Assert.True(mode.YCbCr420Supported); Assert.Null(mode.YCbCr420CapabilityMap!.BitIndex); });
        var only = Assert.Single(modes, mode => mode.YCbCr420Only == true);
        Assert.True(only.YCbCr420Supported); Assert.Null(only.YCbCr420CapabilityMap);
    }

    [Theory]
     [InlineData(0x20)]
    public void DisplayIdEmbeddedColourContinuationSharesOneReferenceDomain(byte version)
    {
        var bytes = Base(3);
        Did8Section(bytes, 1, version, DidCtaMetadataData([0x41, 16]), 1, false);
        Did8Section(bytes, 2, version, DidCtaMetadataData([0xE2, 0x0E, 97, 0xE2, 0x0F, 2]), 0, true);
        SetCta(bytes, 3, [0x41, 31]);
        var modes = Capability(Checked(bytes)).Modes;
        Assert.Equal(new uint?[] { 16, 97, 31 }, modes.Select(mode => mode.Code));
        Assert.True(modes.Single(mode => mode.Code == 16).YCbCr420Supported);
        Assert.False(modes.Single(mode => mode.Code == 31).YCbCr420Supported);
        Assert.Null(modes.Single(mode => mode.Code == 97).YCbCr420CapabilityMap);
    }

    [Theory]
    [InlineData(false, false)] [InlineData(false, true)] [InlineData(true, false)] [InlineData(true, true)]
    public void DisplayIdEmbeddedColourRegularOnlyConflictAcrossContainersRefusesWholeValue(bool embeddedFirst, bool innerRegular)
        => AssertTypeXUnknown(EmbeddedColourMixedChain(0x20, embeddedFirst,
            innerRegular ? [0x41, 16] : [0xE2, 0x0E, 16], innerRegular ? [0xE2, 0x0E, 16] : [0x41, 16]));

    [Theory]
     [InlineData(0x20)]
    public void DisplayIdEmbeddedColourSameWrapperRegularOnlyConflictRefusesWholeValue(byte version)
        => AssertTypeXUnknown(DidCtaMetadataWithTypeX(version, [0x41, 16, 0xE2, 0x0E, 16]));

    [Theory]
    [InlineData(2)] [InlineData(128)]
    public void DisplayIdEmbeddedColourOnly420NeverConsumesAMapOrdinal(byte bitmap)
        => AssertTypeXUnknown(DidCtaMetadataWithTypeX(0x20, [0x41, 16, 0xE2, 0x0E, 97, 0xE2, 0x0F, bitmap]));

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void DisplayIdEmbeddedColourDuplicateMapsAcrossWrappersAndOrdinaryCtaRemainUnknown(bool ordinary)
    {
        if (ordinary)
            AssertTypeXUnknown(EmbeddedColourMixedChain(0x20, true, [0x41, 16, 0xE1, 0x0F], [0xE1, 0x0F]));
        else
            AssertTypeXUnknown(DidCtaMetadataWithTypeX(0x20, [0x41, 16, 0xE1, 0x0F, 0xE1, 0x0F]));
    }

    [Theory]
    [InlineData(0)] [InlineData(128)] [InlineData(129)] [InlineData(192)] [InlineData(220)] [InlineData(255)]
    public void DisplayIdEmbeddedColourUnverifiedOnly420CodesDoNotPublishGoodPrefix(byte code)
        => AssertTypeXUnknown(DidCtaMetadataWithTypeX(0x20, [0x41, 16, 0xE3, 0x0E, 97, code]));

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public void DisplayIdEmbeddedColourEmptyAndTruncatedBlocksRefuseWholeValue(int variant)
    {
        byte[] data = variant switch { 0 => [0xE1, 0x0E], 1 => [0xE2, 0x0F], _ => [0xE3, 0x0E, 97] };
        AssertTypeXUnknown(DidCtaMetadataWithTypeX(0x20, [0x41, 16, .. data]));
    }

    private static byte[] EmbeddedColourMixedChain(byte version, bool embeddedFirst, byte[] embedded, byte[] ordinary)
    {
        var bytes = Base(2);
        Did8Section(bytes, embeddedFirst ? 1 : 2, version, DidCtaMetadataData(embedded), 0, false);
        SetCta(bytes, embeddedFirst ? 2 : 1, ordinary);
        return Checked(bytes);
    }
}
