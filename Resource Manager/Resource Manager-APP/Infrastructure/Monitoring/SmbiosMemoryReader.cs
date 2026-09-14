using System.Text;
using ResourceManager.App.Infrastructure.Windows;

namespace ResourceManager.App.Infrastructure.Monitoring;

internal static class SmbiosMemoryReader
{
    private const uint RawSmbiosProvider = 0x52534D42;
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

    private static ushort? MostCommon(IEnumerable<ushort?> values)
    {
        return values
            .Where(static value => value.HasValue)
            .Select(static value => value!.Value)
            .GroupBy(static value => value)
            .OrderByDescending(static group => group.Count())
            .ThenBy(static group => group.Key)
            .Select(static group => (ushort?)group.Key)
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
        return NativeMethods.GetPhysicallyInstalledSystemMemory(out var installedKilobytes) && installedKilobytes > 0
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

    private static byte[]? ReadRawSmbiosData(uint provider)
    {
        var size = NativeMethods.GetSystemFirmwareTableSize(provider, 0, IntPtr.Zero, 0);
        if (size == 0)
        {
            return null;
        }

        var buffer = new byte[size];
        var bytesRead = NativeMethods.GetSystemFirmwareTable(provider, 0, buffer, size);
        return bytesRead == size ? buffer : null;
    }

    private static IReadOnlyList<MemoryDevice> ParseDevices(byte[] rawData)
    {
        var tableLength = BitConverter.ToUInt32(rawData, 4);
        var tableStart = 8;
        var tableEnd = Math.Min(rawData.Length, tableStart + (int)tableLength);
        var offset = tableStart;
        var devices = new List<MemoryDevice>();

        while (offset + 4 <= tableEnd)
        {
            var type = rawData[offset];
            var length = rawData[offset + 1];
            if (length < 4 || offset + length > tableEnd)
            {
                break;
            }

            var stringStart = offset + length;
            var nextOffset = FindNextStructureOffset(rawData, stringStart, tableEnd);
            if (nextOffset <= offset)
            {
                break;
            }

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

        return tableEnd;
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

            var value = Encoding.ASCII.GetString(rawData, cursor, end - cursor).Trim();
            if (!string.IsNullOrWhiteSpace(value))
            {
                strings.Add(value);
            }

            cursor = end + 1;
        }

        return strings;
    }

    private static MemoryDevice? ParseMemoryDevice(ReadOnlySpan<byte> formatted, IReadOnlyList<string> strings)
    {
        var sizeMib = ReadMemorySizeMib(formatted);
        if (sizeMib == 0)
        {
            return null;
        }

        return new MemoryDevice(
            sizeMib,
            ReadMemoryType(formatted),
            ReadUInt16(formatted, 0x15),
            ReadUInt16(formatted, 0x20),
            ReadSmbiosString(formatted, strings, 0x17),
            ReadSmbiosString(formatted, strings, 0x1A));
    }

    private static ulong ReadMemorySizeMib(ReadOnlySpan<byte> formatted)
    {
        var size = ReadUInt16(formatted, 0x0C);
        if (size is null or 0xFFFF or 0)
        {
            return 0;
        }

        if (size == 0x7FFF)
        {
            var extendedSize = ReadUInt32(formatted, 0x1C);
            return extendedSize.HasValue ? extendedSize.Value & 0x7FFFFFFF : 0;
        }

        if ((size.Value & 0x8000) != 0)
        {
            return (ulong)(size.Value & 0x7FFF) / 1024;
        }

        return size.Value;
    }

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
        ushort? SpeedMtps,
        ushort? ConfiguredSpeedMtps,
        string? Manufacturer,
        string? PartNumber);
}
