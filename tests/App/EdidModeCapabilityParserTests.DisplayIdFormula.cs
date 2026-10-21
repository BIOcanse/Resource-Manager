using System.Security.Cryptography;
using System.Text.Json;
using ResourceManager.TestFixtures;
using ResourceManager.ReadWrite.Windows.Devices.Display;

namespace Resource_Manager_APP.Tests;

public sealed partial class EdidModeCapabilityParserTests
{
    [Theory]
     [InlineData(0x20)]
    public void DisplayIdTypeIxIndependentLiteralRetainsAllThreeFormulasAndFullUnsignedExtent(byte version)
    {
        var bytes = DisplayIdFormulaFixture.Bytes(version);
        Assert.Equal(version == 0x20 ? DisplayIdFormulaFixture.Sha256Version20 : DisplayIdFormulaFixture.Sha256Version21,
            Convert.ToHexString(SHA256.HashData(bytes)));
        var capability = Capability(bytes);
        DisplayIdFormulaExpectation.Verify(JsonSerializer.SerializeToElement(capability, new JsonSerializerOptions(JsonSerializerDefaults.Web)), version);
        bytes[136] = 255;
        Assert.Equal(Convert.FromHexString("007F0737043B"), capability.Modes[0].RawDescriptor.ToArray());
    }

    [Theory]
    [InlineData(1u, 1u, 1u)] [InlineData(65536u, 65536u, 256u)]
    [InlineData(1u, 65536u, 256u)] [InlineData(65536u, 1u, 1u)]
    public void DisplayIdTypeIxDoesNotWrapPlusOneDimensionsAtUshortMaximum(uint width, uint height, uint rate)
    {
        var mode = Assert.Single(Capability(TypeIxEdid(0x20, 0, TypeXBody(flags: 2, width: width, height: height, rate: rate))).Modes);
        Assert.Equal(width, mode.Width); Assert.Equal(height, mode.Height); Assert.Equal(rate, mode.NominalVerticalRateHz);
        Assert.Null(mode.Timing); Assert.False(mode.Interlaced);
    }

    [Fact]
    public void DisplayIdTypeIxEveryOptionsByteHasAnExplicitSupportedOrWholeUnknownOutcome()
    {
        byte[] supported = [0x00, 0x01, 0x02, 0x10, 0x11, 0x12, 0x20, 0x21, 0x22,
            0x30, 0x31, 0x32, 0x40, 0x41, 0x42, 0x50, 0x51, 0x52];
        for (var value = 0; value <= byte.MaxValue; value++)
        {
            var bytes = TypeIxEdid(0x20, 0, TypeXBody(flags: (byte)value));
            if (!supported.Contains((byte)value)) { AssertTypeXUnknown(bytes); continue; }
            var mode = Assert.Single(Capability(bytes).Modes); var parameters = mode.CvtParameters!;
            Assert.Equal((byte)(value % 16), parameters.ReducedBlankingVersion);
            Assert.Equal((byte)(value / 32), parameters.StereoCode);
            Assert.Equal((value / 16) % 2 == 1, parameters.AlternativeRefresh1000Over1001Supported);
            Assert.Equal(value % 16 != 0, mode.ReducedBlanking);
            Assert.Null(mode.YCbCr420Encoding); Assert.Null(mode.Timing);
        }
    }

    [Fact]
    public void DisplayIdTypeIxRejectsEveryUnverifiedHeaderWithoutTruncatingItsPayload()
    {
        for (var header = 1; header <= byte.MaxValue; header++)
            AssertTypeXUnknown(TypeIxEdid(0x20, (byte)header, TypeXBody(flags: 1)));
    }

    [Theory]
     [InlineData(0x12)] [InlineData(0x22)]
    public void DisplayIdTypeIxDoesNotBorrowLegacyTagsOrFutureContainerSemantics(byte version)
        => AssertTypeXUnknown(TypeIxEdid(version, 0, TypeXBody(flags: 1)));

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(5)] [InlineData(7)] [InlineData(11)]
    public void DisplayIdTypeIxRequiresACompleteNonemptySixByteList(int length)
        => AssertTypeXUnknown(TypeIxEdid(0x20, 0, new byte[length]));

    [Fact]
    public void DisplayIdTypeIxBadFinalDescriptorRejectsTheGoodPrefixAndKeepsExactRaw()
    {
        var bytes = DisplayIdFormulaFixture.Bytes(); bytes[148] = 0x03;
        DisplayIdFormulaFixture.Repair(bytes); AssertTypeXUnknown(bytes);
    }

    [Fact]
    public void DisplayIdTypeIxNaturalMaximumListAndRepeatedDeclarationsPreserveEveryPhysicalOffset()
    {
        var body = Convert.FromHexString("127F0737043B");
        var modes = Capability(TypeIxEdid(0x20, 0, Enumerable.Range(0, 19).Select(_ => body).ToArray())).Modes;
        Assert.Equal(19, modes.Length);
        Assert.Equal(Enumerable.Range(0, 19).Select(i => (uint)(8 + 6 * i)), modes.Select(mode => mode.ByteOffset));
        Assert.All(modes, mode => { Assert.Equal(body, mode.RawDescriptor.ToArray()); Assert.Null(mode.Timing); });
    }

    [Fact]
    public void DisplayIdTypeIxMultipleBlocksStayInOriginalOrder()
    {
        var bytes = Base(1);
        Did8Section(bytes, 1, 0x20, [.. TypeIxData(0, Convert.FromHexString("017F0737043B")),
            .. TypeIxData(0, Convert.FromHexString("52FFFFFFFFFF"))], 0, false);
        var modes = Capability(Checked(bytes)).Modes;
        Assert.Equal(new uint[] { 8, 17 }, modes.Select(mode => mode.ByteOffset));
        Assert.Equal(new uint?[] { 1920, 65536 }, modes.Select(mode => mode.Width));
        Assert.Equal(new byte[] { 1, 2 }, modes.Select(mode => mode.CvtParameters!.ReducedBlankingVersion));
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)]
    public void DisplayIdTypeIxStillRequiresTheWholeOuterAndInnerChain(int variant)
    {
        var bytes = DisplayIdFormulaFixture.Bytes();
        if (variant == 0) bytes[255] ^= 1;
        if (variant == 1) { bytes[154] ^= 1; Checked(bytes); }
        if (variant == 2) { bytes[200] = 1; Checked(bytes); }
        if (variant == 3) { bytes[132] = 1; DisplayIdFormulaFixture.Repair(bytes); }
        if (variant == 4) Array.Resize(ref bytes, 255);
        if (variant == 5) Array.Resize(ref bytes, 384);
        AssertTypeXUnknown(bytes);
    }

    [Fact]
    public void DisplayIdTypeIxDoesNotUseTypeXFlagApplicabilityOrCreateItsColourDeclaration()
    {
        var body = Convert.FromHexString("117F0737043B");
        var mode = Assert.Single(Capability(TypeIxEdid(0x20, 0, body)).Modes);
        Assert.Equal((byte)1, mode.CvtParameters!.ReducedBlankingVersion);
        Assert.True(mode.CvtParameters.AlternativeRefresh1000Over1001Supported);
        Assert.Null(mode.YCbCr420Encoding);
        AssertTypeXUnknown(TypeXEdid(0x20, 0, body));
        body[0] = 0x9B;
        Assert.NotNull(Capability(TypeXEdid(0x20, 0, body)).Modes[0].CvtParameters);
        AssertTypeXUnknown(TypeIxEdid(0x20, 0, body));
    }

    [Fact]
    public void DisplayIdTypeIxCannotSatisfyAMissingCtaT7OrT10Selection()
    {
        var bytes = Base(2); SetCta(bytes, 1, SelectionData(0x0D, [145]));
        Did8Section(bytes, 2, 0x20, TypeIxData(0, TypeXBody(flags: 1)), 0, false);
        AssertTypeXUnknown(Checked(bytes));
    }

    [Fact]
    public void DisplayIdTypeIxTypedRoundtripPreservesExplicitFalseAndNullParameters()
    {
        var capability = Capability(DisplayIdFormulaFixture.Bytes());
        var serialized = JsonSerializer.Serialize(capability);
        var restored = JsonSerializer.Deserialize<EdidModeCapability>(serialized)!;
        Assert.Equal(serialized, JsonSerializer.Serialize(restored));
        Assert.False(restored.Modes[0].CvtParameters!.AlternativeRefresh1000Over1001Supported);
        Assert.Null(restored.Modes[1].CvtParameters!.EarlyVsyncRequired);
    }

    private static byte[] TypeIxData(byte header, params byte[][] bodies)
    {
        var data = new List<byte> { 0x24, header, 0 };
        foreach (var body in bodies) data.AddRange(body);
        data[2] = checked((byte)(data.Count - 3)); return data.ToArray();
    }

    private static byte[] TypeIxEdid(byte version, byte header, params byte[][] bodies)
    {
        var bytes = Base(1); Did8Section(bytes, 1, version, TypeIxData(header, bodies), 0, false);
        return Checked(bytes);
    }
}
