using System.Buffers.Binary;
using ResourceManager.ReadWrite.Windows.Devices.Models;

namespace ResourceManager.ReadWrite.Windows.Devices;

internal static class WindowsVolumeMetadataInformationDecoder
{
    internal const int ExtentOffset = 8;
    internal const int ExtentSize = 24;
    internal const int MaximumExtents = 4096;

    internal static int RequiredExtentBytes(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < sizeof(uint)) return 0;
        var count = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        return count > MaximumExtents ? 0 : ExtentOffset + (int)count * ExtentSize;
    }

    internal static IReadOnlyList<DeviceTopologyVolumeDiskExtent>? Extents(ReadOnlySpan<byte> bytes)
    {
        var required = RequiredExtentBytes(bytes);
        if (required == 0 || bytes.Length < required) return null;
        var count = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        var result = new List<DeviceTopologyVolumeDiskExtent>((int)count);
        for (var index = 0; index < count; index++)
        {
            var item = bytes.Slice(ExtentOffset + index * ExtentSize, ExtentSize);
            var disk = BinaryPrimitives.ReadUInt32LittleEndian(item);
            var start = BinaryPrimitives.ReadInt64LittleEndian(item[8..]);
            var length = BinaryPrimitives.ReadInt64LittleEndian(item[16..]);
            if (disk == uint.MaxValue || start < 0 || length <= 0 || (ulong)start + (ulong)length > long.MaxValue) return null;
            result.Add(new(disk, (ulong)start, (ulong)length));
        }
        return result.OrderBy(static extent => extent.DiskNumberReported).ThenBy(static extent => extent.StartingOffsetBytes)
            .ThenBy(static extent => extent.ExtentLengthBytes).ToArray();
    }

    internal static IReadOnlyList<string>? MountPaths(ReadOnlySpan<char> characters)
    {
        if (characters.IsEmpty) return null;
        var paths = new List<string>();
        var position = 0;
        while (position < characters.Length)
        {
            var end = characters[position..].IndexOf('\0');
            if (end < 0) return null;
            if (end == 0)
            {
                foreach (var character in characters[position..]) if (character != '\0') return null;
                return paths.Order(StringComparer.Ordinal).ToArray();
            }
            paths.Add(new string(characters.Slice(position, end)));
            position += end + 1;
        }
        return null; // A list containing paths requires the additional MULTI_SZ terminator.
    }
}
