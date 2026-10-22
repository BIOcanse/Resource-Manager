using System.Text.Json;
using ResourceManager.App.Infrastructure.DeviceTopology;
using ResourceManager.App.Infrastructure.DeviceTopology.Snapshots;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.ReadWrite.Windows.Devices.Display;

namespace Resource_Manager_APP.Tests;

public sealed partial class EdidModeCapabilityParserTests
{
    private static readonly JsonSerializerOptions TypeVViWeb = new(JsonSerializerDefaults.Web);

    [Theory]
    [InlineData((byte)0, (byte)0)]
    [InlineData((byte)1, (byte)2)]
    public void TypeVUsesOnlyTable426FieldsAndKeepsItsUnlistedByteRaw(byte formula, byte stereo)
    {
        var options = (byte)(0x80 | (stereo << 5) | 0x10 | formula);
        var raw = DisplayEdidTypeVViTestData.TypeVDescriptor(options, 0xE7, 1920, 1080, 60);
        var mode = Assert.Single(Capability(DisplayEdidTypeVViTestData.TypeVBytes(raw)).Modes);
        Assert.Equal("displayid-v", mode.Encoding);
        Assert.Equal((1920u, 1080u, 60u), (mode.Width!.Value, mode.Height!.Value, mode.NominalVerticalRateHz!.Value));
        Assert.True(mode.Preferred);
        Assert.Equal("cvt", mode.Formula);
        Assert.True(mode.ReducedBlanking);
        Assert.Null(mode.Timing);
        Assert.Null(mode.CvtParameters);
        Assert.Equal(new EdidDisplayIdTypeVOptions(formula, stereo, true), mode.DisplayIdTypeVOptions);
        Assert.Equal(raw, mode.RawDescriptor.ToArray());
        Assert.Equal((byte)0xE7, mode.RawDescriptor[1]);
        Assert.Equal(1u, mode.BlockIndex);
        Assert.Equal(8u, mode.ByteOffset);
        Assert.Equal((byte)0, mode.Revision);
        Assert.Equal((byte)0, mode.ContainerFlags);
        Assert.Equal((byte)0x12, mode.ContainerRevision);
        var wire = JsonSerializer.SerializeToElement(mode, TypeVViWeb);
        Assert.Equal((byte)0xE7, wire.GetProperty("rawDescriptor")[1].GetByte());
        Assert.Equal(formula, wire.GetProperty("displayIdTypeVOptions").GetProperty("formulaCode").GetByte());
        Assert.Equal(stereo, wire.GetProperty("displayIdTypeVOptions").GetProperty("stereoSupportCode").GetByte());
    }

    [Fact]
    public void TypeVDecodesFullUnsignedActiveRangeWithoutSyntheticTiming()
    {
        var raw = DisplayEdidTypeVViTestData.TypeVDescriptor(width: 65_536, height: 65_536, refreshHz: 256);
        var mode = Assert.Single(Capability(DisplayEdidTypeVViTestData.TypeVBytes(raw)).Modes);
        Assert.Equal((65_536u, 65_536u, 256u), (mode.Width!.Value, mode.Height!.Value, mode.NominalVerticalRateHz!.Value));
        Assert.Null(mode.Interlaced);
        Assert.Null(mode.Timing);
    }

    [Theory]
    [InlineData(0xD2)] // Reserved formula code.
    [InlineData(0xDC)] // Reserved options bits.
    [InlineData(0xF1)] // Reserved stereo code.
    public void TypeVReservedOptionsRejectOnlyTheModeCapability(byte options)
    {
        var bytes = DisplayEdidTypeVViTestData.TypeVBytes(DisplayEdidTypeVViTestData.TypeVDescriptor(options));
        var read = WindowsMonitorEdidReader.CreateReadResult(bytes);
        Assert.True(read.Complete);
        Assert.Null(read.Capabilities!.ModeCapability);
        Assert.Equal(bytes, Convert.FromBase64String(read.RawEdidDeclaredBase64!));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(6)]
    [InlineData(8)]
    public void TypeVRequiresACompletePositiveSevenBytePayload(int descriptorBytes)
    {
        var payload = new byte[descriptorBytes];
        var read = WindowsMonitorEdidReader.CreateReadResult(DisplayEdidTypeVViTestData.TypeVBytes(payload));
        Assert.Null(read.Capabilities!.ModeCapability);
        Assert.NotNull(read.RawEdidDeclaredBase64);
    }

    [Fact]
    public void TypeVAndTypeViShareTheOriginalOrderedModeOwnerAndRoundTripAsTypedJson()
    {
        var bytes = DisplayEdidTypeVViTestData.Bytes();
        var capability = Capability(bytes);
        Assert.Equal(2, capability.Modes.Length);
        Assert.Equal(new[] { "displayid-v", "displayid-vi" }, capability.Modes.Select(static mode => mode.Encoding));
        Assert.Equal(bytes, Convert.FromBase64String(WindowsMonitorEdidReader.CreateReadResult(bytes).RawEdidDeclaredBase64!));
        DisplayEdidTypeVViTestData.AssertValue(JsonSerializer.SerializeToElement(capability, TypeVViWeb));
        var restored = JsonSerializer.Deserialize<EdidModeCapability>(JsonSerializer.Serialize(capability, TypeVViWeb), TypeVViWeb)!;
        Assert.Equal(JsonSerializer.Serialize(capability, TypeVViWeb), JsonSerializer.Serialize(restored, TypeVViWeb));
        Assert.Equal(capability.Modes[0].DisplayIdTypeVOptions, restored.Modes[0].DisplayIdTypeVOptions);
        Assert.Equal(capability.Modes[1].DisplayIdTypeViTiming, restored.Modes[1].DisplayIdTypeViTiming);
        Assert.Equal(capability.Modes[1].DisplayIdTypeViImageSize, restored.Modes[1].DisplayIdTypeViImageSize);
    }

    [Fact]
    public void TypeViWalksFourteenAndSeventeenByteDescriptorsFromEachRecordFlag()
    {
        var first = DisplayEdidTypeVViTestData.TypeViDescriptor(includeAspectAndSize: false);
        var second = DisplayEdidTypeVViTestData.TypeViDescriptor(includeAspectAndSize: true);
        var capability = Capability(DisplayEdidTypeVViTestData.TypeViBytes(first, second));
        Assert.Equal(2, capability.Modes.Length);
        Assert.Equal(new uint[] { 8, 22 }, capability.Modes.Select(static mode => mode.ByteOffset));
        Assert.Equal(new[] { 14, 17 }, capability.Modes.Select(static mode => mode.RawDescriptor.Length));
        Assert.Null(capability.Modes[0].AspectRatioDeclared);
        Assert.Null(capability.Modes[0].DisplayIdTypeViImageSize);
        Assert.NotNull(capability.Modes[1].AspectRatioDeclared);
        Assert.NotNull(capability.Modes[1].DisplayIdTypeViImageSize);
        Assert.Equal(148_500_000UL, capability.Modes[0].Timing!.PixelClockHz);
        Assert.Equal(new EdidModeRate(60, 1), capability.Modes[0].Timing.FrameRate);
        Assert.Null(capability.Modes[0].Timing.FieldRate);
        Assert.Equal(44u, capability.Modes[0].Timing.HorizontalSyncWidth);
        Assert.Equal(5u, capability.Modes[0].Timing.VerticalSyncWidth);
        Assert.Equal((byte)1, capability.Modes[0].DisplayIdTypeViTiming!.StereoSupportCode);
    }

    [Theory]
    [InlineData(0x000000u, 1_000UL)]
    [InlineData(0x1FFFFFu, 2_097_152_000UL)]
    public void TypeViDefinedClockCodesUseOneKilohertzIncrements(uint code, ulong expectedHz)
    {
        var mode = Assert.Single(Capability(DisplayEdidTypeVViTestData.TypeViBytes(
            DisplayEdidTypeVViTestData.TypeViDescriptor(code))).Modes);
        Assert.Equal((ulong?)expectedHz, mode.Timing!.PixelClockHz);
        Assert.Equal((ulong?)expectedHz, mode.DisplayIdTypeViTiming!.PixelClockHz);
    }

    [Fact]
    public void TypeViOnlyWithholdsClockAndRatesForTheContradictoryTwentyFirstBit()
    {
        var descriptor = DisplayEdidTypeVViTestData.TypeViDescriptor(0x200000, interlaced: true);
        var bytes = DisplayEdidTypeVViTestData.TypeViBytes(descriptor);
        var mode = Assert.Single(Capability(bytes).Modes);
        Assert.Equal((1920u, 1080u), (mode.Width!.Value, mode.Height!.Value));
        Assert.True(mode.Interlaced);
        Assert.Null(mode.Timing);
        Assert.Null(mode.DisplayIdTypeViTiming!.PixelClockHz);
        Assert.Equal(280u, mode.DisplayIdTypeViTiming.HorizontalBlankingPixels);
        Assert.Equal(88u, mode.DisplayIdTypeViTiming.HorizontalFrontPorchPixels);
        Assert.Equal(44u, mode.DisplayIdTypeViTiming.HorizontalSyncWidthPixels);
        Assert.Equal(45u, mode.DisplayIdTypeViTiming.VerticalBlankingLines);
        Assert.Equal(4u, mode.DisplayIdTypeViTiming.VerticalFrontPorchLines);
        Assert.Equal(5u, mode.DisplayIdTypeViTiming.VerticalSyncWidthLines);
        Assert.Equal(descriptor, mode.RawDescriptor.ToArray());
        var wire = JsonSerializer.SerializeToElement(mode, TypeVViWeb);
        Assert.Equal(JsonValueKind.Null, wire.GetProperty("timing").ValueKind);
        Assert.Equal(JsonValueKind.Null, wire.GetProperty("displayIdTypeViTiming").GetProperty("pixelClockHz").ValueKind);
        Assert.Equal(descriptor, wire.GetProperty("rawDescriptor").EnumerateArray().Select(static item => item.GetByte()));
    }

    [Theory]
    [InlineData(0)] // Horizontal active flags byte reserved bit 6.
    [InlineData(1)] // Vertical active flags byte reserved bit 6.
    [InlineData(2)] // Reserved Type VI timing-flags bit 4.
    [InlineData(3)] // Reserved stereo code.
    public void TypeViReservedBitsClearTheWholeCapabilityWithoutDroppingRaw(int field)
    {
        var descriptor = DisplayEdidTypeVViTestData.TypeViDescriptor();
        if (field == 0) descriptor[4] |= 0x40;
        else if (field == 1) descriptor[6] |= 0x40;
        else if (field == 2) descriptor[13] |= 0x10;
        else descriptor[13] = (byte)((descriptor[13] & 0x9F) | 0x60);
        var bytes = DisplayEdidTypeVViTestData.TypeViBytes(descriptor);
        var read = WindowsMonitorEdidReader.CreateReadResult(bytes);
        Assert.Null(read.Capabilities!.ModeCapability);
        Assert.Equal(bytes, Convert.FromBase64String(read.RawEdidDeclaredBase64!));
    }

    [Fact]
    public void TypeViBadFinalStrideOrPorchDoesNotPublishAnEarlierModePrefix()
    {
        var first = DisplayEdidTypeVViTestData.TypeViDescriptor();
        var shortTail = DisplayEdidTypeVViTestData.TypeViBytes(first.Concat(new byte[] { 1 }).ToArray());
        Assert.Null(EdidParser.Parse(shortTail)!.ModeCapability);
        var invalidPorch = DisplayEdidTypeVViTestData.TypeViDescriptor();
        invalidPorch[12] = 250;
        var bytes = DisplayEdidTypeVViTestData.TypeViBytes(first, invalidPorch);
        DisplayEdidTypeVViTestData.RepairChecksums(bytes);
        Assert.Null(EdidParser.Parse(bytes)!.ModeCapability);
    }

    [Fact]
    public void TypeViAnAspectFlagWithoutItsWholeOptionalTailIsUnavailable()
    {
        var descriptor = DisplayEdidTypeVViTestData.TypeViDescriptor();
        descriptor[2] |= 0x40;
        var bytes = DisplayEdidTypeVViTestData.TypeViBytes(descriptor);
        Assert.Null(EdidParser.Parse(bytes)!.ModeCapability);
    }

    [Fact]
    public void TypeViInterlaceDeclarationDoesNotInventAFieldRate()
    {
        var mode = Assert.Single(Capability(DisplayEdidTypeVViTestData.TypeViBytes(
            DisplayEdidTypeVViTestData.TypeViDescriptor(interlaced: true))).Modes);
        Assert.True(mode.Interlaced);
        Assert.Equal(new EdidModeRate(60, 1), mode.Timing!.FrameRate);
        Assert.Null(mode.Timing.FieldRate);
    }

    [Fact]
    public void TypeVViPropertiesCrossTheOriginalOpaquePayloadAndSemanticHash()
    {
        var path = DisplayEdidModeTestData.Path(WindowsMonitorEdidReader.CreateReadResult(
            DisplayEdidTypeVViTestData.Bytes()));
        var capability = path.Edid!.ModeCapability!;
        var changed = capability.Modes[0] with
        {
            DisplayIdTypeVOptions = capability.Modes[0].DisplayIdTypeVOptions! with
            {
                FractionalRefreshSupported = false
            }
        };
        var other = path with
        {
            Edid = path.Edid! with
            {
                ModeCapability = capability with { Modes = capability.Modes.SetItem(0, changed) }
            }
        };
        Assert.Equal(path.EdidRawDeclaredBase64, other.EdidRawDeclaredBase64);
        var catalog = new NativeDisplayCoordinatorPayloadCatalog();
        Assert.NotEqual(catalog.AddDisplayPath(path), catalog.AddDisplayPath(other));
        var comparer = new DeviceTopologySemanticComparer();
        Assert.NotEqual(comparer.ComputeHash(DisplayEdidModeTestData.Snapshot(path)),
            comparer.ComputeHash(DisplayEdidModeTestData.Snapshot(other)));
    }

    [Fact]
    public void LegacyModeShapeOmitsNewNullTypeVViProperties()
    {
        var json = JsonSerializer.Serialize(new EdidModeDeclaration(1, 8, "legacy", 0, [1]), TypeVViWeb);
        Assert.DoesNotContain("displayIdTypeVOptions", json);
        Assert.DoesNotContain("displayIdTypeViTiming", json);
        Assert.DoesNotContain("displayIdTypeViImageSize", json);
    }
}
