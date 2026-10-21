using System.Text.Json;
using ResourceManager.ReadWrite.Windows.Devices;
using ResourceManager.ReadWrite.Windows.Devices.Display;
using ResourceManager.TestFixtures;

namespace Resource_Manager_APP.Tests;

public sealed class DisplayIdDynamicRangeTests
{
    [Fact]
    public void DynamicRangeUsesNormativeClockCodesAndTenBitMaximumWithoutInventedModes()
    {
        var value = Capability(DisplayIdDynamicRangeFixture.Bytes());
        var range = Assert.Single(value.DynamicTimingRanges);
        Assert.Empty(value.Modes); Assert.Empty(value.Ranges);
        Assert.Equal(60_000_000UL, range.MinimumPixelClockHz); Assert.Equal(600_000_000UL, range.MaximumPixelClockHz);
        Assert.Equal(48u, range.MinimumVerticalRateHz); Assert.Equal(360u, range.MaximumVerticalRateHz);
        Assert.Equal(1u, range.BlockIndex); Assert.Equal(8u, range.ByteOffset);
        Assert.Equal((byte)1, range.Revision); Assert.Equal((byte)1, range.ContainerFlags); Assert.Equal((byte)0x20, range.ContainerRevision);
        Assert.True(range.SeamlessWithFixedHorizontalPixelRateAndDynamicVerticalBlanking);
        Assert.Equal(DisplayIdDynamicRangeFixture.Payload(), range.RawDescriptor.ToArray());
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(0xFFFFFF, 255)]
    public void DynamicRangeClockBoundariesAndZeroVerticalRatesAreLiteral(uint code, byte rate)
    {
        var p = new byte[9];
        p[0] = p[3] = (byte)(code & 255); p[1] = p[4] = (byte)((code >> 8) & 255);
        p[2] = p[5] = (byte)(code >> 16); p[6] = p[7] = rate;
        var range = Assert.Single(Capability(DisplayIdDynamicRangeFixture.Bytes(p, flags: 0)).DynamicTimingRanges);
        Assert.Equal(((ulong)code + 1) * 1000, range.MinimumPixelClockHz);
        Assert.Equal(range.MinimumPixelClockHz, range.MaximumPixelClockHz);
        Assert.Equal((uint)rate, range.MinimumVerticalRateHz); Assert.Equal((uint)rate, range.MaximumVerticalRateHz);
        Assert.False(range.SeamlessWithFixedHorizontalPixelRateAndDynamicVerticalBlanking);
    }

    [Theory]
    [InlineData(0, 255)]
    [InlineData(1, 511)]
    [InlineData(2, 767)]
    [InlineData(3, 1023)]
    public void RevisionOneMaximumUsesOnlyTwoHighBits(byte high, uint expected)
    {
        var p = DisplayIdDynamicRangeFixture.Payload(); p[7] = 255; p[8] = high;
        Assert.Equal(expected, Assert.Single(Capability(DisplayIdDynamicRangeFixture.Bytes(p)).DynamicTimingRanges).MaximumVerticalRateHz);
    }

    [Fact]
    public void RevisionZeroDoesNotReinterpretTheFlagsAsExtraFrequencyBits()
    {
        var p = DisplayIdDynamicRangeFixture.Payload(); p[8] = 128;
        var range = Assert.Single(Capability(DisplayIdDynamicRangeFixture.Bytes(p, flags: 0)).DynamicTimingRanges);
        Assert.Equal(104u, range.MaximumVerticalRateHz); Assert.True(range.SeamlessWithFixedHorizontalPixelRateAndDynamicVerticalBlanking);
        p[8] |= 1; AssertUnknown(DisplayIdDynamicRangeFixture.Bytes(p, flags: 0));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(0x81)]
    public void UnknownRevisionOrHeaderReservedBitsNeverPublishAValue(byte flags)
        => AssertUnknown(DisplayIdDynamicRangeFixture.Bytes(flags: flags));

    [Theory]
    [InlineData(4)]
    [InlineData(8)]
    [InlineData(16)]
    [InlineData(32)]
    [InlineData(64)]
    public void ReservedPayloadFlagsAreNotIgnored(byte flag)
    {
        var p = DisplayIdDynamicRangeFixture.Payload(); p[8] |= flag;
        AssertUnknown(DisplayIdDynamicRangeFixture.Bytes(p));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(8)]
    [InlineData(10)]
    public void PayloadRequiresAllNineBytesAndNoUnknownTail(int length)
        => AssertUnknown(DisplayIdDynamicRangeFixture.Bytes(new byte[length]));

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ReversedClockOrVerticalBoundsCannotDescribeAValidRange(bool clock)
    {
        var p = DisplayIdDynamicRangeFixture.Payload();
        p[8] = 128;
        if (clock) { p[0] = p[1] = p[2] = 255; } else p[6] = 200;
        AssertUnknown(DisplayIdDynamicRangeFixture.Bytes(p, flags: 0));
    }

    [Theory]
    [InlineData((byte)0x12)]
    [InlineData((byte)0x13)]
    [InlineData((byte)0x21)]
    public void LegacyAndLaterContainersDoNotInheritVersionTwoDynamicRange(byte version)
        => AssertUnknown(DisplayIdDynamicRangeFixture.Bytes(version: version));

    [Fact]
    public void DynamicRangeIsRestrictedToTheBaseDisplayIdSection()
        => AssertUnknown(DisplayIdDynamicRangeFixture.Bytes(continuation: true));

    [Fact]
    public void DuplicateDeclarationsKeepPhysicalOrderAndRawBytes()
    {
        var ranges = Capability(DisplayIdDynamicRangeFixture.Bytes(duplicate: true)).DynamicTimingRanges;
        Assert.Equal(new uint[] { 8, 20 }, ranges.Select(static value => value.ByteOffset));
        Assert.All(ranges, range => Assert.Equal(DisplayIdDynamicRangeFixture.Payload(), range.RawDescriptor.ToArray()));
    }

    [Theory]
    [InlineData(145)]
    [InlineData(255)]
    public void EitherChecksumFailureRejectsTheWholeModeCapability(int offset)
    {
        var bytes = DisplayIdDynamicRangeFixture.Bytes(); bytes[offset]++;
        Assert.Null(EdidParser.Parse(bytes)!.ModeCapability);
    }

    [Fact]
    public void LaterBadRangeCannotPublishTheValidPrefix()
    {
        var bytes = DisplayIdDynamicRangeFixture.Bytes(duplicate: true); bytes[156] |= 4;
        DisplayIdDynamicRangeFixture.Checksums(bytes); AssertUnknown(bytes);
    }

    [Fact]
    public void DynamicRangeSurvivesJsonRoundTripWithoutChangingTheOriginalConstructor()
    {
        var value = Capability(DisplayIdDynamicRangeFixture.Bytes());
        var restored = JsonSerializer.Deserialize<EdidModeCapability>(JsonSerializer.Serialize(value))!;
        Assert.Equal(value.DynamicTimingRanges[0].RawDescriptor.ToArray(), restored.DynamicTimingRanges[0].RawDescriptor.ToArray());
        Assert.Equal(360u, Assert.Single(restored.DynamicTimingRanges).MaximumVerticalRateHz);
        Assert.Empty(new EdidModeCapability([], []).DynamicTimingRanges);
    }

    private static EdidModeCapability Capability(byte[] bytes)
        => Assert.IsType<EdidModeCapability>(EdidParser.Parse(bytes)!.ModeCapability);

    private static void AssertUnknown(byte[] bytes)
    {
        var read = WindowsMonitorEdidReader.CreateReadResult(bytes);
        Assert.True(read.Complete); Assert.NotNull(read.Capabilities); Assert.Null(read.Capabilities.ModeCapability);
        Assert.Equal(Convert.ToBase64String(bytes), read.RawEdidDeclaredBase64);
    }
}
