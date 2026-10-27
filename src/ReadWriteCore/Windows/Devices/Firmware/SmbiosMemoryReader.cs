using ResourceManager.ReadWrite.Information;
using System.Text;
using ResourceManager.ReadWrite.Windows.Devices.Models;

namespace ResourceManager.ReadWrite.Windows.Devices;

public static class SmbiosMemoryReader
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    public static IReadOnlyList<FirmwareMemoryModule> ReadModules()
        => ReadDevices().Select(static device => device.Module).ToArray();

    public static IReadOnlyList<FirmwareMemoryModule> ParseModules(byte[] rawData)
        => ParseDevices(rawData).Select(static device => device.Module).ToArray();
    internal const uint RawSmbiosProvider = 0x52534D42;
    private const uint RawSmbiosProviderReversed = 0x424D5352;

    public static string? ReadDescription()
    {
        var devices = ReadDevices();
        if (devices.Count > 0)
        {
            var description = BuildDescription(devices);
            if (!string.IsNullOrWhiteSpace(description))
            {
                return description;
            }
        }

        return ReadInstalledMemoryDescription();
    }

    private static string? BuildDescription(IReadOnlyList<MemoryDevice> devices)
    {
        var populated = devices.Where(static device => device.SizeMib > 0).ToArray();
        if (populated.Length == 0)
        {
            return null;
        }

        var totalMib = populated.Aggregate(0UL, static (total, device) => total + device.SizeMib);
        var memoryType = MostCommon(populated.Select(static device => device.MemoryType).Where(static value => !string.IsNullOrWhiteSpace(value)));
        var speed = MostCommon(populated.Select(static device => device.ConfiguredSpeedMtps ?? device.SpeedMtps).Where(static value => value is > 0));
        var moduleLayout = FormatModuleLayout(populated);

        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(memoryType))
        {
            parts.Add(memoryType);
        }

        parts.Add(FormatCapacity(totalMib));

        if (!string.IsNullOrWhiteSpace(moduleLayout))
        {
            parts.Add(moduleLayout);
        }

        if (speed is > 0)
        {
            parts.Add($"{speed:N0} MT/s");
        }

        return string.Join(" · ", parts);
    }

    private static string? FormatModuleLayout(IReadOnlyList<MemoryDevice> devices)
    {
        if (devices.Count <= 1)
        {
            return null;
        }

        var firstSize = devices[0].SizeMib;
        if (devices.All(device => device.SizeMib == firstSize))
        {
            return $"{devices.Count}x{FormatCapacity(firstSize)}";
        }

        return $"{devices.Count} modules";
    }

    private static string? MostCommon(IEnumerable<string?> values)
    {
        return values
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value!)
            .GroupBy(static value => value, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(static group => group.Count())
            .ThenBy(static group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.Key)
            .FirstOrDefault();
    }

    private static uint? MostCommon(IEnumerable<uint?> values)
    {
        return values
            .Where(static value => value.HasValue)
            .Select(static value => value!.Value)
            .GroupBy(static value => value)
            .OrderByDescending(static group => group.Count())
            .ThenBy(static group => group.Key)
            .Select(static group => (uint?)group.Key)
            .FirstOrDefault();
    }

    private static string FormatCapacity(ulong sizeMib)
    {
        if (sizeMib >= 1024)
        {
            var sizeGib = sizeMib / 1024d;
            return Math.Abs(sizeGib - Math.Round(sizeGib)) < 0.05
                ? $"{Math.Round(sizeGib):0} GB"
                : $"{sizeGib:0.#} GB";
        }

        return $"{sizeMib:N0} MB";
    }

    private static string? ReadInstalledMemoryDescription()
    {
        return WindowsFirmwareNativeMethods.GetPhysicallyInstalledSystemMemory(out var installedKilobytes) && installedKilobytes > 0
            ? $"Physical RAM {FormatCapacity(installedKilobytes / 1024)}"
            : null;
    }

    private static IReadOnlyList<MemoryDevice> ReadDevices()
    {
        foreach (var provider in new[] { RawSmbiosProvider, RawSmbiosProviderReversed })
        {
            var rawData = ReadRawSmbiosData(provider);
            if (rawData is { Length: > 8 })
            {
                var devices = ParseDevices(rawData);
                if (devices.Count > 0)
                {
                    return devices;
                }
            }
        }

        return [];
    }

    [ResourceManager.ReadWrite.RuntimeRead(ResourceManager.ReadWrite.ReadVolatilityKind.Fixed)]

    internal static byte[]? ReadRawSmbiosData(uint provider)
        => ReadRawSmbiosData(buffer => buffer is null
            ? WindowsFirmwareNativeMethods.GetSystemFirmwareTableSize(provider, 0, IntPtr.Zero, 0)
            : WindowsFirmwareNativeMethods.GetSystemFirmwareTable(provider, 0, buffer, checked((uint)buffer.Length)));

    [ResourceManager.ReadWrite.RuntimeRead(ResourceManager.ReadWrite.ReadVolatilityKind.Fixed)]

    internal static byte[]? ReadRawSmbiosData(Func<byte[]?, uint> read)
    {
        var required = read(null);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            if (required < 8 || required > 4 * 1024 * 1024) return null;
            var buffer = new byte[checked((int)required)];
            var returned = read(buffer);
            if (returned > buffer.Length) { required = returned; continue; }
            if (returned < 8) return null;
            return returned == buffer.Length ? buffer : buffer.AsSpan(0, checked((int)returned)).ToArray();
        }
        return null;
    }

    private static IReadOnlyList<MemoryDevice> ParseDevices(byte[] rawData)
    {
        if (rawData.Length < 8) return [];
        var tableLength = BitConverter.ToUInt32(rawData, 4);
        var tableStart = 8;
        if (tableLength > rawData.Length - tableStart || rawData.Length > 4 * 1024 * 1024) return [];
        var tableEnd = tableStart + checked((int)tableLength);
        var offset = tableStart;
        var devices = new List<MemoryDevice>();
        var structureCount = 0;

        while (offset < tableEnd)
        {
            if (++structureCount > 65_536 || tableEnd - offset < 4) return [];
            var type = rawData[offset];
            var length = rawData[offset + 1];
            if (length < 4 || offset + length > tableEnd)
            {
                return [];
            }

            var stringStart = offset + length;
            var nextOffset = FindNextStructureOffset(rawData, stringStart, tableEnd);
            if (nextOffset <= offset)
            {
                return [];
            }

            if (type == 127) break;

            if (type == 17)
            {
                var strings = ReadStrings(rawData, stringStart, nextOffset);
                var formatted = rawData.AsSpan(offset, length);
                var device = ParseMemoryDevice(formatted, strings);
                if (device is not null)
                {
                    devices.Add(device);
                }
            }

            offset = nextOffset;
        }

        return devices;
    }

    private static int FindNextStructureOffset(byte[] rawData, int stringStart, int tableEnd)
    {
        var cursor = stringStart;
        while (cursor + 1 < tableEnd)
        {
            if (rawData[cursor] == 0 && rawData[cursor + 1] == 0)
            {
                return cursor + 2;
            }

            cursor++;
        }

        return -1;
    }

    private static IReadOnlyList<string> ReadStrings(byte[] rawData, int stringStart, int nextOffset)
    {
        var strings = new List<string>();
        var cursor = stringStart;
        var stringEnd = Math.Max(stringStart, nextOffset - 2);

        while (cursor < stringEnd)
        {
            var end = cursor;
            while (end < stringEnd && rawData[end] != 0)
            {
                end++;
            }

            if (end == cursor)
            {
                break;
            }

            var bytes = rawData.AsSpan(cursor, end - cursor);
            var utf8 = rawData[1] > 3 || rawData[1] == 3 && rawData[2] >= 5;
            var value = string.Empty;
            if (utf8)
            {
                try { value = StrictUtf8.GetString(bytes); }
                catch (DecoderFallbackException) { }
                if (value.Any(char.IsControl)) value = string.Empty;
                else value = value.Trim();
            }
            else
            {
                var ascii = true;
                foreach (var character in bytes)
                    if (character is < 0x20 or > 0x7e) { ascii = false; break; }
                if (ascii) value = Encoding.ASCII.GetString(bytes).Trim();
            }
            strings.Add(value);

            cursor = end + 1;
        }

        return strings;
    }

    private static MemoryDevice? ParseMemoryDevice(ReadOnlySpan<byte> formatted, IReadOnlyList<string> strings)
    {
        var sizeCode = ReadUInt16(formatted, 0x0C);
        if (sizeCode is null or 0)
        {
            return null;
        }

        ulong? capacity = sizeCode == 0xFFFF ? null : sizeCode == 0x7FFF
            ? ReadUInt32(formatted, 0x1C) is { } extended && (extended & 0x7FFFFFFF) > 0
                ? (ulong)(extended & 0x7FFFFFFF) * 1024 * 1024 : null
            : (sizeCode.Value & 0x8000) != 0 ? (ulong)(sizeCode.Value & 0x7FFF) * 1024
            : (ulong)sizeCode.Value * 1024 * 1024;
        var module = new FirmwareMemoryModule(
            $"smbios-memory|{ReadUInt16(formatted, 2):X4}", capacity,
            ReadSmbiosString(formatted, strings, 0x10),
            ReadSmbiosString(formatted, strings, 0x17),
            ReadSmbiosString(formatted, strings, 0x1A),
            ReadSmbiosString(formatted, strings, 0x18), ReadMemoryType(formatted),
            ReadSpeed(formatted, 0x15, 0x54), ReadSpeed(formatted, 0x20, 0x58),
            ReadWidth(formatted, 0x08), ReadWidth(formatted, 0x0A),
            ReadUInt16(formatted, 0x26) is > 0 and var millivolts ? millivolts / 1000d : null);
        return new MemoryDevice(capacity.GetValueOrDefault() / (1024*1024), module.MemoryType,
            module.DeclaredDataRateMtps, module.ConfiguredDataRateMtps, module.Manufacturer, module.PartNumber)
        { Module = module };
    }

    private static uint? ReadSpeed(ReadOnlySpan<byte> data, int offset, int extendedOffset)
        => ReadUInt16(data, offset) switch
        {
            null or 0 => null,
            0xFFFF => ReadUInt32(data, extendedOffset) is > 0 and <= 0x7FFFFFFF and var speed ? speed : null,
            { } speed => speed
        };

    private static ushort? ReadWidth(ReadOnlySpan<byte> data, int offset)
        => ReadUInt16(data, offset) is null or 0 or 0xFFFF ? null : ReadUInt16(data, offset);

    private static string? ReadMemoryType(ReadOnlySpan<byte> formatted)
    {
        if (formatted.Length <= 0x12)
        {
            return null;
        }

        return formatted[0x12] switch
        {
            0x12 => "DDR",
            0x13 => "DDR2",
            0x18 => "DDR3",
            0x1A => "DDR4",
            0x1B => "LPDDR",
            0x1C => "LPDDR2",
            0x1D => "LPDDR3",
            0x1E => "LPDDR4",
            0x22 => "DDR5",
            0x23 => "LPDDR5",
            _ => null
        };
    }

    private static string? ReadSmbiosString(ReadOnlySpan<byte> formatted, IReadOnlyList<string> strings, int offset)
    {
        if (formatted.Length <= offset)
        {
            return null;
        }

        var index = formatted[offset];
        return index > 0 && index <= strings.Count ? strings[index - 1] : null;
    }

    private static ushort? ReadUInt16(ReadOnlySpan<byte> data, int offset)
    {
        return data.Length >= offset + 2
            ? BitConverter.ToUInt16(data.Slice(offset, 2))
            : null;
    }

    private static uint? ReadUInt32(ReadOnlySpan<byte> data, int offset)
    {
        return data.Length >= offset + 4
            ? BitConverter.ToUInt32(data.Slice(offset, 4))
            : null;
    }

    private sealed record MemoryDevice(
        ulong SizeMib,
        string? MemoryType,
        uint? SpeedMtps,
        uint? ConfiguredSpeedMtps,
        string? Manufacturer,
        string? PartNumber)
    {
        internal required FirmwareMemoryModule Module { get; init; }
    }
}
