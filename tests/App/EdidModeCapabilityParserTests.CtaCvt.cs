using System.Text.Json;
using System.Text.Json.Nodes;
using ResourceManager.App.Domain.DeviceTopology;
using ResourceManager.App.Infrastructure.DeviceTopology;
using ResourceManager.App.Infrastructure.DeviceTopology.Snapshots;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.ReadWrite.Windows.Devices.Display;

namespace Resource_Manager_APP.Tests;

public sealed partial class EdidModeCapabilityParserTests
{
    [Theory]
    [InlineData(0, 6)]
    [InlineData(16, 7)]
    [InlineData(32, 8)]
    public void CtaTypeXExactStridesPreserveSourceAndOriginalParameters(byte header, int stride)
    {
        var body = CtaCvtBody(stride); var bytes = Cta(CtaCvtData(header, body));
        var mode = Assert.Single(Capability(bytes).Modes);
        Assert.Equal((1u, 7u, "cta-vtdb-x", (byte)0), (mode.BlockIndex, mode.ByteOffset, mode.Encoding, mode.Revision));
        Assert.Equal(header, mode.ContainerFlags); Assert.Equal((byte)3, mode.ContainerRevision);
        Assert.Equal((1920u, 1080u, 60u), (mode.Width!.Value, mode.Height!.Value, mode.NominalVerticalRateHz!.Value));
        Assert.Equal(body, mode.RawDescriptor.ToArray()); Assert.Equal(stride, mode.RawDescriptor.Length);
        Assert.Null(mode.Code); Assert.Null(mode.Native); Assert.Null(mode.Preferred); Assert.Null(mode.DscPassThrough);
        Assert.Equal("cvt", mode.Formula); Assert.True(mode.ReducedBlanking); Assert.False(mode.Interlaced); Assert.Null(mode.Timing);
        var cvt = Assert.IsType<EdidCvtTimingParameters>(mode.CvtParameters);
        Assert.Equal((byte)3, cvt.ReducedBlankingVersion); Assert.Equal((byte)0, cvt.StereoCode);
        bytes[135] = 0; Assert.Equal(body, mode.RawDescriptor.ToArray());
    }

    [Theory]
    [InlineData(1, 1, 1, 6)]
    [InlineData(65536, 65536, 256, 6)]
    [InlineData(65536, 65536, 1024, 8)]
    [InlineData(1920, 1080, 300, 7)]
    public void CtaTypeXDimensionsAndRefreshUseUnsignedPlusOneWithoutTruncation(uint width, uint height, uint refresh, int stride)
    {
        var mode = Assert.Single(Capability(Cta(CtaCvtData((byte)((stride - 6) << 4), CtaCvtBody(stride, width: width, height: height, refresh: refresh)))).Modes);
        Assert.Equal(width, mode.Width); Assert.Equal(height, mode.Height); Assert.Equal(refresh, mode.NominalVerticalRateHz);
        Assert.Null(mode.Timing); Assert.Null(mode.Code);
    }

    [Theory]
    [InlineData(6, 1)]
    [InlineData(6, 256)]
    [InlineData(7, 1)]
    [InlineData(7, 1024)]
    [InlineData(8, 1)]
    [InlineData(8, 1024)]
    public void CtaTypeXRefreshParameterBoundariesAreNotAnExactClockOrRate(int stride, uint refresh)
    {
        var mode = Assert.Single(Capability(Cta(CtaCvtData((byte)((stride - 6) << 4), CtaCvtBody(stride, refresh: refresh)))).Modes);
        Assert.Equal(refresh, mode.NominalVerticalRateHz); Assert.Null(mode.Timing); Assert.Equal("cvt", mode.Formula);
    }

    [Theory]
    [InlineData(1, 0)] [InlineData(1, 1)] [InlineData(1, 2)]
    [InlineData(2, 0)] [InlineData(2, 1)] [InlineData(2, 2)]
    [InlineData(3, 0)] [InlineData(3, 1)] [InlineData(3, 2)]
    public void CtaTypeXRetainsEachSupportedReducedBlankingAndStereoCode(byte rb, byte stereo)
    {
        var mode = Assert.Single(Capability(Cta(CtaCvtData(0, CtaCvtBody(flags: (byte)(rb | stereo << 5))))).Modes);
        Assert.Equal((rb, stereo), (mode.CvtParameters!.ReducedBlankingVersion, mode.CvtParameters.StereoCode));
        Assert.True(mode.ReducedBlanking); Assert.Null(mode.Timing); Assert.Null(mode.Native); Assert.Null(mode.Preferred);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void CtaTypeXReducedBlankingTwoAlternativeRefreshDoesNotGenerateAnotherMode(bool supported)
    {
        var mode = Assert.Single(Capability(Cta(CtaCvtData(0x10, CtaCvtBody(7, (byte)(supported ? 0x12 : 2), refresh: 300)))).Modes);
        var cvt = mode.CvtParameters!;
        Assert.Equal(supported, cvt.AlternativeRefresh1000Over1001Supported);
        Assert.Null(cvt.HorizontalBlankingBase160Pixels); Assert.Null(cvt.EarlyVsyncRequired);
        Assert.Null(cvt.DeltaHorizontalBlankingCode); Assert.Null(cvt.AdditionalVerticalBlankingCode);
        Assert.Equal(300u, mode.NominalVerticalRateHz); Assert.Null(mode.Timing);
    }

    [Theory]
    [InlineData(false, false, 0, 0, false)]
    [InlineData(false, true, 5, 7, false)]
    [InlineData(false, false, 6, 2, true)]
    [InlineData(false, true, 7, 7, true)]
    [InlineData(true, false, 0, 0, true)]
    [InlineData(true, true, 5, 2, false)]
    [InlineData(true, false, 6, 7, false)]
    [InlineData(true, true, 7, 2, true)]
    public void CtaTypeXReducedBlankingThreeRetainsVariantAndDeltaCodesWithoutSynthesizingTiming(bool base160, bool early, byte delta, byte additional, bool alternativeMinimum)
    {
        var flags = (byte)(3 | (base160 ? 0x10 : 0) | (early ? 8 : 0));
        var body = CtaCvtBody(8, flags, delta: delta, additional: additional, alternativeMinimum: alternativeMinimum);
        var mode = Assert.Single(Capability(Cta(CtaCvtData(0x20, body))).Modes); var cvt = mode.CvtParameters!;
        Assert.Equal(base160, cvt.HorizontalBlankingBase160Pixels); Assert.Equal(early, cvt.EarlyVsyncRequired);
        Assert.Equal(delta, cvt.DeltaHorizontalBlankingCode); Assert.Equal(additional, cvt.AdditionalVerticalBlankingCode);
        Assert.Equal(alternativeMinimum, cvt.AlternativeMinimumVerticalBlanking); Assert.Null(cvt.AlternativeRefresh1000Over1001Supported);
        Assert.Equal(body, mode.RawDescriptor.ToArray()); Assert.Null(mode.Timing); Assert.Single(Capability(Cta(CtaCvtData(0x20, body))).Modes);
    }

    [Theory]
    [InlineData(1, 6)] [InlineData(1, 7)] [InlineData(1, 8)]
    [InlineData(2, 6)] [InlineData(2, 7)] [InlineData(2, 8)]
    [InlineData(3, 6)] [InlineData(3, 7)] [InlineData(3, 8)]
    public void CtaTypeXAbsentAndInapplicableExtensionParametersRemainNull(byte rb, int stride)
    {
        var cvt = Assert.Single(Capability(Cta(CtaCvtData((byte)((stride - 6) << 4), CtaCvtBody(stride, rb)))).Modes).CvtParameters!;
        Assert.Equal(rb == 2 ? (bool?)false : null, cvt.AlternativeRefresh1000Over1001Supported);
        Assert.Equal(rb == 3 ? (bool?)false : null, cvt.HorizontalBlankingBase160Pixels);
        Assert.Equal(rb == 3 ? (bool?)false : null, cvt.EarlyVsyncRequired);
        Assert.Equal(rb == 3 && stride > 6 ? (byte?)0 : null, cvt.DeltaHorizontalBlankingCode);
        Assert.Equal(rb == 3 && stride > 6 ? (byte?)0 : null, cvt.AdditionalVerticalBlankingCode);
        Assert.Equal(stride == 8 ? (bool?)false : null, cvt.AlternativeMinimumVerticalBlanking);
    }

    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void CtaTypeXAlternativeMinimumVerticalBlankingBitAppliesToAllSupportedVersions(byte rb)
    {
        var cvt = Assert.Single(Capability(Cta(CtaCvtData(0x20, CtaCvtBody(8, rb, alternativeMinimum: true)))).Modes).CvtParameters!;
        Assert.True(cvt.AlternativeMinimumVerticalBlanking);
    }

    [Theory]
    [InlineData(1, 300)] [InlineData(1, 600)] [InlineData(1, 1024)]
    [InlineData(2, 300)] [InlineData(2, 600)] [InlineData(2, 1024)]
    public void CtaTypeXHighRefreshBitsAreValidForReducedBlankingOneAndTwo(byte rb, uint rate)
        => Assert.Equal(rate, Assert.Single(Capability(Cta(CtaCvtData(0x10, CtaCvtBody(7, rb, refresh: rate)))).Modes).NominalVerticalRateHz);

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void CtaTypeXYCbCrFlagIsOriginalEncodingWithoutPreferredDscOrOnlyInference(bool encoded)
    {
        var mode = Assert.Single(Capability(Cta(CtaCvtData(0, CtaCvtBody(flags: (byte)(encoded ? 0x83 : 3))))).Modes);
        Assert.Equal(encoded, mode.YCbCr420Encoding); Assert.Equal(encoded ? (bool?)true : null, mode.YCbCr420Supported);
        Assert.Null(mode.YCbCr420Only); Assert.Null(mode.Native); Assert.Null(mode.Preferred); Assert.Null(mode.DscPassThrough);
    }

    [Fact]
    public void CtaTypeXFixedSixAndSevenByteExamplesDoNotDependOnTheFixtureEncoder()
    {
        byte[] first = [1, 0x7F, 7, 0x37, 4, 0x3B];
        byte[] second = [0x12, 0x7F, 7, 0x37, 4, 0x2B, 1];
        var six = Assert.Single(Capability(Cta([0xE8, 0x2A, 0, .. first])).Modes);
        var seven = Assert.Single(Capability(Cta([0xE9, 0x2A, 0x10, .. second])).Modes);
        Assert.Equal((1920u, 1080u, 60u), (six.Width!.Value, six.Height!.Value, six.NominalVerticalRateHz!.Value));
        Assert.Equal((1920u, 1080u, 300u), (seven.Width!.Value, seven.Height!.Value, seven.NominalVerticalRateHz!.Value));
        Assert.Equal((byte)1, six.CvtParameters!.ReducedBlankingVersion);
        Assert.Equal((byte)2, seven.CvtParameters!.ReducedBlankingVersion);
        Assert.True(seven.CvtParameters.AlternativeRefresh1000Over1001Supported);
        Assert.Null(six.CvtParameters.AlternativeMinimumVerticalBlanking); Assert.Null(seven.CvtParameters.DeltaHorizontalBlankingCode);
        Assert.Equal(first, six.RawDescriptor.ToArray()); Assert.Equal(second, seven.RawDescriptor.ToArray());
        Assert.Null(six.Timing); Assert.Null(seven.Timing);
    }

    [Fact]
    public void CtaTypeXFixedEightByteExampleRetainsAllOriginalMeaningfulParameters()
    {
        byte[] body = [0x9B, 0x7F, 7, 0x37, 4, 0x3B, 0x58, 1];
        var mode = Assert.Single(Capability(Cta(CtaCvtData(0x20, body))).Modes); var cvt = mode.CvtParameters!;
        Assert.Equal((1920u, 1080u, 60u), (mode.Width!.Value, mode.Height!.Value, mode.NominalVerticalRateHz!.Value));
        Assert.Equal((byte)3, cvt.ReducedBlankingVersion); Assert.Equal((byte)0, cvt.StereoCode);
        Assert.True(cvt.HorizontalBlankingBase160Pixels); Assert.True(cvt.EarlyVsyncRequired);
        Assert.Equal((byte)6, cvt.DeltaHorizontalBlankingCode); Assert.Equal((byte)2, cvt.AdditionalVerticalBlankingCode);
        Assert.True(cvt.AlternativeMinimumVerticalBlanking); Assert.Null(cvt.AlternativeRefresh1000Over1001Supported);
        Assert.True(mode.YCbCr420Encoding); Assert.Null(mode.Timing);
    }

    [Fact]
    public void CtaTypeXDuplicateAndCrossBlockDeclarationsRemainIndependentAndRawIsCopied()
    {
        var body = CtaCvtBody(); var bytes = Base(2); SetCta(bytes, 1, CtaCvtData(0, body, body)); SetCta(bytes, 2, CtaCvtData(0, body));
        var modes = Capability(bytes).Modes;
        Assert.Equal(new uint[] { 1, 1, 2 }, modes.Select(mode => mode.BlockIndex)); Assert.Equal(new uint[] { 7, 13, 7 }, modes.Select(mode => mode.ByteOffset));
        Assert.All(modes, mode => Assert.Equal(body, mode.RawDescriptor.ToArray()));
        bytes[135] = 0; Assert.All(modes, mode => Assert.Equal(body, mode.RawDescriptor.ToArray()));
    }

    [Theory]
    [InlineData(0, 6, 4)] [InlineData(16, 7, 4)] [InlineData(32, 8, 3)]
    public void CtaTypeXNaturalWireMaximumListsAreNotAnAdditionalSoftwareCap(byte header, int stride, int count)
    {
        var bodies = Enumerable.Range(0, count).Select(_ => CtaCvtBody(stride)).ToArray();
        Assert.Equal(count, Capability(Cta(CtaCvtData(header, bodies))).Modes.Length);
    }

    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)] [InlineData(6)] [InlineData(7)]
    [InlineData(8)] [InlineData(9)] [InlineData(17)] [InlineData(24)] [InlineData(33)] [InlineData(40)]
    [InlineData(48)] [InlineData(64)] [InlineData(112)] [InlineData(128)] [InlineData(255)]
    public void CtaTypeXUnknownHeaderRevisionFlagsOrStrideRejectWholeCapability(byte header)
        => AssertCtaCvtUnknown(Cta(CtaCvtData(header, CtaCvtBody())));

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)]
    [InlineData(7)] [InlineData(8)] [InlineData(9)] [InlineData(11)]
    public void CtaTypeXEmptyTruncatedOrTrailingBodyNeverPublishesAValidPrefix(int length)
    {
        var body = new byte[length]; CtaCvtBody().AsSpan(0, Math.Min(length, 6)).CopyTo(body);
        AssertCtaCvtUnknown(Cta(CtaCvtData(0, body)));
    }

    [Theory]
    [InlineData(0)] [InlineData(4)] [InlineData(5)] [InlineData(6)] [InlineData(7)]
    public void CtaTypeXUnverifiedReducedBlankingFallbackIsNotAccepted(byte rb)
        => AssertCtaCvtUnknown(Cta(CtaCvtData(0, CtaCvtBody(flags: rb))));

    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void CtaTypeXReservedStereoRejectsAllSupportedVersions(byte rb)
        => AssertCtaCvtUnknown(Cta(CtaCvtData(0, CtaCvtBody(flags: (byte)(rb | 0x60)))));

    [Theory]
    [InlineData(0x11)] [InlineData(0x09)] [InlineData(0x0A)] [InlineData(0x19)] [InlineData(0x1A)]
    public void CtaTypeXVariantBitsMustBeApplicableToTheirReducedBlankingVersion(byte flags)
        => AssertCtaCvtUnknown(Cta(CtaCvtData(0, CtaCvtBody(flags: flags))));

    [Theory]
    [InlineData(1, 4)] [InlineData(1, 28)] [InlineData(1, 32)] [InlineData(1, 224)] [InlineData(1, 252)]
    [InlineData(2, 4)] [InlineData(2, 28)] [InlineData(2, 32)] [InlineData(2, 224)] [InlineData(2, 252)]
    public void CtaTypeXDeltaAndAdditionalBlankingBitsAreNotApplicableToVersionsOneAndTwo(byte rb, byte extension)
    {
        var body = CtaCvtBody(7, rb); body[6] = extension; AssertCtaCvtUnknown(Cta(CtaCvtData(0x10, body)));
    }

    [Theory]
    [InlineData(2)] [InlineData(4)] [InlineData(8)] [InlineData(16)] [InlineData(32)] [InlineData(64)] [InlineData(128)] [InlineData(255)]
    public void CtaTypeXUnexplainedEighthByteBitsAreNotSilentlyIgnored(byte extension)
    {
        var body = CtaCvtBody(8); body[7] = extension; AssertCtaCvtUnknown(Cta(CtaCvtData(0x20, body)));
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(4)] [InlineData(255)]
    public void CtaTypeXStillRequiresTheVerifiedCtaContainerRevision(byte revision)
    {
        var bytes = Cta(CtaCvtData(0, CtaCvtBody())); bytes[129] = revision; AssertCtaCvtUnknown(Checked(bytes));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void CtaTypeXLastBlockChecksumAndBadDescriptorCannotExposeAnEarlierPrefix(bool badChecksum)
    {
        var bytes = Base(2); SetCta(bytes, 1, CtaCvtData(0, CtaCvtBody())); SetCta(bytes, 2, CtaCvtData(0, CtaCvtBody())); Checked(bytes);
        if (badChecksum) bytes[^1]++; else { bytes[263] = 0; Checked(bytes); }
        AssertCtaCvtUnknown(bytes);
    }

    [Fact]
    public void CtaTypeXSourcePositionAndVtdbOrderAreSharedWithT7ButNotDmtOrDtd()
    {
        var bytes = Base(2); Dtd().CopyTo(bytes, 54);
        SetCta(bytes, 1, [.. SelectionData(0x0D, [145, 146, 147, 148, 129]), .. SelectionT7(148499), .. CtaCvtData(0, CtaCvtBody(flags: 1))]);
        SetCta(bytes, 2, [0xE3, 0x23, 1, 4, .. CtaCvtData(0x10, CtaCvtBody(7, 2, refresh: 300)), .. SelectionT7(148349)]);
        var capability = Capability(bytes); Assert.Equal(6, capability.Modes.Length);
        var targets = Assert.Single(capability.SelectionDeclarations).References;
        Assert.Equal(new[] { "cta-vtdb-vii", "cta-vtdb-x", "cta-vtdb-x", "cta-vtdb-vii", "edid-detailed" }, targets.Select(reference => reference.TargetEncoding));
        Assert.Equal(new uint[] { 1, 1, 2, 2, 0 }, targets.Select(reference => reference.Target!.BlockIndex));
        Assert.Equal(new uint[] { 14, 37, 11, 21, 54 }, targets.Select(reference => reference.Target!.ByteOffset));
        Assert.Equal(CtaCvtBody(flags: 1), targets[1].Target!.RawDescriptor.ToArray());
        Assert.Equal(CtaCvtBody(7, 2, refresh: 300), targets[2].Target!.RawDescriptor.ToArray());
    }

    [Fact]
    public void CtaTypeXOrdinalSixteenCountsEveryMixedOriginalDescriptorWithoutDeduplication()
    {
        var bytes = Base(4);
        for (var block = 1; block <= 4; block++)
        {
            var bodies = Enumerable.Range(0, block == 1 ? 3 : 4).Select(_ => CtaCvtBody()).ToArray();
            var data = CtaCvtData(0, bodies);
            if (block == 1) data = [.. SelectionT7(148499), .. data];
            if (block == 4) data = [.. data, .. SelectionData(0x0D, [160])];
            SetCta(bytes, block, data);
        }
        var capability = Capability(bytes); Assert.Equal(16, capability.Modes.Length);
        var target = Assert.Single(Assert.Single(capability.SelectionDeclarations).References);
        Assert.Equal("cta-vtdb-x", target.TargetEncoding); Assert.Equal((4u, 25u), (target.Target!.BlockIndex, target.Target.ByteOffset));
        Assert.Equal(CtaCvtBody(), target.Target.RawDescriptor.ToArray());
    }

    [Theory]
    [InlineData(146)] [InlineData(160)] [InlineData(254)]
    public void CtaTypeXSelectionOutOfRangeOrUnverifiedReferenceHasNoFallback(byte svr)
        => AssertCtaCvtUnknown(Cta([.. CtaCvtData(0, CtaCvtBody()), .. SelectionData(0x0D, [svr])]));

    [Fact]
    public void CtaTypeXVtdbNeverUsesT8OrOrdinarySvdAsATarget()
        => AssertCtaCvtUnknown(Cta([0xE3, 0x23, 1, 4, 0x41, 16, .. SelectionData(0x0D, [145])]));

    [Fact]
    public void CtaTypeXDoesNotEnterNativeDtdCountOrCtaCapabilityMapDomain()
    {
        var bytes = Cta(CtaCvtData(0, CtaCvtBody())); bytes[131] = 1; AssertCtaCvtUnknown(Checked(bytes));
        AssertCtaCvtUnknown(Cta([0x41, 16, .. CtaCvtData(0, CtaCvtBody()), 0xE2, 0x0F, 0x02]));
    }

    [Fact]
    public void CtaTypeXOpaquePrimaryPayloadRoundTripsOriginalParametersAndSelectionReferences()
    {
        byte[] body = [0x9B, 0x7F, 7, 0x37, 4, 0x3B, 0x58, 1];
        var path = DisplayEdidModeTestData.Path(WindowsMonitorEdidReader.CreateReadResult(Cta([.. CtaCvtData(0x20, body), .. SelectionData(0x0D, [145])])));
        var catalog = new NativeDisplayCoordinatorPayloadCatalog();
        var fact = Assert.Single(Assert.Single(NativeDisplayCoordinatorObservationProjector.Create([path], [], catalog, DateTimeOffset.UtcNow), batch => batch.Source == NativeDisplaySource.DisplayConfig).Facts);
        var restored = new NativeDisplayCoordinatorPayloadCatalog(); restored.Import(catalog.Export());
        var roundtrip = Assert.Single(NativeDisplayCoordinatorReadProjection.CreateDisplayPaths([new NativeDisplayNodeOutput { Kind = (uint)NativeDisplayNodeKind.Connector, PrimaryPayloadHandle = fact.PayloadHandle }], restored)).Path;
        Assert.Equal(JsonSerializer.Serialize(path.Edid!.ModeCapability), JsonSerializer.Serialize(roundtrip.Edid!.ModeCapability));
        var mode = Assert.Single(roundtrip.Edid.ModeCapability!.Modes); Assert.Equal(body, mode.RawDescriptor.ToArray());
        Assert.Equal((byte)6, mode.CvtParameters!.DeltaHorizontalBlankingCode); Assert.Equal((byte)2, mode.CvtParameters.AdditionalVerticalBlankingCode);
        Assert.True(mode.CvtParameters.AlternativeMinimumVerticalBlanking); Assert.Null(mode.Timing);
        Assert.Equal(body, Assert.Single(Assert.Single(roundtrip.Edid.ModeCapability.SelectionDeclarations).References).Target!.RawDescriptor.ToArray());
    }

    [Fact]
    public void CtaTypeXLegacyMissingInitParametersDoesNotReparseRawAndKeepsOriginalPositionalContract()
    {
        var path = DisplayEdidModeTestData.Path(WindowsMonitorEdidReader.CreateReadResult(Cta(CtaCvtData(0, CtaCvtBody()))));
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web); var node = JsonSerializer.SerializeToNode(path, options)!.AsObject();
        Assert.True(node["edid"]!["modeCapability"]!["modes"]![0]!.AsObject().Remove("cvtParameters"));
        var legacy = node.Deserialize<DeviceTopologyDisplayPath>(options)!; var mode = Assert.Single(legacy.Edid!.ModeCapability!.Modes);
        Assert.Null(mode.CvtParameters); Assert.Equal(path.EdidRawDeclaredBase64, legacy.EdidRawDeclaredBase64);
        Assert.Equal(Assert.Single(path.Edid!.ModeCapability!.Modes).RawDescriptor.ToArray(), mode.RawDescriptor.ToArray());
        var original = new EdidModeDeclaration(0, 1, "legacy", 0, []); var (block, offset, encoding, revision, raw) = original;
        Assert.Equal((0u, 1u, "legacy", (byte)0), (block, offset, encoding, revision)); Assert.Empty(raw); Assert.Null(original.CvtParameters);
        var parent = new EdidModeCapability([], []); var (modes, ranges) = parent; Assert.Empty(modes); Assert.Empty(ranges);
    }

    [Fact]
    public void CtaTypeXParametersAloneChangeOriginalOpaqueAndDomainSemanticHashes()
    {
        var first = DisplayEdidModeTestData.Path(WindowsMonitorEdidReader.CreateReadResult(Cta(CtaCvtData(0x20, CtaCvtBody(8, delta: 1)))));
        var capability = first.Edid!.ModeCapability!; var mode = Assert.Single(capability.Modes); var parameters = mode.CvtParameters!;
        var secondCapability = capability with { Modes = capability.Modes.SetItem(0, mode with { CvtParameters = parameters with { DeltaHorizontalBlankingCode = 2 } }) };
        var second = first with { Edid = first.Edid with { ModeCapability = secondCapability } };
        Assert.Equal(first.EdidRawDeclaredBase64, second.EdidRawDeclaredBase64);
        Assert.Equal(mode.RawDescriptor.ToArray(), Assert.Single(secondCapability.Modes).RawDescriptor.ToArray());
        var catalog = new NativeDisplayCoordinatorPayloadCatalog(); Assert.NotEqual(catalog.AddDisplayPath(first), catalog.AddDisplayPath(second));
        var comparer = new DeviceTopologySemanticComparer(); Assert.NotEqual(comparer.ComputeHash(DisplayEdidModeTestData.Snapshot(first)), comparer.ComputeHash(DisplayEdidModeTestData.Snapshot(second)));
    }

    private static byte[] CtaCvtBody(int stride = 6, byte flags = 3, uint width = 1920, uint height = 1080, uint refresh = 60,
        byte delta = 0, byte additional = 0, bool alternativeMinimum = false)
    {
        var body = new byte[stride]; body[0] = flags; WriteMinusOne(body, 1, width); WriteMinusOne(body, 3, height);
        var encoded = checked(refresh - 1); body[5] = (byte)(encoded & 255);
        if (stride > 6) body[6] = checked((byte)((encoded >> 8) | (uint)delta << 2 | (uint)additional << 5));
        if (stride == 8) body[7] = alternativeMinimum ? (byte)1 : (byte)0;
        return body;
    }

    private static byte[] CtaCvtData(byte header, params byte[][] descriptors)
    {
        var bytes = new List<byte> { 0, 0x2A, header }; foreach (var descriptor in descriptors) bytes.AddRange(descriptor);
        Assert.InRange(bytes.Count - 1, 2, 31); bytes[0] = checked((byte)(0xE0 | (bytes.Count - 1))); return bytes.ToArray();
    }

    private static void AssertCtaCvtUnknown(byte[] bytes)
    {
        var read = WindowsMonitorEdidReader.CreateReadResult(bytes); Assert.True(read.Complete); Assert.NotNull(read.Capabilities);
        Assert.Null(read.Capabilities.ModeCapability); Assert.Equal(bytes, Convert.FromBase64String(read.RawEdidDeclaredBase64!));
    }
}
