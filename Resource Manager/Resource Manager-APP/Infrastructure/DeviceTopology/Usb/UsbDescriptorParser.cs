using System.Buffers.Binary;
using System.Text;

namespace ResourceManager.App.Infrastructure.DeviceTopology;

internal static class UsbDescriptorParser
{
    private const byte ConfigurationDescriptorType = 0x02;
    private const byte StringDescriptorType = 0x03;
    private const byte InterfaceDescriptorType = 0x04;
    private const byte EndpointDescriptorType = 0x05;
    private const byte HidDescriptorType = 0x21;
    private const byte ClassSpecificInterfaceDescriptorType = 0x24;
    private const byte VideoInterfaceClass = 0x0E;
    private const byte VideoStreamingSubClass = 0x02;
    private const byte VideoFormatUncompressed = 0x04;
    private const byte VideoFrameUncompressed = 0x05;
    private const byte VideoFormatMjpeg = 0x06;
    private const byte VideoFrameMjpeg = 0x07;
    private const ushort EnglishUnitedStatesLanguageId = 0x0409;

    public static string FormatUsbSpecification(ushort value)
    {
        return $"USB {FormatBcd(value)} (bcdUSB 0x{value:X4})";
    }

    public static string FormatDeviceRevision(ushort value)
    {
        return $"{FormatBcd(value)} (bcdDevice 0x{value:X4})";
    }

    public static string DescribeClass(byte deviceClass, byte deviceSubClass, byte deviceProtocol)
    {
        var name = (deviceClass, deviceSubClass, deviceProtocol) switch
        {
            (0x03, 0x01, 0x01) => "HID Boot Keyboard",
            (0x03, 0x01, 0x02) => "HID Boot Mouse",
            (0x08, 0x06, 0x50) => "Mass Storage / SCSI / Bulk-Only",
            (0x08, 0x06, 0x62) => "Mass Storage / SCSI / UAS",
            (0x0E, 0x01, _) => "Video Control",
            (0x0E, 0x02, _) => "Video Streaming",
            (0x01, 0x01, _) => "Audio Control",
            (0x01, 0x02, _) => "Audio Streaming",
            (0x01, 0x03, _) => "MIDI Streaming",
            _ => DescribeClassName(deviceClass)
        };

        return $"{name} [{deviceClass:X2}/{deviceSubClass:X2}/{deviceProtocol:X2}]";
    }

    public static bool TryReadConfigurationHeader(
        ReadOnlySpan<byte> descriptor,
        out ushort totalLength,
        out byte configurationValue)
    {
        totalLength = 0;
        configurationValue = 0;
        if (descriptor.Length < 9
            || descriptor[0] < 9
            || descriptor[1] != ConfigurationDescriptorType)
        {
            return false;
        }

        totalLength = BinaryPrimitives.ReadUInt16LittleEndian(descriptor.Slice(2, sizeof(ushort)));
        configurationValue = descriptor[5];
        return totalLength >= 9;
    }

    public static IReadOnlyList<string> ParseInterfaceProtocols(ReadOnlySpan<byte> descriptor)
    {
        if (!TryReadConfigurationHeader(descriptor, out var declaredLength, out _))
        {
            return [];
        }

        var limit = Math.Min(descriptor.Length, declaredLength);
        var protocols = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var offset = 0;
        while (offset + 2 <= limit)
        {
            var itemLength = descriptor[offset];
            if (itemLength < 2 || offset + itemLength > limit)
            {
                break;
            }

            var itemType = descriptor[offset + 1];
            if (itemType == InterfaceDescriptorType && itemLength >= 9)
            {
                var summary = DescribeClass(
                    descriptor[offset + 5],
                    descriptor[offset + 6],
                    descriptor[offset + 7]);
                if (seen.Add(summary))
                {
                    protocols.Add(summary);
                }
            }

            offset += itemLength;
        }

        return protocols;
    }

    public static IReadOnlyList<DeviceTopologyUsbEndpointDescriptor> ParseEndpoints(ReadOnlySpan<byte> descriptor)
    {
        if (!TryReadConfigurationHeader(descriptor, out var declaredLength, out _))
        {
            return [];
        }

        var limit = Math.Min(descriptor.Length, declaredLength);
        var endpoints = new List<DeviceTopologyUsbEndpointDescriptor>();
        var current = UsbInterfaceContext.Empty;
        var offset = 0;
        while (TryReadDescriptor(descriptor, limit, ref offset, out var item))
        {
            if (item[1] == InterfaceDescriptorType && item.Length >= 9)
            {
                current = new UsbInterfaceContext(
                    item[2],
                    item[3],
                    item[5],
                    item[6],
                    item[7]);
                continue;
            }

            if (item[1] != EndpointDescriptorType || item.Length < 7 || !current.IsSet)
            {
                continue;
            }

            endpoints.Add(new DeviceTopologyUsbEndpointDescriptor(
                current.InterfaceNumber,
                current.AlternateSetting,
                current.InterfaceClass,
                current.InterfaceSubClass,
                current.InterfaceProtocol,
                item[2],
                (byte)(item[3] & 0x03),
                BinaryPrimitives.ReadUInt16LittleEndian(item.Slice(4, sizeof(ushort))),
                item[6]));
        }

        return endpoints;
    }

    public static IReadOnlyList<DeviceTopologyUsbHidDescriptor> ParseHidDescriptors(ReadOnlySpan<byte> descriptor)
    {
        if (!TryReadConfigurationHeader(descriptor, out var declaredLength, out _))
        {
            return [];
        }

        var limit = Math.Min(descriptor.Length, declaredLength);
        var result = new List<DeviceTopologyUsbHidDescriptor>();
        var current = UsbInterfaceContext.Empty;
        var offset = 0;
        while (TryReadDescriptor(descriptor, limit, ref offset, out var item))
        {
            if (item[1] == InterfaceDescriptorType && item.Length >= 9)
            {
                current = new UsbInterfaceContext(item[2], item[3], item[5], item[6], item[7]);
                continue;
            }

            if (item[1] != HidDescriptorType
                || item.Length < 6
                || current.InterfaceClass != 0x03)
            {
                continue;
            }

            ushort? reportLength = item.Length >= 9 && item[6] == 0x22
                ? BinaryPrimitives.ReadUInt16LittleEndian(item.Slice(7, sizeof(ushort)))
                : null;
            result.Add(new DeviceTopologyUsbHidDescriptor(
                current.InterfaceNumber,
                current.AlternateSetting,
                current.InterfaceSubClass,
                current.InterfaceProtocol,
                BinaryPrimitives.ReadUInt16LittleEndian(item.Slice(2, sizeof(ushort))),
                item[4],
                reportLength));
        }

        return result;
    }

    public static IReadOnlyList<DeviceTopologyUsbCameraModeDescriptor> ParseUvcCameraModes(
        ReadOnlySpan<byte> descriptor)
    {
        if (!TryReadConfigurationHeader(descriptor, out var declaredLength, out _))
        {
            return [];
        }

        var limit = Math.Min(descriptor.Length, declaredLength);
        var modes = new List<DeviceTopologyUsbCameraModeDescriptor>();
        var current = UsbInterfaceContext.Empty;
        var pixelFormat = "UVC";
        var offset = 0;
        while (TryReadDescriptor(descriptor, limit, ref offset, out var item))
        {
            if (item[1] == InterfaceDescriptorType && item.Length >= 9)
            {
                current = new UsbInterfaceContext(item[2], item[3], item[5], item[6], item[7]);
                pixelFormat = "UVC";
                continue;
            }

            if (!current.IsSet
                || current.InterfaceClass != VideoInterfaceClass
                || current.InterfaceSubClass != VideoStreamingSubClass
                || item[1] != ClassSpecificInterfaceDescriptorType
                || item.Length < 3)
            {
                continue;
            }

            pixelFormat = item[2] switch
            {
                VideoFormatUncompressed => ParseUvcFormatGuid(item),
                VideoFormatMjpeg => "MJPEG",
                _ => pixelFormat
            };

            if (item[2] is not (VideoFrameUncompressed or VideoFrameMjpeg) || item.Length < 26)
            {
                continue;
            }

            var width = BinaryPrimitives.ReadUInt16LittleEndian(item.Slice(5, sizeof(ushort)));
            var height = BinaryPrimitives.ReadUInt16LittleEndian(item.Slice(7, sizeof(ushort)));
            var fastestInterval = ReadFastestUvcFrameInterval(item);
            if (width == 0 || height == 0 || fastestInterval is null or 0)
            {
                continue;
            }

            modes.Add(new DeviceTopologyUsbCameraModeDescriptor(
                width,
                height,
                10_000_000d / fastestInterval.Value,
                item[2] == VideoFrameMjpeg ? "MJPEG" : pixelFormat));
        }

        return modes
            .GroupBy(static mode => (mode.Width, mode.Height, mode.PixelFormat))
            .Select(static group => group.OrderByDescending(static mode => mode.MaximumFrameRate).First())
            .OrderByDescending(static mode => (ulong)mode.Width * mode.Height)
            .ThenByDescending(static mode => mode.MaximumFrameRate)
            .ThenBy(static mode => mode.PixelFormat, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static IReadOnlyList<ushort> ParseLanguageIds(ReadOnlySpan<byte> descriptor)
    {
        if (descriptor.Length < 4
            || descriptor[1] != StringDescriptorType
            || descriptor[0] < 4
            || (descriptor[0] & 1) != 0)
        {
            return [];
        }

        var length = Math.Min(descriptor.Length, descriptor[0]);
        var result = new List<ushort>((length - 2) / 2);
        for (var offset = 2; offset + 2 <= length; offset += 2)
        {
            result.Add(BinaryPrimitives.ReadUInt16LittleEndian(descriptor.Slice(offset, sizeof(ushort))));
        }

        return result;
    }

    public static ushort? SelectLanguageId(IReadOnlyList<ushort> languageIds, int currentCultureLcid)
    {
        if (languageIds.Count == 0)
        {
            return null;
        }

        var current = unchecked((ushort)currentCultureLcid);
        if (languageIds.Contains(current))
        {
            return current;
        }

        if (languageIds.Contains(EnglishUnitedStatesLanguageId))
        {
            return EnglishUnitedStatesLanguageId;
        }

        return languageIds[0];
    }

    public static string? ParseStringDescriptor(ReadOnlySpan<byte> descriptor)
    {
        if (descriptor.Length < 2
            || descriptor[1] != StringDescriptorType
            || descriptor[0] < 2
            || (descriptor[0] & 1) != 0
            || descriptor[0] > descriptor.Length)
        {
            return null;
        }

        var value = Encoding.Unicode.GetString(descriptor.Slice(2, descriptor[0] - 2)).TrimEnd('\0');
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static string FormatBcd(ushort value)
    {
        var thousands = (value >> 12) & 0xF;
        var hundreds = (value >> 8) & 0xF;
        var tenths = (value >> 4) & 0xF;
        var hundredths = value & 0xF;
        if (thousands > 9 || hundreds > 9 || tenths > 9 || hundredths > 9)
        {
            return $"0x{value:X4}";
        }

        var major = (thousands * 10) + hundreds;
        return hundredths == 0
            ? $"{major}.{tenths}"
            : $"{major}.{tenths}{hundredths}";
    }

    internal static string FormatBcdVersion(ushort value)
    {
        return FormatBcd(value);
    }

    private static bool TryReadDescriptor(
        ReadOnlySpan<byte> descriptor,
        int limit,
        ref int offset,
        out ReadOnlySpan<byte> item)
    {
        item = default;
        if (offset + 2 > limit)
        {
            return false;
        }

        var itemLength = descriptor[offset];
        if (itemLength < 2 || offset + itemLength > limit)
        {
            offset = limit;
            return false;
        }

        item = descriptor.Slice(offset, itemLength);
        offset += itemLength;
        return true;
    }

    private static uint? ReadFastestUvcFrameInterval(ReadOnlySpan<byte> descriptor)
    {
        var frameIntervalType = descriptor[25];
        if (frameIntervalType == 0)
        {
            return descriptor.Length >= 38
                ? BinaryPrimitives.ReadUInt32LittleEndian(descriptor.Slice(26, sizeof(uint)))
                : null;
        }

        var availableCount = Math.Min(frameIntervalType, (descriptor.Length - 26) / sizeof(uint));
        uint? fastest = null;
        for (var index = 0; index < availableCount; index++)
        {
            var value = BinaryPrimitives.ReadUInt32LittleEndian(
                descriptor.Slice(26 + (index * sizeof(uint)), sizeof(uint)));
            if (value > 0 && (fastest is null || value < fastest))
            {
                fastest = value;
            }
        }

        return fastest;
    }

    private static string ParseUvcFormatGuid(ReadOnlySpan<byte> descriptor)
    {
        if (descriptor.Length < 21)
        {
            return "Uncompressed";
        }

        var fourCc = Encoding.ASCII.GetString(descriptor.Slice(5, 4));
        return fourCc.All(static character => character is >= ' ' and <= '~')
            ? fourCc.TrimEnd('\0', ' ')
            : "Uncompressed";
    }

    private readonly record struct UsbInterfaceContext(
        byte InterfaceNumber,
        byte AlternateSetting,
        byte InterfaceClass,
        byte InterfaceSubClass,
        byte InterfaceProtocol)
    {
        public static UsbInterfaceContext Empty => new(byte.MaxValue, 0, 0, 0, 0);
        public bool IsSet => InterfaceNumber != byte.MaxValue;
    }

    private static string DescribeClassName(byte deviceClass)
    {
        return deviceClass switch
        {
            0x00 => "Per-interface class",
            0x01 => "Audio",
            0x02 => "CDC Control",
            0x03 => "HID",
            0x05 => "Physical",
            0x06 => "Still Image",
            0x07 => "Printer",
            0x08 => "Mass Storage",
            0x09 => "Hub",
            0x0A => "CDC Data",
            0x0B => "Smart Card",
            0x0D => "Content Security",
            0x0E => "Video",
            0x0F => "Personal Healthcare",
            0x10 => "Audio/Video",
            0x11 => "Billboard",
            0x12 => "USB Type-C Bridge",
            0xDC => "Diagnostic",
            0xE0 => "Wireless Controller",
            0xEF => "Miscellaneous",
            0xFE => "Application Specific",
            0xFF => "Vendor Specific",
            _ => "Unknown USB class"
        };
    }
}
