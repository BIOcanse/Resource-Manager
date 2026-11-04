using System.Collections.Immutable;

namespace ResourceManager.ReadWrite.Windows.Devices.Display;

/// <summary>Complete declarations in this registry EDID, not an operating-system mode list.</summary>
public sealed record EdidModeCapability(
    ImmutableArray<EdidModeDeclaration> Modes,
    ImmutableArray<EdidRangeDeclaration> Ranges)
{
    public byte EdidRevision { get; init; }
    public byte BaseFeatures { get; init; }
    public byte? CtaFlags { get; init; }
    public ImmutableArray<EdidModeSelectionDeclaration> SelectionDeclarations { get; init; } = [];
    public ImmutableArray<EdidDynamicTimingRangeDeclaration> DynamicTimingRanges { get; init; } = [];
    public ImmutableArray<EdidAdaptiveSyncRangeDeclaration> AdaptiveSyncRanges { get; init; } = [];
}

public sealed record EdidModeDeclaration(
    uint BlockIndex, uint ByteOffset, string Encoding, byte Revision,
    ImmutableArray<byte> RawDescriptor)
{
    public uint? Code { get; init; }
    /// <summary>Bit position within RawDescriptor, byte zero first and least-significant bit zero.</summary>
    public int? BitIndex { get; init; }
    public uint? Width { get; init; }
    public uint? Height { get; init; }
    /// <summary>Original short-timing aspect code; undefined is not an inferred pixel height.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public EdidAspectRatioDeclaration? AspectRatioDeclared { get; init; }
    public uint? HorizontalPixelRepetition { get; init; }
    public bool? Interlaced { get; init; }
    /// <summary>Nominal frames/s for progressive, fields/s for interlaced declarations.</summary>
    public uint? NominalVerticalRateHz { get; init; }
    public bool? Native { get; init; }
    public bool? Preferred { get; init; }
    public bool? YCbCr420Only { get; init; }
    public bool? YCbCr420Encoding { get; init; }
    public bool? YCbCr420Supported { get; init; }
    public EdidModeReference? YCbCr420CapabilityMap { get; init; }
    public byte? ContainerFlags { get; init; }
    public byte? ContainerRevision { get; init; }
    public bool? DscPassThrough { get; init; }
    public string? Formula { get; init; }
    public bool? ReducedBlanking { get; init; }
    public EdidDetailedTiming? Timing { get; init; }
    public EdidCvtTimingParameters? CvtParameters { get; init; }
    /// <summary>Type V fields explicitly listed by DisplayID 1.3 Table 4-26.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public EdidDisplayIdTypeVOptions? DisplayIdTypeVOptions { get; init; }
    /// <summary>Type VI data needed when its inconsistent high pixel-clock bit makes Timing unavailable.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public EdidDisplayIdTypeViTiming? DisplayIdTypeViTiming { get; init; }
    /// <summary>Optional Type VI aspect multiplier and vertical image size, kept as declared.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public EdidDisplayIdTypeViImageSize? DisplayIdTypeViImageSize { get; init; }
}

/// <summary>One original CTA selection block, in source order; not a computed winning mode.</summary>
public sealed record EdidModeSelectionDeclaration(
    uint BlockIndex, uint ByteOffset, string Encoding, byte Revision,
    ImmutableArray<byte> RawDescriptor, ImmutableArray<EdidModeSelectionReference> References)
{
    public byte? Flags { get; init; }
    public EdidNativeImageSizeDeclaration? ImageSize { get; init; }
}

public sealed record EdidModeSelectionReference(
    uint ByteOffset, byte ShortVideoReference, string TargetEncoding, uint? Code, EdidModeReference? Target);

/// <summary>Raw NVRDB image-size codes and their declared unit; not an observed panel size.</summary>
public sealed record EdidNativeImageSizeDeclaration(ushort WidthCode, ushort HeightCode, uint UnitMicrometers);

public sealed record EdidModeReference(uint BlockIndex, uint ByteOffset, ImmutableArray<byte> RawDescriptor, int? BitIndex);

/// <summary>Encoded geometry is preserved; VerticalActive is per field for EDID DTD, per frame for DisplayID.</summary>
public sealed record EdidDetailedTiming(
    ulong PixelClockHz,
    uint HorizontalActive, uint HorizontalBlanking, uint HorizontalSyncOffset, uint HorizontalSyncWidth,
    uint VerticalActive, uint VerticalBlanking, uint VerticalSyncOffset, uint VerticalSyncWidth,
    byte Flags, bool? PositiveHorizontalSync, bool? PositiveVerticalSync,
    EdidModeRate FrameRate, EdidModeRate? FieldRate);

/// <summary>Original CVT parameters. Version0 denotes standard blanking, versions1..3 reduced blanking; no timing is generated.</summary>
public sealed record EdidCvtTimingParameters(byte ReducedBlankingVersion, byte StereoCode)
{
    /// <summary>Additional refresh multiplied by1000/1001: Type IX bit4; Type X RB2 VR_HB only.</summary>
    public bool? AlternativeRefresh1000Over1001Supported { get; init; }
    /// <summary>RB3 VR_HB: true selects base160, false base80; no horizontal timing is synthesized.</summary>
    public bool? HorizontalBlankingBase160Pixels { get; init; }
    public bool? EarlyVsyncRequired { get; init; }
    /// <summary>RB3 original D6 bits4:2; absent D6 or other RB versions remain null.</summary>
    public byte? DeltaHorizontalBlankingCode { get; init; }
    /// <summary>RB3 original D6 bits7:5; original code, not a generated vertical interval.</summary>
    public byte? AdditionalVerticalBlankingCode { get; init; }
    /// <summary>Original D7.0 for every supported RB version: true selects300us/20us, false460us/35us.</summary>
    public bool? AlternativeMinimumVerticalBlanking { get; init; }
}

public sealed record EdidDisplayIdTypeVOptions(
    byte FormulaCode, byte StereoSupportCode, bool FractionalRefreshSupported);

/// <summary>
/// DisplayID 1.3 Type VI declared pixel clock and porch/sync values. PixelClockHz is null only
/// for the high clock bit that conflicts with the table's stated maximum; RawDescriptor remains complete.
/// </summary>
public sealed record EdidDisplayIdTypeViTiming(
    ulong? PixelClockHz,
    uint HorizontalBlankingPixels, uint HorizontalFrontPorchPixels, uint HorizontalSyncWidthPixels,
    uint VerticalBlankingLines, uint VerticalFrontPorchLines, uint VerticalSyncWidthLines,
    bool PositiveHorizontalSync, bool PositiveVerticalSync, byte StereoSupportCode);

/// <summary>Type VI's optional aspect multiplier and vertical image-size values in millimeters.</summary>
public sealed record EdidDisplayIdTypeViImageSize(
    byte AspectMultiplier, uint VerticalImageBaseSizeMillimeters,
    byte SizeMultiplier, uint VerticalImageSizeMillimeters);

/// <summary>Original aspect code and its explicit ratio; code8 declares an undefined ratio.</summary>
public sealed record EdidAspectRatioDeclaration(byte Code, uint? Horizontal, uint? Vertical);

public sealed record EdidModeRate(ulong Numerator, ulong Denominator);

public sealed record EdidRangeDeclaration(
    uint BlockIndex, uint ByteOffset, string Encoding, byte Revision, ImmutableArray<byte> RawDescriptor,
    string Formula, uint MinimumVerticalRateHz, uint MaximumVerticalRateHz,
    uint MinimumHorizontalFrequencyHz, uint MaximumHorizontalFrequencyHz, ulong? MaximumPixelClockHz)
{
    public EdidSecondaryGtfCurve? SecondaryGtf { get; init; }
    public EdidCvtRange? Cvt { get; init; }
}

public sealed record EdidSecondaryGtfCurve(
    uint StartHorizontalFrequencyHz, uint CTimesTwo, uint M, uint K, uint JTimesTwo);

public sealed record EdidCvtRange(
    byte Version, uint? MaximumActivePixelsPerLine, byte SupportedAspectRatiosMask,
    byte PreferredAspectRatioCode, bool StandardBlanking, bool ReducedBlanking,
    byte ScalingMask, uint? PreferredVerticalRateHz);
