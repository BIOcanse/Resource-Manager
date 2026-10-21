using ResourceManager.ReadWrite.Windows.Devices.Display;
using static Resource_Manager_APP.Tests.DisplayIdCompactTimingTestData;

namespace Resource_Manager_APP.Tests;

public sealed class DisplayIdStructureVersionTests
{
    [Fact]
    public void Document13TypeIIUsesStructure12AndRetainsTheActualWireByte()
    {
        // DisplayID1.3 section2.1/table2-5: version1, revision2, not0x13.
        var bytes = Bytes(version: 0x12);
        Assert.Equal(new byte[] { 0x70, 0x12, 14, 2, 0, 4, 0, 11 }, bytes[128..136]);
        var mode = Mode(bytes);
        Assert.Equal((byte)0x12, mode.ContainerRevision);
        Assert.Equal("displayid-ii", mode.Encoding);
        Assert.Equal(new EdidModeRate(203125, 2923), mode.Timing!.FrameRate);
        Assert.Equal(Descriptor(), mode.RawDescriptor.ToArray());
    }

    [Theory]
    [InlineData((byte)0x13)]
    [InlineData((byte)0x21)]
    public void ADocumentNumberIsNotASupportedEncodedStructureVersion(byte version)
    {
        // A valid progressive TypeI/VII payload must not hide the header error.
        byte[] timing = [0, 0x1D, 0, 0x84, 0x7F, 7, 0x17, 1, 0x57, 0x80,
            0x2B, 0, 0x37, 4, 0x2C, 0, 3, 0x80, 9, 0];
        AssertUnknown(Bytes(timing, version: version, tag: version < 0x20 ? (byte)3 : (byte)0x22));
    }

    [Fact]
    public void Document13TypeIInterlaceUsesStructure12AndAnOddDeclaredFrameTotal()
    {
        byte[] timing = [0, 0x1D, 0, 0x94, 0x7F, 7, 0x17, 1, 0x57, 0x80,
            0x2B, 0, 0x37, 4, 0x2C, 0, 3, 0x80, 9, 0];
        var mode = Mode(Bytes(timing, header: 1, version: 0x12, tag: 3));
        Assert.Equal((byte)0x12, mode.ContainerRevision);
        Assert.Equal(new EdidModeRate(30, 1), mode.Timing!.FrameRate);
        Assert.Equal(new EdidModeRate(60, 1), mode.Timing.FieldRate);
        timing[14] = 43;
        AssertUnknown(Bytes(timing, header: 1, version: 0x12, tag: 3));
    }

    [Fact]
    public void Document13PackageInputsContainTheNormativeHeaderAndBothChecksums()
    {
        foreach (var typeI in new[] { false, true })
        {
            var bytes = ResourceManager.TestFixtures.DisplayIdCompactTimingFixture.Bytes(typeI);
            Assert.Equal((byte)0x12, bytes[129]);
            Assert.Equal(0, bytes[129..(134 + bytes[130])].Sum(b => b) & 255);
            Assert.Equal(0, bytes[128..].Sum(b => b) & 255);
            Assert.Equal((byte)0x12, Mode(bytes).ContainerRevision);
        }
    }
}
