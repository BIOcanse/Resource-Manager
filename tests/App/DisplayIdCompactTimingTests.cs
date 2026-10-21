using System.Text.Json;
using ResourceManager.ReadWrite.Windows.Devices.Display;
using static Resource_Manager_APP.Tests.DisplayIdCompactTimingTestData;

namespace Resource_Manager_APP.Tests;

public sealed class DisplayIdCompactTimingTests
{
    [Fact]
    public void NormativeByteTenAndByteThreeResolveBothIndependentParserConflicts()
    {
        var input = Bytes(); var mode = Mode(input); var timing = mode.Timing!;
        Assert.Equal("displayid-ii", mode.Encoding); Assert.Equal(1u, mode.BlockIndex); Assert.Equal(8u, mode.ByteOffset);
        Assert.Equal((byte)0, mode.Revision); Assert.Equal((byte)0, mode.ContainerFlags); Assert.Equal((byte)0x12, mode.ContainerRevision);
        Assert.Equal((1024u, 768u), (mode.Width, mode.Height)); Assert.False(mode.Interlaced); Assert.True(mode.Preferred);
        Assert.Equal(new EdidDetailedTiming(65_000_000, 1024, 160, 48, 32, 768, 22, 3, 5, 0x8C, true, true,
            new(203125, 2923), null), timing);
        input[139] = 0;
        Assert.Equal(Descriptor(), mode.RawDescriptor.ToArray());
        Assert.Null(mode.AspectRatioDeclared); Assert.Null(mode.NominalVerticalRateHz); Assert.Null(mode.DscPassThrough);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(8)]
    [InlineData(12)]
    public void SyncPolaritiesComeOnlyFromTheDescriptorOptions(byte flags)
    {
        var d = Descriptor(); d[3] = flags; var mode = Mode(Bytes(d));
        Assert.Equal((flags & 8) != 0, mode.Timing!.PositiveHorizontalSync);
        Assert.Equal((flags & 4) != 0, mode.Timing.PositiveVerticalSync);
        Assert.False(mode.Preferred);
    }

    [Fact]
    public void InterlacePreservesFrameValuesOddTotalAndExactHalfLineRates()
    {
        var d = Descriptor(); d[3] |= 16; d[9] = 32;
        var mode = Mode(Bytes(d)); var timing = mode.Timing!;
        Assert.True(mode.Interlaced); Assert.Equal(768u, mode.Height);
        Assert.Equal((768u, 33u, 3u, 5u), (timing.VerticalActive, timing.VerticalBlanking, timing.VerticalSyncOffset, timing.VerticalSyncWidth));
        Assert.Equal(new EdidModeRate(2031250, 29637), timing.FrameRate);
        Assert.Equal(new EdidModeRate(4062500, 29637), timing.FieldRate);
        d[9] = 33; AssertUnknown(Bytes(d));
    }

    [Fact]
    public void MinimumAndMaximumCodesKeepFullClockAndGeometryWithoutOverflow()
    {
        var minimum = Mode(Bytes([0, 0, 0, 0, 0, 2, 0, 0, 0, 1, 0]));
        Assert.Equal(new EdidDetailedTiming(10_000, 8, 16, 8, 8, 1, 2, 1, 1, 0, false, false,
            new(1250, 9), null), minimum.Timing);
        var maximum = Mode(Bytes([255, 255, 255, 0x8C, 255, 255, 255, 255, 15, 255, 255]));
        Assert.Equal(167_772_160_000UL, maximum.Timing!.PixelClockHz);
        Assert.Equal((4096u, 1024u, 128u, 128u), (maximum.Width, maximum.Timing.HorizontalBlanking,
            maximum.Timing.HorizontalSyncOffset, maximum.Timing.HorizontalSyncWidth));
        Assert.Equal((4096u, 256u, 16u, 16u), (maximum.Height, maximum.Timing.VerticalBlanking,
            maximum.Timing.VerticalSyncOffset, maximum.Timing.VerticalSyncWidth));
    }

    [Fact]
    public void EveryDescriptorIncludingDuplicatesRetainsOrderOffsetAndOwnedRawBytes()
    {
        var payload = Enumerable.Range(0, 10).SelectMany(_ => Descriptor()).ToArray();
        var bytes = Bytes(payload); var modes = EdidParser.Parse(bytes)!.ModeCapability!.Modes;
        Assert.Equal(10, modes.Length); Assert.Equal(107u, modes[^1].ByteOffset);
        Assert.Equal(payload, modes.SelectMany(m => m.RawDescriptor).ToArray());
        Assert.All(modes, m => Assert.Equal(3u, m.Timing!.VerticalSyncOffset));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(32)]
    [InlineData(64)]
    public void DefinedStereoFlagsArePreservedWithoutInventingAdditionalMetadata(byte stereo)
    {
        var d = Descriptor(); d[3] = checked((byte)(0x8C | stereo)); var mode = Mode(Bytes(d));
        Assert.Equal(d[3], mode.Timing!.Flags); Assert.Equal(Descriptor()[4..], mode.RawDescriptor.ToArray()[4..]);
    }

    [Fact]
    public void ReservedDescriptorBitsAndImpossibleSyncIntervalsRejectTheWholeValue()
    {
        foreach (var (index, value) in new (int, byte)[] { (3, 1), (3, 2), (3, 0x60), (8, 0x10), (8, 0x80), (5, 0), (9, 0) })
        {
            var d = Descriptor(); d[index] = value;
            AssertUnknown(Bytes([.. Descriptor(), .. d]));
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(10)]
    [InlineData(12)]
    [InlineData(21)]
    public void EmptyAndTruncatedDescriptorListsNeverPublishAGoodPrefix(int length)
    {
        var payload = new byte[length]; Descriptor().AsSpan(0, Math.Min(length, 11)).CopyTo(payload);
        AssertUnknown(Bytes(payload));
    }

    [Fact]
    public void OnlyTheNormativelyReviewedVersionAndHeaderAreEnabled()
    {
        foreach (var version in new byte[] { 0x13, 0x20, 0x21 }) AssertUnknown(Bytes(version: version));
        for (var header = 1; header <= 255; header++) AssertUnknown(Bytes(header: checked((byte)header)));
    }

    [Fact]
    public void BadInnerOuterExtentAndUnknownTailCannotEscapeWholeChainValidation()
    {
        var inner = Bytes(); inner[140] ^= 1; OuterChecksums(inner); AssertUnknown(inner);
        var outer = Bytes(); outer[255] ^= 1; AssertUnknown(outer);
        var extent = Bytes(); extent[135] = 12; Recheck(extent); AssertUnknown(extent);
        var unknownTail = Bytes([.. Descriptor(), .. Descriptor()]); unknownTail[147] = 0x7F;
        unknownTail[148] = 0; unknownTail[149] = 8; unknownTail[135] = 11; Recheck(unknownTail); AssertUnknown(unknownTail);
    }

    [Fact]
    public void JsonRoundTripPreservesDeclaredTimingAndOriginalRawByValue()
    {
        var mode = Mode(Bytes()); var restored = JsonSerializer.Deserialize<EdidModeDeclaration>(JsonSerializer.Serialize(mode))!;
        Assert.Equal(mode.Timing, restored.Timing); Assert.Equal(mode.RawDescriptor.ToArray(), restored.RawDescriptor.ToArray());
        Assert.Equal((mode.Encoding, mode.Width, mode.Height, mode.ContainerRevision),
            (restored.Encoding, restored.Width, restored.Height, restored.ContainerRevision));
    }

    [Theory]
    [InlineData(1080u, 45u, 4u)]
    [InlineData(1081u, 44u, 5u)]
    public void TypeI13UsesDeclaredOddFrameTotalWithoutExtraLineOrEvenPorchRestriction(uint active, uint blanking, uint porch)
    {
        var mode = Mode(TypeI(active, blanking, porch)); var timing = mode.Timing!;
        Assert.Equal(active, mode.Height); Assert.Equal(blanking, timing.VerticalBlanking); Assert.Equal(porch, timing.VerticalSyncOffset);
        Assert.Equal(new EdidModeRate(30, 1), timing.FrameRate); Assert.Equal(new EdidModeRate(60, 1), timing.FieldRate);
    }

    [Fact]
    public void TypeI13RejectsEvenTotalInsteadOfSilentlyAddingALine()
        => AssertUnknown(TypeI(1080, 44, 4));

    private static byte[] TypeI(uint active, uint blanking, uint porch)
    {
        // 74.25MHz, 1920+280 pixels, frame-based 1080+45 (or1081+44) lines.
        byte[] d = [0, 0x1D, 0, 0x94, 0x7F, 7, 0x17, 1, 0x57, 0x80, 0x2B, 0,
            0x37, 4, 0x2C, 0, 3, 0x80, 9, 0];
        d[12] = checked((byte)((active - 1) & 255)); d[13] = checked((byte)((active - 1) >> 8));
        d[14] = checked((byte)(blanking - 1)); d[16] = checked((byte)(porch - 1));
        return Bytes(d, header: 1, tag: 3);
    }
}
