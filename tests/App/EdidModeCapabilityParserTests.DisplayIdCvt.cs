using System.Text.Json;
using ResourceManager.ReadWrite.Windows.Devices.Display;

namespace Resource_Manager_APP.Tests;

public sealed partial class EdidModeCapabilityParserTests
{
    [Fact]
    public void DisplayIdTypeXIndependentLiteralPreservesThreeOriginalParameterDeclarations()
    {
        var bytes = Convert.FromBase64String("AP///////wAAAAAAAAAAAAAAAQS1AAAAAAAAAAAAAAAAAAAAAAABAQEBAQEBAQEBAQEBAQEBAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAATtwIB4CACoABhJ/BzcEOyoQBwJ/BzcEKwEqIAibfwc3BDtYARAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAkA==");
        var modes = Capability(bytes).Modes;
        Assert.Equal(3, modes.Length);
        Assert.Equal(new uint[] { 8, 17, 27 }, modes.Select(mode => mode.ByteOffset));
        Assert.Equal(new byte?[] { 0, 16, 32 }, modes.Select(mode => mode.ContainerFlags));
        Assert.Equal(new uint?[] { 60, 300, 60 }, modes.Select(mode => mode.NominalVerticalRateHz));
        Assert.Equal(new byte[] { 2, 2, 3 }, modes.Select(mode => mode.CvtParameters!.ReducedBlankingVersion));
        Assert.Equal(Convert.FromHexString("127F0737043B"), modes[0].RawDescriptor.ToArray());
        Assert.Equal(Convert.FromHexString("027F0737042B01"), modes[1].RawDescriptor.ToArray());
        Assert.Equal(Convert.FromHexString("9B7F0737043B5801"), modes[2].RawDescriptor.ToArray());
        Assert.True(modes[0].CvtParameters!.AlternativeRefresh1000Over1001Supported);
        Assert.False(modes[1].CvtParameters!.AlternativeRefresh1000Over1001Supported);
        var third = modes[2].CvtParameters!;
        Assert.Null(third.AlternativeRefresh1000Over1001Supported);
        Assert.True(third.HorizontalBlankingBase160Pixels); Assert.True(third.EarlyVsyncRequired);
        Assert.Equal((byte)6, third.DeltaHorizontalBlankingCode); Assert.Equal((byte)2, third.AdditionalVerticalBlankingCode);
        Assert.True(third.AlternativeMinimumVerticalBlanking);
        Assert.True(modes[2].YCbCr420Encoding); Assert.True(modes[2].YCbCr420Supported);
        Assert.All(modes, mode => {
            Assert.Equal((1u, "displayid-x", (byte)0, (byte?)0x20), (mode.BlockIndex, mode.Encoding, mode.Revision, mode.ContainerRevision));
            Assert.Equal((1920u, 1080u), (mode.Width!.Value, mode.Height!.Value));
            Assert.False(mode.Interlaced); Assert.Equal("cvt", mode.Formula); Assert.True(mode.ReducedBlanking);
            Assert.Null(mode.Timing); Assert.Null(mode.Code); Assert.Null(mode.Native); Assert.Null(mode.Preferred);
            Assert.Null(mode.DscPassThrough); Assert.Null(mode.BitIndex); Assert.Null(mode.YCbCr420Only);
        });
        bytes[136] = 0;
        Assert.Equal(Convert.FromHexString("127F0737043B"), modes[0].RawDescriptor.ToArray());
    }

    [Theory]
    [InlineData(0x20, 0, 6)] [InlineData(0x20, 16, 7)] [InlineData(0x20, 32, 8)]
    public void DisplayIdTypeXBothVersionsAndEveryStrideKeepRealContainerMetadata(byte version, byte header, int stride)
    {
        var body = TypeXBody(stride);
        var mode = Assert.Single(Capability(TypeXEdid(version, header, body)).Modes);
        Assert.Equal((1u, 8u, "displayid-x", (byte)0), (mode.BlockIndex, mode.ByteOffset, mode.Encoding, mode.Revision));
        Assert.Equal(version, mode.ContainerRevision); Assert.Equal(header, mode.ContainerFlags);
        Assert.Equal(body, mode.RawDescriptor.ToArray()); Assert.Equal(stride, mode.RawDescriptor.Length);
        Assert.Null(mode.Timing); Assert.Null(mode.DscPassThrough); Assert.Null(mode.YCbCr420CapabilityMap);
    }

    [Theory]
    [InlineData(1u, 1u, 1u, 6)] [InlineData(65536u, 65536u, 256u, 6)]
    [InlineData(1920u, 1080u, 300u, 7)] [InlineData(65536u, 65536u, 1024u, 7)]
    [InlineData(1u, 65536u, 1u, 8)] [InlineData(65536u, 1u, 1024u, 8)]
    public void DisplayIdTypeXUnsignedGeometryAndRefreshRetainFullPlusOneExtent(uint width, uint height, uint rate, int stride)
    {
        var mode = Assert.Single(Capability(TypeXEdid(0x20, (byte)((stride - 6) << 4), TypeXBody(stride, width: width, height: height, rate: rate))).Modes);
        Assert.Equal(width, mode.Width); Assert.Equal(height, mode.Height); Assert.Equal(rate, mode.NominalVerticalRateHz);
        Assert.False(mode.Interlaced); Assert.Null(mode.Timing);
    }

    [Theory]
    [InlineData(1, 0)] [InlineData(1, 1)] [InlineData(1, 2)]
    [InlineData(2, 0)] [InlineData(2, 1)] [InlineData(2, 2)]
    [InlineData(3, 0)] [InlineData(3, 1)] [InlineData(3, 2)]
    public void DisplayIdTypeXKeepsEverySupportedRbAndStereoCode(byte rb, byte stereo)
    {
        var mode = Assert.Single(Capability(TypeXEdid(0x20, 0, TypeXBody(flags: (byte)(rb | stereo << 5)))).Modes);
        Assert.Equal((rb, stereo), (mode.CvtParameters!.ReducedBlankingVersion, mode.CvtParameters.StereoCode));
        Assert.True(mode.ReducedBlanking); Assert.Null(mode.Timing); Assert.Null(mode.Native); Assert.Null(mode.Preferred);
    }

    [Theory]
    [InlineData(6, false)] [InlineData(6, true)] [InlineData(7, false)]
    [InlineData(7, true)] [InlineData(8, false)] [InlineData(8, true)]
    public void DisplayIdTypeXRbTwoFractionalSupportDoesNotSynthesizeAnotherMode(int stride, bool fractional)
    {
        var modes = Capability(TypeXEdid(0x20, (byte)((stride - 6) << 4), TypeXBody(stride, (byte)(fractional ? 0x12 : 2)))).Modes;
        var mode = Assert.Single(modes); var parameters = mode.CvtParameters!;
        Assert.Equal(fractional, parameters.AlternativeRefresh1000Over1001Supported);
        Assert.Equal(60u, mode.NominalVerticalRateHz); Assert.Null(mode.Timing);
        Assert.Null(parameters.HorizontalBlankingBase160Pixels); Assert.Null(parameters.EarlyVsyncRequired);
        Assert.Null(parameters.DeltaHorizontalBlankingCode); Assert.Null(parameters.AdditionalVerticalBlankingCode);
    }

    [Theory]
    [InlineData(false, false, 0, 0, false)] [InlineData(false, true, 5, 7, false)]
    [InlineData(false, false, 6, 2, true)] [InlineData(false, true, 7, 7, true)]
    [InlineData(true, false, 0, 0, true)] [InlineData(true, true, 5, 2, false)]
    [InlineData(true, false, 6, 7, false)] [InlineData(true, true, 7, 2, true)]
    public void DisplayIdTypeXRbThreeRetainsVariantCodesWithoutGeneratingTiming(bool base160, bool early, byte delta, byte additional, bool alternative)
    {
        var flags = (byte)(3 | (base160 ? 16 : 0) | (early ? 8 : 0));
        var body = TypeXBody(8, flags, delta: delta, additional: additional, alternative: alternative);
        var mode = Assert.Single(Capability(TypeXEdid(0x20, 32, body)).Modes); var parameters = mode.CvtParameters!;
        Assert.Equal(base160, parameters.HorizontalBlankingBase160Pixels); Assert.Equal(early, parameters.EarlyVsyncRequired);
        Assert.Equal(delta, parameters.DeltaHorizontalBlankingCode); Assert.Equal(additional, parameters.AdditionalVerticalBlankingCode);
        Assert.Equal(alternative, parameters.AlternativeMinimumVerticalBlanking); Assert.Null(parameters.AlternativeRefresh1000Over1001Supported);
        Assert.Equal(body, mode.RawDescriptor.ToArray()); Assert.Null(mode.Timing);
    }

    [Theory]
    [InlineData(1, 6)] [InlineData(1, 7)] [InlineData(1, 8)]
    [InlineData(2, 6)] [InlineData(2, 7)] [InlineData(2, 8)]
    [InlineData(3, 6)] [InlineData(3, 7)] [InlineData(3, 8)]
    public void DisplayIdTypeXAbsentAndInapplicableParametersStayNull(byte rb, int stride)
    {
        var cvt = Assert.Single(Capability(TypeXEdid(0x20, (byte)((stride - 6) << 4), TypeXBody(stride, rb))).Modes).CvtParameters!;
        Assert.Equal(rb == 2 ? (bool?)false : null, cvt.AlternativeRefresh1000Over1001Supported);
        Assert.Equal(rb == 3 ? (bool?)false : null, cvt.HorizontalBlankingBase160Pixels);
        Assert.Equal(rb == 3 ? (bool?)false : null, cvt.EarlyVsyncRequired);
        Assert.Equal(rb == 3 && stride > 6 ? (byte?)0 : null, cvt.DeltaHorizontalBlankingCode);
        Assert.Equal(rb == 3 && stride > 6 ? (byte?)0 : null, cvt.AdditionalVerticalBlankingCode);
        Assert.Equal(stride == 8 ? (bool?)false : null, cvt.AlternativeMinimumVerticalBlanking);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void DisplayIdTypeXEncoding420IsNotOnly420OrCurrentEncoding(bool encoded)
    {
        var mode = Assert.Single(Capability(TypeXEdid(0x20, 0, TypeXBody(flags: (byte)(encoded ? 0x83 : 3)))).Modes);
        Assert.Equal(encoded, mode.YCbCr420Encoding); Assert.Equal(encoded ? (bool?)true : null, mode.YCbCr420Supported);
        Assert.Null(mode.YCbCr420Only); Assert.Null(mode.YCbCr420CapabilityMap); Assert.Null(mode.DscPassThrough);
    }

    [Fact]
    public void DisplayIdTypeXRepeatedBodiesAndContinuationSourcesAreNeverMerged()
    {
        var body = TypeXBody(); var bytes = Base(2);
        Did8Section(bytes, 1, 0x20, TypeXData(0, body, body), 1, false);
        Did8Section(bytes, 2, 0x20, TypeXData(0, body), 0, true);
        var modes = Capability(bytes).Modes;
        Assert.Equal(new uint[] { 1, 1, 2 }, modes.Select(mode => mode.BlockIndex));
        Assert.Equal(new uint[] { 8, 14, 8 }, modes.Select(mode => mode.ByteOffset));
        Assert.All(modes, mode => Assert.Equal(body, mode.RawDescriptor.ToArray()));
    }

    [Theory]
    [InlineData(0, 6, 19)] [InlineData(16, 7, 16)] [InlineData(32, 8, 14)]
    public void DisplayIdTypeXConsumesTheFullNaturalSectionListWithoutACtaLengthCap(byte header, int stride, int count)
    {
        var body = TypeXBody(stride);
        var data = TypeXData(header, Enumerable.Range(0, count).Select(_ => body).ToArray());
        var modes = Capability(TypeXSection(0x20, data)).Modes;
        Assert.Equal(count, modes.Length);
        Assert.Equal(checked(8u + (uint)((count - 1) * stride)), modes[^1].ByteOffset);
        Assert.All(modes, mode => Assert.Equal(body, mode.RawDescriptor.ToArray()));
    }

    [Fact]
    public void DisplayIdTypeXRejectsEveryUnverifiedHeaderWithoutMaskingRevisionOrStride()
    {
        for (var value = 0; value <= 255; value++)
        {
            if (value is 0 or 16 or 32) continue;
            AssertTypeXUnknown(TypeXEdid(0x20, checked((byte)value), TypeXBody()));
        }
    }

    [Theory]
     [InlineData(0x12)]
    public void DisplayIdTypeXDoesNotBorrowTheTwoXTagInLegacyContainers(byte version)
        => AssertTypeXUnknown(TypeXEdid(version, 0, TypeXBody()));

    [Theory]
    [InlineData(0x11)] [InlineData(0x14)] [InlineData(0x22)]
    public void DisplayIdTypeXDoesNotExpandTheExistingContainerVersionContract(byte version)
        => AssertTypeXUnknown(TypeXEdid(version, 0, TypeXBody()));

    [Theory]
    [InlineData(0, 0)] [InlineData(0, 1)] [InlineData(0, 5)] [InlineData(0, 7)] [InlineData(0, 11)]
    [InlineData(16, 6)] [InlineData(16, 8)] [InlineData(32, 7)] [InlineData(32, 9)]
    public void DisplayIdTypeXRequiresAPositiveWholeDescriptorList(byte header, int length)
    {
        var payload = new byte[length]; var good = TypeXBody(8);
        good.AsSpan(0, Math.Min(length, good.Length)).CopyTo(payload);
        AssertTypeXUnknown(TypeXEdid(0x20, header, payload));
    }

    [Theory]
    [InlineData(0)] [InlineData(4)] [InlineData(5)] [InlineData(6)] [InlineData(7)]
    public void DisplayIdTypeXUnknownFormulaCannotFallbackToAnInventedTiming(byte rb)
        => AssertTypeXUnknown(TypeXEdid(0x20, 0, TypeXBody(flags: rb)));

    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void DisplayIdTypeXUnknownStereoRejectsTheWholeField(byte rb)
        => AssertTypeXUnknown(TypeXEdid(0x20, 0, TypeXBody(flags: (byte)(rb | 0x60))));

    [Theory]
    [InlineData(0x11)] [InlineData(0x09)] [InlineData(0x0A)]
    public void DisplayIdTypeXDoesNotApplyRbThreeFlagsToOtherVariants(byte flags)
        => AssertTypeXUnknown(TypeXEdid(0x20, 0, TypeXBody(flags: flags)));

    [Theory]
    [InlineData(7, 1, 4)] [InlineData(7, 2, 0xFC)] [InlineData(8, 1, 4)] [InlineData(8, 2, 0x80)]
    public void DisplayIdTypeXDoesNotInterpretDeltaOrAdditionalBitsForRbOneOrTwo(int stride, byte rb, byte extension)
    {
        var body = TypeXBody(stride, rb); body[6] = extension;
        AssertTypeXUnknown(TypeXEdid(0x20, (byte)((stride - 6) << 4), body));
    }

    [Fact]
    public void DisplayIdTypeXUnexplainedFinalByteBitsNeverPublishAGoodPrefix()
    {
        for (var bit = 1; bit < 8; bit++)
        {
            var body = TypeXBody(8); body[7] = checked((byte)(1 << bit));
            AssertTypeXUnknown(TypeXEdid(0x20, 32, TypeXBody(8), body));
        }
    }

    [Theory]
    [InlineData(0, 6)] [InlineData(16, 7)] [InlineData(32, 8)]
    public void DisplayIdTypeXInvalidLastBodyCannotLeakEarlierValidBodies(byte header, int stride)
        => AssertTypeXUnknown(TypeXEdid(0x20, header, TypeXBody(stride), TypeXBody(stride, 0)));

    [Theory]
    [InlineData(127)] [InlineData(255)]
    public void DisplayIdTypeXAllOuterChecksumsRemainRequired(int index)
    {
        var bytes = TypeXEdid(0x20, 0, TypeXBody()); bytes[index] ^= 1;
        AssertTypeXUnknown(bytes);
    }

    [Fact]
    public void DisplayIdTypeXInnerChecksumCannotBeRepairedByOnlyTheOuterChecksum()
    {
        var bytes = TypeXEdid(0x20, 0, TypeXBody()); bytes[142] ^= 1;
        AssertTypeXUnknown(Checked(bytes));
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public void DisplayIdTypeXBlockExtentAndUnexplainedTailAreNotClipped(int variant)
    {
        var bytes = TypeXEdid(0x20, 0, TypeXBody());
        if (variant == 0) bytes[135] = 7; // Body extends beyond the complete section data.
        if (variant == 1) bytes[135] = 5; // Body has a trailing partial descriptor.
        if (variant == 2) bytes[150] = 1; // Nonzero bytes after the inner checksum.
        DisplayIdChecksum(bytes, 1); AssertTypeXUnknown(Checked(bytes));
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)]
    public void DisplayIdTypeXWholeContinuationAndPhysicalLengthAreRequired(int variant)
    {
        var bytes = Base(2);
        Did8Section(bytes, 1, 0x20, TypeXData(0, TypeXBody()), 1, false);
        Did8Section(bytes, 2, 0x20, TypeXData(0, TypeXBody()), 0, true);
        if (variant == 0) bytes[131] = 0;
        if (variant == 1) bytes[259] = 2;
        if (variant == 2) bytes[260] = 1;
        if (variant == 3) bytes[257] = 0x21;
        DisplayIdChecksum(bytes, 1); DisplayIdChecksum(bytes, 2); Checked(bytes);
        if (variant == 4) Array.Resize(ref bytes, 256);
        if (variant == 5) Array.Resize(ref bytes, 512);
        AssertTypeXUnknown(bytes);
    }

    [Fact]
    public void DisplayIdTypeXDoesNotMakeASeparateUnknownTypeIxBlockHarmless()
        => AssertTypeXUnknown(TypeXSection(0x20, [.. TypeXData(0, TypeXBody()), 0x24, 0, 1, 0]));

    [Theory]
    [InlineData(0, 6)] [InlineData(16, 7)] [InlineData(32, 8)]
    public void DisplayIdAndCtaTypeXKeepTheSameBodyValuesButDifferentOriginalSources(byte header, int stride)
    {
        var body = TypeXBody(stride);
        var cta = Assert.Single(Capability(Cta(CtaCvtData(header, body))).Modes);
        var displayId = Assert.Single(Capability(TypeXEdid(0x20, header, body)).Modes);
        Assert.Equal((7u, "cta-vtdb-x", (byte?)3), (cta.ByteOffset, cta.Encoding, cta.ContainerRevision));
        Assert.Equal((8u, "displayid-x", (byte?)0x20), (displayId.ByteOffset, displayId.Encoding, displayId.ContainerRevision));
        var sourceAdjusted = cta with { ByteOffset = 8, Encoding = "displayid-x", ContainerRevision = 0x20 };
        Assert.Equal(JsonSerializer.Serialize(sourceAdjusted), JsonSerializer.Serialize(displayId));
    }

    [Fact]
    public void DisplayIdTypeXNeverConsumesTheGlobalCtaT7T10Ordinal()
    {
        var bytes = Base(3);
        SetCta(bytes, 1, [.. SelectionData(0x0D, [145, 146]), .. SelectionT7(148499)]);
        Did8Section(bytes, 2, 0x20, TypeXData(0, TypeXBody()), 0, false);
        SetCta(bytes, 3, CtaCvtData(0, TypeXBody()));
        var capability = Capability(bytes); var references = Assert.Single(capability.SelectionDeclarations).References;
        Assert.Equal(new[] { "cta-vtdb-vii", "cta-vtdb-x" }, references.Select(reference => reference.TargetEncoding));
        Assert.Equal(new uint[] { 1, 3 }, references.Select(reference => reference.Target!.BlockIndex));
        Assert.Equal(new uint[] { 11, 7 }, references.Select(reference => reference.Target!.ByteOffset));
        Assert.Equal(3, capability.Modes.Length); Assert.Equal("displayid-x", capability.Modes[1].Encoding);
    }

    [Theory]
    [InlineData(129)] [InlineData(145)] [InlineData(146)] [InlineData(254)]
    public void DisplayIdTypeXCannotSatisfyAMissingOrUnverifiedCtaSelection(byte svr)
    {
        var bytes = Base(2); SetCta(bytes, 1, SelectionData(0x0D, [svr]));
        Did8Section(bytes, 2, 0x20, TypeXData(0, TypeXBody()), 0, false);
        AssertTypeXUnknown(Checked(bytes));
    }

    [Fact]
    public void DisplayIdTypeXDoesNotChangeExistingCodeOrderingOrCtaFlags()
    {
        var bytes = Base(2); SetCta(bytes, 1, [0x41, 16]); bytes[131] = 0x80;
        Did8Section(bytes, 2, 0x20, [.. Did8Data(0, [4]), .. TypeXData(32, TypeXBody(8))], 0, false);
        var capability = Capability(bytes);
        Assert.Equal((byte?)0x80, capability.CtaFlags);
        Assert.Equal(new[] { "cta-vic", "displayid-dmt-code", "displayid-x" }, capability.Modes.Select(mode => mode.Encoding));
        Assert.Equal(4u, capability.Modes[1].Code); Assert.Equal(640u, capability.Modes[1].Width);
        Assert.Equal((2u, 12u, (byte?)32, (byte?)0x20), (capability.Modes[2].BlockIndex, capability.Modes[2].ByteOffset,
            capability.Modes[2].ContainerFlags, capability.Modes[2].ContainerRevision));
        Assert.Null(capability.Modes[2].Native); Assert.Null(capability.Modes[2].DscPassThrough);
    }

    [Fact]
    public void DisplayIdTypeXTypedRoundtripKeepsValuesWithoutImmutableArrayReferenceEquality()
    {
        var capability = Capability(TypeXEdid(0x20, 32, Convert.FromHexString("9B7F0737043B5801")));
        var restored = JsonSerializer.Deserialize<EdidModeCapability>(JsonSerializer.Serialize(capability))!;
        var expected = Assert.Single(capability.Modes); var actual = Assert.Single(restored.Modes);
        Assert.Equal(JsonSerializer.Serialize(expected), JsonSerializer.Serialize(actual));
        Assert.Equal(expected.RawDescriptor.ToArray(), actual.RawDescriptor.ToArray());
        Assert.Equal(expected.CvtParameters, actual.CvtParameters); Assert.Null(actual.Timing);
        Assert.Empty(restored.SelectionDeclarations); Assert.Empty(restored.Ranges);
    }

    private static byte[] TypeXBody(int stride = 6, byte flags = 3, uint width = 1920, uint height = 1080,
        uint rate = 60, byte delta = 0, byte additional = 0, bool alternative = false)
    {
        var body = new byte[stride]; body[0] = flags;
        var w = checked(width - 1); var h = checked(height - 1); var r = checked(rate - 1);
        body[1] = (byte)(w & 255); body[2] = checked((byte)(w >> 8));
        body[3] = (byte)(h & 255); body[4] = checked((byte)(h >> 8)); body[5] = (byte)(r & 255);
        if (stride > 6) body[6] = checked((byte)((r >> 8) | (uint)delta << 2 | (uint)additional << 5));
        if (stride == 8) body[7] = alternative ? (byte)1 : (byte)0;
        return body;
    }
    private static byte[] TypeXData(byte header, params byte[][] bodies)
    {
        var data = new List<byte> { 0x2A, header, 0 };
        foreach (var body in bodies) data.AddRange(body);
        data[2] = checked((byte)(data.Count - 3)); return data.ToArray();
    }
    private static byte[] TypeXEdid(byte version, byte header, params byte[][] bodies)
        => TypeXSection(version, TypeXData(header, bodies));
    private static byte[] TypeXSection(byte version, byte[] data)
    {
        var bytes = Base(1); Did8Section(bytes, 1, version, data, 0, false); return Checked(bytes);
    }
    private static void AssertTypeXUnknown(byte[] bytes)
    {
        var read = WindowsMonitorEdidReader.CreateReadResult(bytes);
        Assert.True(read.Complete); Assert.NotNull(read.Capabilities); Assert.Null(read.Capabilities.ModeCapability);
        Assert.Equal(bytes, Convert.FromBase64String(read.RawEdidDeclaredBase64!));
    }
}
