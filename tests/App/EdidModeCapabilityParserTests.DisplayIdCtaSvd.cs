using System.Security.Cryptography;
using System.Text.Json;
using ResourceManager.ReadWrite.Windows.Devices.Display;
using ResourceManager.TestFixtures;

namespace Resource_Manager_APP.Tests;

public sealed partial class EdidModeCapabilityParserTests
{
    [Theory]
     [InlineData(0x20)]
    public void DisplayIdEmbeddedSvdLiteralKeepsRealHeadersCodesAndPriorTypeX(byte version)
    {
        var bytes = DisplayIdCtaSvdFixture.Bytes(version);
        if (version == 0x20) Assert.Equal(DisplayIdCtaSvdFixture.Sha256, Convert.ToHexString(SHA256.HashData(bytes)));
        var read = WindowsMonitorEdidReader.CreateReadResult(bytes);
        var capability = Assert.IsType<EdidModeCapability>(read.Capabilities!.ModeCapability);
        Assert.True(read.Complete); Assert.Equal(4, capability.Modes.Length);
        var previous = Assert.Single(Capability(TypeXSection(version, TypeXData(0, Convert.FromHexString("127F0737043B")))).Modes);
        Assert.Equal(JsonSerializer.Serialize(previous), JsonSerializer.Serialize(capability.Modes[0]));
        var modes = capability.Modes.Skip(1).ToArray();
        Assert.Equal(new uint?[] { 16, 65, 193 }, modes.Select(mode => mode.Code));
        Assert.Equal(new uint[] { 18, 19, 20 }, modes.Select(mode => mode.ByteOffset));
        Assert.Equal(new bool?[] { true, false, false }, modes.Select(mode => mode.Native));
        Assert.Equal(new[] { (1920u, 1080u, 60u), (1280u, 720u, 24u), (5120u, 2160u, 120u) },
            modes.Select(mode => (mode.Width!.Value, mode.Height!.Value, mode.NominalVerticalRateHz!.Value)));
        for (var i = 0; i < modes.Length; i++)
        {
            var mode = modes[i];
            Assert.Equal((1u, "displayid-cta-svd", (byte)0), (mode.BlockIndex, mode.Encoding, mode.Revision));
            Assert.Equal((byte?)0, mode.ContainerFlags); Assert.Equal((byte?)version, mode.ContainerRevision);
            Assert.Equal(new byte[] { bytes[146 + i] }, mode.RawDescriptor.ToArray());
            Assert.False(mode.Interlaced); Assert.False(mode.YCbCr420Only);
            Assert.Null(mode.Timing); Assert.Null(mode.Preferred); Assert.Null(mode.DscPassThrough);
            Assert.Null(mode.YCbCr420Supported); Assert.Null(mode.YCbCr420CapabilityMap);
        }
        Assert.Null(capability.CtaFlags); Assert.Empty(capability.SelectionDeclarations); Assert.Empty(capability.Ranges);
        Assert.Equal(bytes, Convert.FromBase64String(read.RawEdidDeclaredBase64!));
        bytes[146] = 0;
        Assert.Equal(new byte[] { 0x90 }, modes[0].RawDescriptor.ToArray()); Assert.Equal(16u, modes[0].Code);
    }

    [Theory]
    [InlineData(0x20, false)] [InlineData(0x20, true)]
    public void DisplayIdEmbeddedSvdMapUsesOrdinaryThenEmbeddedOrderWithoutMovingOutput(byte version, bool embeddedFirst)
    {
        var bytes = EmbeddedSvdMixedChain(version, embeddedFirst, [0x42, 31, 65, 0xE2, 0x0F, 4]);
        var capability = Capability(bytes); var modes = capability.Modes.ToArray();
        Assert.Equal(embeddedFirst ? new uint?[] { 16, 31, 65 } : new uint?[] { 31, 65, 16 }, modes.Select(mode => mode.Code));
        var embedded = Assert.Single(modes.Where(mode => mode.Encoding == "displayid-cta-svd"));
        Assert.Equal((embeddedFirst ? 1u : 2u, 9u), (embedded.BlockIndex, embedded.ByteOffset));
        Assert.True(embedded.YCbCr420Supported); Assert.True(embedded.Native);
        var map = Assert.IsType<EdidModeReference>(embedded.YCbCr420CapabilityMap);
        Assert.Equal((embeddedFirst ? 2u : 1u, 7u, (int?)18), (map.BlockIndex, map.ByteOffset, map.BitIndex));
        Assert.Equal(new byte[] { 0xE2, 0x0F, 4 }, map.RawDescriptor.ToArray());
        Assert.All(modes.Where(mode => mode.Encoding == "cta-vic"), mode => Assert.False(mode.YCbCr420Supported));
        Assert.Equal((byte?)0, capability.CtaFlags);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(4)]
    public void DisplayIdEmbeddedSvdMapTargetsEveryActualOrdinal(byte bitmap)
    {
        var modes = Capability(EmbeddedSvdMixedChain(0x20, true, [0x42, 31, 65, 0xE2, 0x0F, bitmap])).Modes;
        Assert.Equal((bitmap & 4) != 0, modes.Single(mode => mode.Code == 16).YCbCr420Supported);
        Assert.Equal((bitmap & 1) != 0, modes.Single(mode => mode.Code == 31).YCbCr420Supported);
        Assert.Equal((bitmap & 2) != 0, modes.Single(mode => mode.Code == 65).YCbCr420Supported);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void DisplayIdEmbeddedSvdMapRejectsBitsPastTheCombinedDomain(bool embeddedFirst)
        => AssertTypeXUnknown(EmbeddedSvdMixedChain(0x20, embeddedFirst, [0x42, 31, 65, 0xE2, 0x0F, 8]));

    [Fact]
    public void DisplayIdEmbeddedSvdEmptyMapIncludesEveryEmbeddedOccurrence()
    {
        var bytes = Base(2);
        Did8Section(bytes, 1, 0x20, DidCtaMetadataData([0x43, 0x90, 0x10, 65]), 0, false);
        SetCta(bytes, 2, [0xE1, 0x0F]);
        var modes = Capability(Checked(bytes)).Modes;
        Assert.Equal(new uint?[] { 16, 16, 65 }, modes.Select(mode => mode.Code));
        Assert.Equal(new bool?[] { true, false, false }, modes.Select(mode => mode.Native));
        Assert.All(modes, mode =>
        {
            Assert.True(mode.YCbCr420Supported); Assert.Null(mode.YCbCr420CapabilityMap!.BitIndex);
            Assert.Equal((2u, 4u), (mode.YCbCr420CapabilityMap.BlockIndex, mode.YCbCr420CapabilityMap.ByteOffset));
        });
    }

    [Theory]
     [InlineData(0x20)]
    public void DisplayIdEmbeddedSvdContinuationOrderSharesTheOriginalCtaMap(byte version)
    {
        var bytes = Base(3);
        Did8Section(bytes, 1, version, DidCtaMetadataData([0x41, 16]), 1, false);
        Did8Section(bytes, 2, version, DidCtaMetadataData([0x41, 65]), 0, true);
        SetCta(bytes, 3, [0x41, 31, 0xE2, 0x0F, 4]);
        var modes = Capability(Checked(bytes)).Modes;
        Assert.Equal(new uint?[] { 16, 65, 31 }, modes.Select(mode => mode.Code));
        Assert.Equal(new bool?[] { false, true, false }, modes.Select(mode => mode.YCbCr420Supported));
    }

    [Fact]
    public void DisplayIdEmbeddedSvdBetweenOrdinaryCtaBlocksDoesNotSplitTheirReferenceDomain()
    {
        var bytes = Base(3); SetCta(bytes, 1, [0x41, 31]);
        Did8Section(bytes, 2, 0x20, DidCtaMetadataData([0x41, 16]), 0, false);
        SetCta(bytes, 3, [0x41, 65, 0xE2, 0x0F, 4]);
        var modes = Capability(Checked(bytes)).Modes;
        Assert.Equal(new uint?[] { 31, 16, 65 }, modes.Select(mode => mode.Code));
        Assert.Equal(new bool?[] { false, true, false }, modes.Select(mode => mode.YCbCr420Supported));
    }

    [Theory]
    [InlineData(0x20, false)] [InlineData(0x20, true)]
    public void DisplayIdEmbeddedSvdConflictsWithOrdinary420OnlyAcrossBothPhysicalOrders(byte version, bool embeddedFirst)
        => AssertTypeXUnknown(EmbeddedSvdMixedChain(version, embeddedFirst, [0xE2, 0x0E, 16]));

    [Fact]
    public void DisplayIdEmbeddedSvdDoesNotInventCtaFlagsOrBaseDtdNativeCount()
    {
        var bytes = TypeXSection(0x20, DidCtaMetadataData([0x41, 0x90]));
        Dtd().CopyTo(bytes, 54); Checksums(bytes);
        var capability = Capability(bytes); Assert.Null(capability.CtaFlags);
        Assert.Null(capability.Modes.Single(mode => mode.Encoding == "edid-detailed").Native);
        Assert.True(capability.Modes.Single(mode => mode.Encoding == "displayid-cta-svd").Native);
    }

    [Theory]
    [InlineData(0)] [InlineData(128)] [InlineData(255)]
    public void DisplayIdEmbeddedSvdBadCodeNeverPublishesTheGoodPrefix(byte code)
        => AssertTypeXUnknown(DidCtaMetadataWithTypeX(0x20, [0x42, 16, code]));

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    [InlineData(4)] [InlineData(5)] [InlineData(6)] [InlineData(7)]
    public void DisplayIdEmbeddedSvdDoesNotHideAnUnsupportedModeOrReferenceTail(int variant)
    {
        byte[] tail = variant switch
        {
            0 => [0xE2, 0x0E, 16], 1 => [0xE2, 0x0F, 2],
            2 => [0xE2, 0x0D, 16], 3 => [0xE2, 0x22, 0],
            4 => [0xE2, 0x23, 1], 5 => [0xE2, 0x2A, 0],
            6 => [0x63, 3, 0x0C, 0], _ => [0xC1, 0]
        };
        AssertTypeXUnknown(DidCtaMetadataWithTypeX(0x20, [0x41, 16, .. tail]));
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public void DisplayIdEmbeddedSvdTruncatedAndEmptyBlocksKeepWholeValueUnknown(int variant)
    {
        byte[] payload = variant switch { 0 => [0x40], 1 => [0x42, 16], _ => [0x41, 16, 0x42, 65] };
        AssertTypeXUnknown(DidCtaMetadataWithTypeX(0x20, payload));
    }

    [Fact]
    public void DisplayIdEmbeddedSvdUsesTheWholeNaturalSectionWithoutTruncatingTheLastBlock()
    {
        var payload = new List<byte>();
        for (var i = 0; i < 3; i++) payload.AddRange([0x5F, .. Enumerable.Repeat((byte)16, 31)]);
        payload.AddRange([0x55, .. Enumerable.Repeat((byte)65, 21)]);
        var bytes = TypeXSection(0x20, DidCtaMetadataData(payload.ToArray()));
        Assert.Equal(121, bytes[130]); var modes = Capability(bytes).Modes;
        Assert.Equal(114, modes.Length); Assert.Equal(125u, modes[^1].ByteOffset); Assert.Equal(65u, modes[^1].Code);
        bytes[232] = 0x56; DisplayIdChecksum(bytes, 1); AssertTypeXUnknown(Checked(bytes));
    }

    [Fact]
    public void DisplayIdEmbeddedSvdOriginalMetadataLiteralAndUnsupportedNegativeRemainDistinct()
    {
        var original = DisplayIdNonModeCtaFixture.Bytes();
        Assert.Equal(DisplayIdNonModeCtaFixture.Sha256, Convert.ToHexString(SHA256.HashData(original)));
        var oldModes = Capability(original).Modes;
        var svd = original.ToArray(); svd[166] = 0x43; DisplayIdChecksum(svd, 1);
        var modes = Capability(Checked(svd)).Modes;
        Assert.Equal(6, modes.Length);
        Assert.Equal(JsonSerializer.Serialize(oldModes), JsonSerializer.Serialize(modes.Take(3)));
        Assert.Equal(new uint?[] { 9, 7, 7 }, modes.Skip(3).Select(mode => mode.Code));
        var unsupported = DisplayIdNonModeCtaFixture.Bytes(modeBearingCta: true);
        Assert.Equal(0xC3, unsupported[166]); AssertTypeXUnknown(unsupported);
        AssertTypeXUnknown(DisplayIdCtaSvdFixture.Bytes(unsupportedCta: true));
    }

    private static byte[] EmbeddedSvdMixedChain(byte version, bool embeddedFirst, byte[] ordinary)
    {
        var bytes = Base(2);
        Did8Section(bytes, embeddedFirst ? 1 : 2, version, DidCtaMetadataData([0x41, 0x90]), 0, false);
        SetCta(bytes, embeddedFirst ? 2 : 1, ordinary);
        return Checked(bytes);
    }
}
