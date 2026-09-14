using ResourceManager.App.Infrastructure.DeviceTopology;

namespace Resource_Manager_APP.Tests;

public sealed class EdidParserTests
{
    [Fact]
    public void Parse_ReportsBitDepthDisplayPortSizeAndHdrEotfs()
    {
        var edid = new byte[256];
        new byte[] { 0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x00 }.CopyTo(edid, 0);
        edid[18] = 1;
        edid[19] = 4;
        edid[20] = 0xB5;
        edid[21] = 60;
        edid[22] = 34;
        WriteTextDescriptor(edid, 54, 0xFC, "P275MS PLUS");
        edid[126] = 1;
        edid[128] = 0x02;
        edid[130] = 8;
        edid[132] = 0xE3;
        edid[133] = 0x06;
        edid[134] = 0x0C;
        edid[135] = 0x01;

        var result = EdidParser.Parse(edid);

        Assert.NotNull(result);
        Assert.Equal("EDID 1.4", result.Version);
        Assert.Equal("P275MS PLUS", result.ProductName);
        Assert.Equal(10u, result.BitsPerColorChannel);
        Assert.Equal("DisplayPort", result.DigitalInterface);
        Assert.Equal("HDR10 / PQ / HLG", result.HdrFormats);
        Assert.Equal(600u, result.WidthMillimeters);
        Assert.Equal(340u, result.HeightMillimeters);
        Assert.Null(result.PanelTechnology);
    }

    [Fact]
    public void Parse_RejectsNonEdidPayload()
    {
        Assert.Null(EdidParser.Parse(new byte[128]));
    }

    private static void WriteTextDescriptor(byte[] edid, int offset, byte type, string value)
    {
        edid[offset + 3] = type;
        var text = System.Text.Encoding.ASCII.GetBytes(value.PadRight(13));
        text.CopyTo(edid, offset + 5);
    }
}
