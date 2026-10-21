using ResourceManager.ReadWrite.Information;
using System.Buffers.Binary;
using ResourceManager.App.Domain.DeviceTopology;
using System.Text;

namespace ResourceManager.ReadWrite.Windows.Devices.Display;

public static partial class EdidParser
{
    private static ReadOnlySpan<byte> Header => [0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x00];

    public static DeviceTopologyEdidCapabilities? Parse(ReadOnlySpan<byte> edid)
    {
        if (edid.Length < 128 || !edid[..8].SequenceEqual(Header))
        {
            return null;
        }

        var digital = (edid[20] & 0x80) != 0;
        var version14 = edid[18] == 1 && edid[19] == 4;
        var bitsPerColor = digital && version14 ? DecodeBitsPerColor(edid[20]) : null;
        var digitalInterface = !digital ? DeviceEdidDigitalInterfaces.Analog
            : version14 ? DecodeDigitalInterface(edid[20]) : DeviceEdidDigitalInterfaces.Undefined;
        var productName = ReadTextDescriptor(edid, 0xFC);
        var serialText = ReadTextDescriptor(edid, 0xFF);
        var numericSerial = BinaryPrimitives.ReadUInt32LittleEndian(edid.Slice(12, sizeof(uint)));
        var cta = ReadCtaDeclarations(edid);
        var hdrFormats = new List<string>();
        AddHdrEotfs(hdrFormats, cta?.Eotfs ?? 0);
        // In 1.4 a single zero makes the other byte an aspect ratio, not centimeters.
        var aspectRatio14 = version14 && (edid[21] == 0 || edid[22] == 0);
        var widthMillimeters = aspectRatio14 || edid[21] == 0 ? null : checked((uint?)edid[21] * 10u);
        var heightMillimeters = aspectRatio14 || edid[22] == 0 ? null : checked((uint?)edid[22] * 10u);

        return new DeviceTopologyEdidCapabilities(
            Version: $"EDID {edid[18]}.{edid[19]}",
            ProductName: productName,
            SerialNumber: serialText ?? (numericSerial == 0 ? null : numericSerial.ToString()),
            BitsPerColorChannel: bitsPerColor,
            DigitalInterface: digitalInterface,
            HdrFormats: hdrFormats.Count == 0 ? null : string.Join(" / ", hdrFormats),
            WidthMillimeters: widthMillimeters,
            HeightMillimeters: heightMillimeters,
            IsDigital: digital,
            PanelTechnology: null,
            ChromaticityDeclared: ReadChromaticity(edid),
            HdrLuminanceDeclared: cta?.Luminance)
        {
            ModeCapability = ReadModeCapability(edid)
        };
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
            0 => DeviceEdidDigitalInterfaces.Undefined,
            1 => DeviceEdidDigitalInterfaces.Dvi,
            2 => DeviceEdidDigitalInterfaces.HdmiTypeA,
            3 => DeviceEdidDigitalInterfaces.HdmiTypeB,
            4 => DeviceEdidDigitalInterfaces.Mddi,
            5 => DeviceEdidDigitalInterfaces.DisplayPort,
            _ => DeviceEdidDigitalInterfaces.Reserved
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

    private static void AddHdrEotfs(ICollection<string> result, byte flags)
    {
        if ((flags & 0x02) != 0)
        {
            result.Add("Traditional HDR");
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

public sealed record DeviceTopologyEdidCapabilities(
    string Version,
    string? ProductName,
    string? SerialNumber,
    uint? BitsPerColorChannel,
    /// <summary>取值见 <see cref="DeviceEdidDigitalInterfaces"/>。</summary>
    string DigitalInterface,
    string? HdrFormats,
    uint? WidthMillimeters,
    uint? HeightMillimeters,
    bool IsDigital,
    string? PanelTechnology,
    DeviceInformationChromaticity? ChromaticityDeclared = null,
    IReadOnlyList<DeviceInformationHdrLuminanceDeclaration>? HdrLuminanceDeclared = null)
{
    public EdidModeCapability? ModeCapability { get; init; }
}
