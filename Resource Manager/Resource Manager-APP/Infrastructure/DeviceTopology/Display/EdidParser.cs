using System.Buffers.Binary;
using System.Text;

namespace ResourceManager.App.Infrastructure.DeviceTopology;

internal static class EdidParser
{
    private static ReadOnlySpan<byte> Header => [0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x00];

    public static DeviceTopologyEdidCapabilities? Parse(ReadOnlySpan<byte> edid)
    {
        if (edid.Length < 128 || !edid[..8].SequenceEqual(Header))
        {
            return null;
        }

        var digital = (edid[20] & 0x80) != 0;
        var bitsPerColor = digital ? DecodeBitsPerColor(edid[20]) : null;
        var digitalInterface = digital ? DecodeDigitalInterface(edid[20]) : "模拟显示输入";
        var productName = ReadTextDescriptor(edid, 0xFC);
        var serialText = ReadTextDescriptor(edid, 0xFF);
        var numericSerial = BinaryPrimitives.ReadUInt32LittleEndian(edid.Slice(12, sizeof(uint)));
        var hdrFormats = ReadHdrFormats(edid);
        var widthMillimeters = edid[21] == 0 ? null : checked((uint?)edid[21] * 10u);
        var heightMillimeters = edid[22] == 0 ? null : checked((uint?)edid[22] * 10u);

        return new DeviceTopologyEdidCapabilities(
            Version: $"EDID {edid[18]}.{edid[19]}",
            ProductName: productName,
            SerialNumber: serialText ?? (numericSerial == 0 ? null : numericSerial.ToString()),
            BitsPerColorChannel: bitsPerColor,
            DigitalInterface: digitalInterface,
            HdrFormats: hdrFormats.Count == 0 ? null : string.Join(" / ", hdrFormats),
            WidthMillimeters: widthMillimeters,
            HeightMillimeters: heightMillimeters,
            DisplayTechnology: digital ? $"数字显示 / {digitalInterface}" : "模拟显示",
            PanelTechnology: null);
    }

    private static uint? DecodeBitsPerColor(byte videoInputDefinition)
    {
        return ((videoInputDefinition >> 4) & 0x07) switch
        {
            1 => 6,
            2 => 8,
            3 => 10,
            4 => 12,
            5 => 14,
            6 => 16,
            _ => null
        };
    }

    private static string DecodeDigitalInterface(byte videoInputDefinition)
    {
        return (videoInputDefinition & 0x0F) switch
        {
            0 => "未定义数字接口",
            1 => "DVI",
            2 => "HDMI Type-A",
            3 => "HDMI Type-B",
            4 => "MDDI",
            5 => "DisplayPort",
            _ => "保留数字接口"
        };
    }

    private static string? ReadTextDescriptor(ReadOnlySpan<byte> edid, byte descriptorType)
    {
        foreach (var offset in new[] { 54, 72, 90, 108 })
        {
            var descriptor = edid.Slice(offset, 18);
            if (descriptor[0] != 0 || descriptor[1] != 0 || descriptor[2] != 0 || descriptor[3] != descriptorType)
            {
                continue;
            }

            var value = Encoding.ASCII.GetString(descriptor.Slice(5, 13))
                .TrimEnd('\0', '\n', '\r', ' ')
                .Trim();
            return value.Length == 0 ? null : value;
        }

        return null;
    }

    private static IReadOnlyList<string> ReadHdrFormats(ReadOnlySpan<byte> edid)
    {
        var result = new List<string>();
        var extensionCount = Math.Min(edid[126], (edid.Length / 128) - 1);
        for (var extensionIndex = 0; extensionIndex < extensionCount; extensionIndex++)
        {
            var extension = edid.Slice((extensionIndex + 1) * 128, 128);
            if (extension[0] != 0x02)
            {
                continue;
            }

            var dataBlockEnd = extension[2] is 0 or > 127 ? 127 : extension[2];
            var offset = 4;
            while (offset < dataBlockEnd)
            {
                var header = extension[offset];
                var length = header & 0x1F;
                if (length == 0 || offset + 1 + length > dataBlockEnd)
                {
                    break;
                }

                var tag = header >> 5;
                var payload = extension.Slice(offset + 1, length);
                if (tag == 0x07 && payload.Length >= 2 && payload[0] == 0x06)
                {
                    AddHdrEotfs(result, payload[1]);
                }

                offset += 1 + length;
            }
        }

        return result;
    }

    private static void AddHdrEotfs(ICollection<string> result, byte flags)
    {
        if ((flags & 0x02) != 0)
        {
            result.Add("传统 HDR");
        }
        if ((flags & 0x04) != 0)
        {
            result.Add("HDR10 / PQ");
        }
        if ((flags & 0x08) != 0)
        {
            result.Add("HLG");
        }
    }
}

internal sealed record DeviceTopologyEdidCapabilities(
    string Version,
    string? ProductName,
    string? SerialNumber,
    uint? BitsPerColorChannel,
    string DigitalInterface,
    string? HdrFormats,
    uint? WidthMillimeters,
    uint? HeightMillimeters,
    string DisplayTechnology,
    string? PanelTechnology);
