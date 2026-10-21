using System.Buffers.Binary;
using System.Text;

namespace ResourceManager.ReadWrite.Windows.Devices.Usb;

public static class UsbDescriptorParser
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

    /// <summary>Complete Uncompressed/MJPEG frame declarations from the captured configuration.
    /// Malformed supported declarations reject this directory rather than publishing a prefix.</summary>
    public static IReadOnlyList<DeviceTopologyUsbCameraModeDescriptor> ParseUvcCameraModes(
        ReadOnlySpan<byte> descriptor)
    {
        if (!TryReadConfigurationHeader(descriptor, out var limit, out var configurationValue)
            || descriptor.Length < limit || configurationValue == 0)
            return [];

        var modes = new List<DeviceTopologyUsbCameraModeDescriptor>();
        var current = UsbInterfaceContext.Empty;
        var seenFormats = new HashSet<(byte Interface, byte Alternate, byte Format)>();
        var seenFrames = new HashSet<(byte Interface, byte Alternate, byte Format, byte Frame)>();
        byte formatSubtype = 0, formatIndex = 0, declaredFrames = 0;
        var parsedFrames = 0;
        var pixelFormat = string.Empty;
        var offset = 0;
        bool CompleteFormat() => formatSubtype == 0 || parsedFrames == declaredFrames;

        while (offset < limit)
        {
            if (!TryReadDescriptor(descriptor, limit, ref offset, out var item))
                return [];
            if (item[1] == InterfaceDescriptorType)
            {
                if (item.Length < 9 || !CompleteFormat()) return [];
                current = new UsbInterfaceContext(item[2], item[3], item[5], item[6], item[7]);
                formatSubtype = 0;
                continue;
            }
            if (!current.IsSet || current.InterfaceClass != VideoInterfaceClass
                || current.InterfaceSubClass != VideoStreamingSubClass
                || item[1] != ClassSpecificInterfaceDescriptorType)
                continue;
            if (item.Length < 3) return [];

            var subtype = item[2];
            if (subtype is VideoFormatUncompressed or VideoFormatMjpeg)
            {
                if (!CompleteFormat()) return [];
                var requiredLength = subtype == VideoFormatUncompressed ? 27 : 11;
                var defaultFrameOffset = subtype == VideoFormatUncompressed ? 22 : 6;
                if (item.Length != requiredLength || item[3] == 0 || item[4] == 0
                    || item[defaultFrameOffset] == 0 || item[defaultFrameOffset] > item[4]
                    || !seenFormats.Add((current.InterfaceNumber, current.AlternateSetting, item[3])))
                    return [];
                formatSubtype = subtype;
                formatIndex = item[3];
                declaredFrames = item[4];
                parsedFrames = 0;
                pixelFormat = subtype == VideoFormatMjpeg ? "MJPEG" : ParseUvcFormatGuid(item);
                continue;
            }
            if (subtype is not (VideoFrameUncompressed or VideoFrameMjpeg))
            {
                // No other payload's frame layout is assumed to share offset 25.
                if (!CompleteFormat()) return [];
                formatSubtype = 0;
                continue;
            }
            if (formatSubtype == 0) continue;
            if ((subtype == VideoFrameUncompressed && formatSubtype != VideoFormatUncompressed)
                || (subtype == VideoFrameMjpeg && formatSubtype != VideoFormatMjpeg)
                || item.Length < 26 || item[3] == 0 || item[3] > declaredFrames
                || !seenFrames.Add((current.InterfaceNumber, current.AlternateSetting, formatIndex, item[3])))
                return [];

            var width = BinaryPrimitives.ReadUInt16LittleEndian(item.Slice(5, sizeof(ushort)));
            var height = BinaryPrimitives.ReadUInt16LittleEndian(item.Slice(7, sizeof(ushort)));
            var intervals = ReadUvcFrameIntervals(item, configurationValue, current, formatIndex);
            if (width == 0 || height == 0 || intervals is null) return [];
            var shortest = intervals.DiscreteIntervals100Nanoseconds is { } discrete
                ? discrete.Min() : intervals.ContinuousInterval!.Minimum100Nanoseconds;
            modes.Add(new DeviceTopologyUsbCameraModeDescriptor(width, height,
                10_000_000d / shortest, pixelFormat) { FrameIntervalsDeclared = intervals });
            parsedFrames++;
        }
        if (!CompleteFormat()) return [];
        return modes
            .OrderByDescending(static mode => (ulong)mode.Width * mode.Height)
            .ThenByDescending(static mode => mode.MaximumFrameRate)
            .ThenBy(static mode => mode.PixelFormat, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static mode => mode.FrameIntervalsDeclared!.InterfaceNumber)
            .ThenBy(static mode => mode.FrameIntervalsDeclared!.AlternateSetting)
            .ThenBy(static mode => mode.FrameIntervalsDeclared!.FormatIndex)
            .ThenBy(static mode => mode.FrameIntervalsDeclared!.FrameIndex)
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

    private static DeviceTopologyUsbCameraFrameIntervals? ReadUvcFrameIntervals(
        ReadOnlySpan<byte> descriptor, byte configurationValue, UsbInterfaceContext current, byte formatIndex)
    {
        var count = descriptor[25];
        var requiredLength = count == 0 ? 38 : checked(26 + (count * sizeof(uint)));
        if (descriptor.Length != requiredLength) return null;
        IReadOnlyList<uint>? discrete = null;
        DeviceTopologyUsbCameraContinuousFrameInterval? continuous = null;
        if (count == 0)
        {
            continuous = new(
                BinaryPrimitives.ReadUInt32LittleEndian(descriptor.Slice(26, sizeof(uint))),
                BinaryPrimitives.ReadUInt32LittleEndian(descriptor.Slice(30, sizeof(uint))),
                BinaryPrimitives.ReadUInt32LittleEndian(descriptor.Slice(34, sizeof(uint))));
        }
        else
        {
            var values = new uint[count];
            for (var index = 0; index < values.Length; index++)
                values[index] = BinaryPrimitives.ReadUInt32LittleEndian(descriptor.Slice(
                    checked(26 + (index * sizeof(uint))), sizeof(uint)));
            discrete = Array.AsReadOnly(values);
        }
        var result = new DeviceTopologyUsbCameraFrameIntervals(configurationValue, current.InterfaceNumber,
            current.AlternateSetting, formatIndex, descriptor[3],
            BinaryPrimitives.ReadUInt32LittleEndian(descriptor.Slice(21, sizeof(uint))), discrete, continuous);
        return result.IsComplete() ? result : null;
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
