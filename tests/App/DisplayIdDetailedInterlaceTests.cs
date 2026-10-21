using ResourceManager.ReadWrite.Windows.Devices.Display;
using static Resource_Manager_APP.Tests.DisplayIdCompactTimingTestData;

namespace Resource_Manager_APP.Tests;

public sealed class DisplayIdDetailedInterlaceTests
{
    // 74.25MHz, 1920+280 pixels, 1080+45 full-frame lines, vertical4/10.
    private static byte[] Timing() => [9, 0x22, 1, 0x94, 0x7F, 7, 0x17, 1, 0x57, 0x80,
        0x2B, 0, 0x37, 4, 0x2C, 0, 3, 0x80, 9, 0];

    [Theory]
    [InlineData((byte)0)]
    [InlineData((byte)1)]
    [InlineData((byte)2)]
    public void Structure20TypeVIIRevisionsUseTheDeclaredOddFrameTotal(byte revision)
    {
        var mode = Mode(Bytes(Timing(), header: revision, version: 0x20, tag: 0x22));
        Assert.Equal("displayid-vii", mode.Encoding);
        Assert.Equal((byte)0x20, mode.ContainerRevision);
        Assert.Equal(revision, mode.Revision);
        Verify(mode);
    }

    [Fact]
    public void CtaEncapsulatedTypeVIIUsesTheSameFullFrameDescriptor()
    {
        var mode = Mode(Cta(Timing()));
        Assert.Equal("cta-vtdb-vii", mode.Encoding);
        Assert.Equal((byte)3, mode.ContainerRevision);
        Assert.Equal((byte)2, mode.Revision);
        Verify(mode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NeitherContainerInventsAnExtraLineForAnEvenDeclaredFrameTotal(bool cta)
    {
        var d = Timing(); d[14] = 43;
        AssertUnknown(cta ? Cta(d) : Bytes(d, version: 0x20, tag: 0x22));
    }

    [Fact]
    public void OddActivePorchAndSyncArePreservedRatherThanRoundedIntoFields()
    {
        var d = Timing(); d[12] = 0x38; d[14] = 43; d[16] = 4; d[18] = 8;
        foreach (var bytes in new[] { Cta(d), Bytes(d, header: 2, version: 0x20, tag: 0x22) })
        {
            var timing = Mode(bytes).Timing!;
            Assert.Equal((1081u, 44u, 5u, 9u), (timing.VerticalActive, timing.VerticalBlanking,
                timing.VerticalSyncOffset, timing.VerticalSyncWidth));
            Assert.Equal(new EdidModeRate(30, 1), timing.FrameRate);
            Assert.Equal(new EdidModeRate(60, 1), timing.FieldRate);
        }
    }

    private static void Verify(EdidModeDeclaration mode)
    {
        var timing = mode.Timing!;
        Assert.Equal(74_250_000UL, timing.PixelClockHz);
        Assert.Equal((1080u, 45u), (timing.VerticalActive, timing.VerticalBlanking));
        Assert.Equal(new EdidModeRate(30, 1), timing.FrameRate);
        Assert.Equal(new EdidModeRate(60, 1), timing.FieldRate);
        Assert.Equal(Timing(), mode.RawDescriptor.ToArray());
    }

    private static byte[] Cta(byte[] descriptor)
    {
        var bytes = Bytes(); Array.Clear(bytes, 128, 128);
        bytes[128] = 2; bytes[129] = 3; bytes[130] = 27;
        bytes[132] = 0xF6; bytes[133] = 0x22; bytes[134] = 2;
        descriptor.CopyTo(bytes, 135); OuterChecksums(bytes);
        return bytes;
    }
}
