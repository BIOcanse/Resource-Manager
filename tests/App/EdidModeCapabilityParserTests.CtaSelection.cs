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
    [InlineData(1)]
    [InlineData(127)]
    [InlineData(193)]
    [InlineData(219)]
    public void CtaSelectionDirectVicNeedsNoVdbAndDoesNotInventAMode(byte code)
    {
        var capability = Capability(Cta(SelectionData(0x0D, [code])));
        Assert.Empty(capability.Modes); Assert.Empty(capability.Ranges);
        var declaration = Assert.Single(capability.SelectionDeclarations);
        Assert.Equal((1u, 6u, "cta-vfpdb", (byte)3),
            (declaration.BlockIndex, declaration.ByteOffset, declaration.Encoding, declaration.Revision));
        Assert.Equal(new byte[] { code }, declaration.RawDescriptor.ToArray()); Assert.Null(declaration.Flags); Assert.Null(declaration.ImageSize);
        var reference = Assert.Single(declaration.References);
        Assert.Equal((6u, code, "cta-vic", (uint?)code),
            (reference.ByteOffset, reference.ShortVideoReference, reference.TargetEncoding, reference.Code));
        Assert.Null(reference.Target);
    }

    [Fact]
    public void CtaSelectionOrderDuplicatesAndConflictingBlocksAreAllOriginalDeclarations()
    {
        var bytes = Cta([.. SelectionData(0x0D, [16, 4, 16]), .. SelectionData(0x0D, [4, 16]),
            .. SelectionData(0x08, [16]), .. SelectionData(0x08, [4])]);
        var selections = Capability(bytes).SelectionDeclarations;
        Assert.Equal(new[] { "cta-vfpdb", "cta-vfpdb", "cta-nvrdb", "cta-nvrdb" }, selections.Select(item => item.Encoding));
        Assert.Equal(new uint[] { 6, 11, 15, 18 }, selections.Select(item => item.ByteOffset));
        Assert.Equal(new byte[] { 16, 4, 16 }, selections[0].References.Select(item => item.ShortVideoReference));
        Assert.Equal(new uint[] { 6, 7, 8 }, selections[0].References.Select(item => item.ByteOffset));
        Assert.Equal(new byte[] { 4, 16 }, selections[1].References.Select(item => item.ShortVideoReference));
        Assert.Equal(16, Assert.Single(selections[2].References).ShortVideoReference);
        Assert.Equal(4, Assert.Single(selections[3].References).ShortVideoReference);
        Assert.Empty(Capability(bytes).Modes);
    }

    [Fact]
    public void CtaSelectionForwardDtdReferencesUseBaseAndAllCtaActualDtds()
    {
        var bytes = Base(3); Dtd().CopyTo(bytes, 54);
        Descriptor(bytes, 72, 0xFC); bytes[77] = (byte)'N';
        SetCta(bytes, 1, SelectionData(0x0D, [130, 129, 130]));
        SetCta(bytes, 2, []); Dtd(true).CopyTo(bytes, 260);
        SetDisplayId(bytes, 3, 0x20, 2, DidTiming(148499), 0, false);
        var capability = Capability(bytes); var references = Assert.Single(capability.SelectionDeclarations).References;
        Assert.Equal(new uint[] { 2, 0, 2 }, references.Select(item => item.Target!.BlockIndex));
        Assert.Equal(new uint[] { 4, 54, 4 }, references.Select(item => item.Target!.ByteOffset));
        Assert.All(references, item => { Assert.Equal("edid-detailed", item.TargetEncoding); Assert.Null(item.Code); Assert.Null(item.Target!.BitIndex); });
        Assert.Equal(Dtd(true), references[0].Target!.RawDescriptor.ToArray());
        Assert.Equal(Dtd(), references[1].Target!.RawDescriptor.ToArray());
        bytes[260] = 0; Assert.Equal(Dtd(true), references[0].Target!.RawDescriptor.ToArray());
    }

    [Fact]
    public void CtaSelectionForwardT7ReferencesHaveASeparateGlobalOrdinalDomain()
    {
        var bytes = Base(3); Dtd().CopyTo(bytes, 54);
        SetCta(bytes, 1, [.. SelectionData(0x08, [146]), 0x41, 16, .. SelectionT7(148499)]);
        SetCta(bytes, 2, [.. SelectionT7(148349), .. SelectionData(0x0D, [145, 146, 129])]);
        SetDisplayId(bytes, 3, 0x20, 2, DidTiming(148499), 0, false);
        var capability = Capability(bytes); var nvr = Assert.Single(capability.SelectionDeclarations[0].References);
        Assert.Equal("cta-vtdb-vii", nvr.TargetEncoding); Assert.Equal(2u, nvr.Target!.BlockIndex); Assert.Equal(7u, nvr.Target.ByteOffset);
        var preferences = capability.SelectionDeclarations[1].References;
        Assert.Equal(new[] { "cta-vtdb-vii", "cta-vtdb-vii", "edid-detailed" }, preferences.Select(item => item.TargetEncoding));
        Assert.Equal(new uint[] { 1, 2, 0 }, preferences.Select(item => item.Target!.BlockIndex));
        Assert.Equal(12u, preferences[0].Target!.ByteOffset); Assert.Equal(54u, preferences[2].Target!.ByteOffset);
        Assert.Equal(DidTiming(148349), nvr.Target.RawDescriptor.ToArray());
    }

    [Fact]
    public void CtaSelectionDtdOrdinalSixteenIsTheSixteenthActualDtdNotASoftwareCap()
    {
        var bytes = Base(3);
        for (var offset = 54; offset <= 108; offset += 18) Dtd().CopyTo(bytes, offset);
        for (var block = 1; block <= 3; block++)
        {
            SetCta(bytes, block, block == 1 ? SelectionData(0x0D, [144]) : []);
            var start = bytes[block * 128 + 2];
            for (var index = 0; index < 4; index++) Dtd().CopyTo(bytes, block * 128 + start + index * 18);
        }
        var target = Assert.Single(Assert.Single(Capability(bytes).SelectionDeclarations).References).Target!;
        Assert.Equal((3u, 58u), (target.BlockIndex, target.ByteOffset)); Assert.Equal(Dtd(), target.RawDescriptor.ToArray());
    }

    [Fact]
    public void CtaSelectionT7OrdinalSixteenCountsEveryOriginalDeclarationWithoutDeduplication()
    {
        var bytes = Base(4);
        for (var block = 1; block <= 4; block++)
        {
            var payload = new List<byte>(); if (block == 1) payload.AddRange(SelectionData(0x0D, [160]));
            for (var index = 0; index < (block == 4 ? 1 : 5); index++) payload.AddRange(SelectionT7(148499));
            SetCta(bytes, block, payload.ToArray());
        }
        var capability = Capability(bytes); Assert.Equal(16, capability.Modes.Length);
        var target = Assert.Single(Assert.Single(capability.SelectionDeclarations).References).Target!;
        Assert.Equal((4u, 7u), (target.BlockIndex, target.ByteOffset)); Assert.Equal(DidTiming(148499), target.RawDescriptor.ToArray());
    }

    [Fact]
    public void CtaSelectionNeverOverridesOriginalPreferredOrNativeFlags()
    {
        var bytes = Base(1); bytes[24] = 2; Dtd().CopyTo(bytes, 54); SetCta(bytes, 1, [0x41, 0x84]); bytes[131] = 1;
        var baseline = Capability(bytes);
        Array.Clear(bytes, 132, 123);
        SetCta(bytes, 1, [0x41, 0x84, .. SelectionData(0x0D, [16]), .. SelectionData(0x08, [16])]);
        var selected = Capability(bytes);
        Assert.Equal(JsonSerializer.Serialize(baseline.Modes), JsonSerializer.Serialize(selected.Modes));
        Assert.True(selected.Modes[0].Preferred); Assert.True(selected.Modes[0].Native); Assert.True(selected.Modes[1].Native);
        Assert.Equal(2, selected.SelectionDeclarations.Length); Assert.Equal(2, selected.Modes.Length);
    }

    [Theory]
    [InlineData(0x0D, 0)]
    [InlineData(0x0D, 128)]
    [InlineData(0x0D, 161)]
    [InlineData(0x0D, 175)]
    [InlineData(0x0D, 176)]
    [InlineData(0x0D, 192)]
    [InlineData(0x0D, 220)]
    [InlineData(0x0D, 253)]
    [InlineData(0x0D, 254)]
    [InlineData(0x0D, 255)]
    [InlineData(0x08, 0)]
    [InlineData(0x08, 128)]
    [InlineData(0x08, 161)]
    [InlineData(0x08, 175)]
    [InlineData(0x08, 176)]
    [InlineData(0x08, 192)]
    [InlineData(0x08, 220)]
    [InlineData(0x08, 253)]
    [InlineData(0x08, 254)]
    [InlineData(0x08, 255)]
    public void CtaSelectionUnknownOrNotYetSupportedSvrIsWholeUnknown(byte tag, byte svr)
        => AssertSelectionUnknown(Cta([0x41, 16, .. SelectionData(tag, [svr])]));

    [Theory]
    [InlineData(129)]
    [InlineData(144)]
    [InlineData(145)]
    [InlineData(160)]
    public void CtaSelectionOrdinalCannotResolveToSvdOrDisplayIdOrAnotherTargetDomain(byte svr)
    {
        var bytes = Base(2); SetCta(bytes, 1, [0x41, 16, .. SelectionData(0x0D, [svr])]);
        SetDisplayId(bytes, 2, 0x20, 2, DidTiming(148499), 0, false);
        AssertSelectionUnknown(Checked(bytes));
    }

    [Theory]
    [InlineData(0x08)]
    [InlineData(0x0D)]
    public void CtaSelectionBlockRequiresAtLeastItsFirstSvr(byte tag)
        => AssertSelectionUnknown(Cta(SelectionData(tag, [])));

    [Theory]
    [InlineData(0x0D)]
    [InlineData(0x08)]
    public void CtaSelectionLastBadReferenceRejectsEarlierGoodDeclarations(byte tag)
    {
        var bytes = Base(2); SetCta(bytes, 1, SelectionData(0x0D, [16]));
        SetCta(bytes, 2, SelectionData(tag, tag == 0x0D ? [16, 130] : [130]));
        AssertSelectionUnknown(Checked(bytes));
    }

    [Theory]
    [InlineData(0x23)]
    [InlineData(0x2A)]
    public void CtaSelectionUnsupportedTimingBodyCannotBeSkippedToReachAnOtherwiseKnownTarget(byte tag)
        => AssertSelectionUnknown(Cta([.. SelectionData(0x0D, [16]), 0xE1, tag]));

    [Theory]
    [InlineData(0x0D)]
    [InlineData(0x08)]
    public void CtaSelectionExtentAndTailAreCheckedBeforePublishingAnyBlock(byte tag)
    {
        var bytes = Cta(SelectionData(tag, [16])); bytes[130]--; AssertSelectionUnknown(Checked(bytes));
        bytes = Cta(SelectionData(tag, [16])); bytes[254] = 1; AssertSelectionUnknown(Checked(bytes));
        bytes = Cta(SelectionData(tag, [16])); bytes[^1]++; AssertSelectionUnknown(bytes);
    }

    [Fact]
    public void CtaSelectionUsesFullNaturalVfpPayloadRatherThanAnInventedPreferenceLimit()
    {
        var codes = Enumerable.Repeat((byte)16, 30).ToArray();
        var selection = Assert.Single(Capability(Cta(SelectionData(0x0D, codes))).SelectionDeclarations);
        Assert.Equal(30, selection.References.Length); Assert.Equal(codes, selection.RawDescriptor.ToArray());
        Assert.Equal(35u, selection.References[^1].ByteOffset);
    }

    [Theory]
    [InlineData(1, 100)]
    [InlineData(129, 1000)]
    public void CtaNativeSelectionKeepsUnsignedLittleEndianCodesAndItsDeclaredUnit(byte flags, uint unit)
    {
        var raw = new byte[] { 16, flags, 0x34, 0x12, 0xDC, 0xFE };
        var selection = Assert.Single(Capability(Cta(SelectionData(0x08, raw))).SelectionDeclarations);
        Assert.Equal(flags, selection.Flags); Assert.Equal(raw, selection.RawDescriptor.ToArray());
        Assert.Equal(new EdidNativeImageSizeDeclaration(0x1234, 0xFEDC, unit), selection.ImageSize);
        Assert.Equal((1u, 6u, "cta-nvrdb", (byte)3), (selection.BlockIndex, selection.ByteOffset, selection.Encoding, selection.Revision));
        Assert.Equal(16u, Assert.Single(selection.References).Code); Assert.Null(Assert.Single(selection.References).Target);
    }

    [Fact]
    public void CtaNativeSelectionDistinguishesOmittedFlagsFromExplicitZeroAndUnusedUnitBit()
    {
        var capability = Capability(Cta([.. SelectionData(0x08, [16]), .. SelectionData(0x08, [16, 0]), .. SelectionData(0x08, [16, 128])]));
        Assert.Null(capability.SelectionDeclarations[0].Flags);
        Assert.Equal((byte)0, capability.SelectionDeclarations[1].Flags); Assert.Equal((byte)128, capability.SelectionDeclarations[2].Flags);
        Assert.All(capability.SelectionDeclarations, item => Assert.Null(item.ImageSize));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(8)]
    [InlineData(16)]
    [InlineData(32)]
    [InlineData(64)]
    public void CtaNativeSelectionDoesNotGuessReservedFlags(byte flags)
        => AssertSelectionUnknown(Cta(SelectionData(0x08, [16, flags])));

    [Theory]
    [InlineData(2, true)]
    [InlineData(3, true)]
    [InlineData(4, true)]
    [InlineData(5, true)]
    [InlineData(7, true)]
    [InlineData(3, false)]
    [InlineData(6, false)]
    [InlineData(30, false)]
    public void CtaNativeSelectionRequiresExactImageSizeExtentAndNeverTruncatesTail(int length, bool hasSize)
    {
        var payload = new byte[length]; payload[0] = 16; payload[1] = hasSize ? (byte)1 : (byte)0;
        if (length >= 6) { payload[2] = 1; payload[4] = 1; }
        AssertSelectionUnknown(Cta(SelectionData(0x08, payload)));
    }

    [Theory]
    [InlineData(1, 0, 1)]
    [InlineData(1, 1, 0)]
    [InlineData(129, 0, 1)]
    [InlineData(129, 1, 0)]
    public void CtaNativeSelectionImageSizeCodesMustBothBeNonzero(byte flags, byte width, byte height)
        => AssertSelectionUnknown(Cta(SelectionData(0x08, [16, flags, width, 0, height, 0])));

    [Fact]
    public void CtaSelectionFullHfChainResolvesForwardTargetsAndBadLastChecksumHasNoPrefix()
    {
        var bytes = Base(2); bytes[126] = 1;
        SetCta(bytes, 1, [0xE2, 0x78, 2, .. SelectionData(0x0D, [145])]); SetCta(bytes, 2, SelectionT7(148499));
        var selection = Assert.Single(Capability(bytes).SelectionDeclarations);
        Assert.Equal(9u, selection.ByteOffset); Assert.Equal(2u, Assert.Single(selection.References).Target!.BlockIndex);
        bytes[^1]++; AssertSelectionUnknown(bytes);
    }

    [Fact]
    public void CtaSelectionOpaquePrimaryPayloadRoundTripsTypedReferencesAndExactSizeCodes()
    {
        var bytes = Cta([.. SelectionT7(148349), .. SelectionData(0x0D, [145, 16, 145]),
            .. SelectionData(0x08, [145, 129, 255, 255, 254, 255])]);
        var path = DisplayEdidModeTestData.Path(WindowsMonitorEdidReader.CreateReadResult(bytes));
        var catalog = new NativeDisplayCoordinatorPayloadCatalog();
        var fact = Assert.Single(Assert.Single(NativeDisplayCoordinatorObservationProjector.Create([path], [], catalog, DateTimeOffset.UtcNow),
            batch => batch.Source == NativeDisplaySource.DisplayConfig).Facts);
        var restored = new NativeDisplayCoordinatorPayloadCatalog(); restored.Import(catalog.Export());
        var roundtrip = Assert.Single(NativeDisplayCoordinatorReadProjection.CreateDisplayPaths(
            [new NativeDisplayNodeOutput { Kind = (uint)NativeDisplayNodeKind.Connector, PrimaryPayloadHandle = fact.PayloadHandle }], restored)).Path;
        var expected = path.Edid!.ModeCapability!; var actual = roundtrip.Edid!.ModeCapability!;
        Assert.Equal(JsonSerializer.Serialize(expected), JsonSerializer.Serialize(actual));
        Assert.Equal(new EdidNativeImageSizeDeclaration(65535, 65534, 1000), actual.SelectionDeclarations[1].ImageSize);
        Assert.Equal(new byte[] { 145, 16, 145 }, actual.SelectionDeclarations[0].References.Select(item => item.ShortVideoReference));
        Assert.Equal(new EdidModeRate(1978, 33), Assert.Single(actual.Modes).Timing!.FrameRate);
    }

    [Fact]
    public void CtaSelectionLegacyMissingInitPropertyPreservesOriginalModesAndDoesNotReparseRaw()
    {
        var bytes = Cta(SelectionData(0x0D, [16])); Dtd().CopyTo(bytes, 54);
        var path = DisplayEdidModeTestData.Path(WindowsMonitorEdidReader.CreateReadResult(Checked(bytes)));
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var node = JsonSerializer.SerializeToNode(path, options)!.AsObject();
        Assert.True(node["edid"]!["modeCapability"]!.AsObject().Remove("selectionDeclarations"));
        var legacy = node.Deserialize<DeviceTopologyDisplayPath>(options)!;
        Assert.Empty(legacy.Edid!.ModeCapability!.SelectionDeclarations); Assert.False(legacy.Edid.ModeCapability.SelectionDeclarations.IsDefault);
        Assert.Equal(path.EdidRawDeclaredBase64, legacy.EdidRawDeclaredBase64);
        Assert.Equal(JsonSerializer.Serialize(path.Edid!.ModeCapability!.Modes), JsonSerializer.Serialize(legacy.Edid.ModeCapability.Modes));
        var compatibility = new EdidModeCapability([], []); var (modes, ranges) = compatibility;
        Assert.Empty(modes); Assert.Empty(ranges); Assert.Empty(compatibility.SelectionDeclarations);
    }

    [Fact]
    public void CtaSelectionAloneChangesOriginalOpaqueAndDomainSemanticHashes()
    {
        var first = DisplayEdidModeTestData.Path(WindowsMonitorEdidReader.CreateReadResult(Cta(SelectionData(0x0D, [16, 4]))));
        var secondCapability = Capability(Cta(SelectionData(0x0D, [4, 16])));
        var second = first with { Edid = first.Edid! with { ModeCapability = secondCapability } };
        Assert.Equal(first.EdidRawDeclaredBase64, second.EdidRawDeclaredBase64);
        Assert.Equal(JsonSerializer.Serialize(first.Edid!.ModeCapability!.Modes), JsonSerializer.Serialize(secondCapability.Modes));
        Assert.Equal(JsonSerializer.Serialize(first.Edid.ModeCapability.Ranges), JsonSerializer.Serialize(secondCapability.Ranges));
        var catalog = new NativeDisplayCoordinatorPayloadCatalog(); Assert.NotEqual(catalog.AddDisplayPath(first), catalog.AddDisplayPath(second));
        var comparer = new DeviceTopologySemanticComparer();
        Assert.NotEqual(comparer.ComputeHash(DisplayEdidModeTestData.Snapshot(first)),
            comparer.ComputeHash(DisplayEdidModeTestData.Snapshot(second)));
    }

    private static byte[] SelectionData(byte tag, byte[] payload) => [checked((byte)(0xE1 + payload.Length)), tag, .. payload];
    private static byte[] SelectionT7(uint clock) => [0xF6, 0x22, 2, .. DidTiming(clock)];
    private static void AssertSelectionUnknown(byte[] bytes)
    {
        var read = WindowsMonitorEdidReader.CreateReadResult(bytes);
        Assert.True(read.Complete); Assert.NotNull(read.Capabilities); Assert.Null(read.Capabilities.ModeCapability);
        Assert.Equal(bytes, Convert.FromBase64String(read.RawEdidDeclaredBase64!));
    }
}
