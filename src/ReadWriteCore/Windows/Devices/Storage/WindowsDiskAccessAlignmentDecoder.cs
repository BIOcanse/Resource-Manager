using System.Buffers.Binary;
using ResourceManager.ReadWrite.Windows.Devices.Models;

namespace ResourceManager.ReadWrite.Windows.Devices;

internal static class WindowsDiskAccessAlignmentDecoder
{
    internal const int DescriptorSize = 28;
    internal const int MaximumBytes = 65536;

    internal static DiskAccessAlignmentInformation? Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < DescriptorSize || bytes.Length > MaximumBytes) return null;
        var version = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        var size = BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]);
        // Version is a structure size, not a version number. Only a complete known prefix is interpreted.
        if (version < DescriptorSize || version > size || size > bytes.Length) return null;
        return new(BinaryPrimitives.ReadUInt32LittleEndian(bytes[8..]), BinaryPrimitives.ReadUInt32LittleEndian(bytes[12..]),
            BinaryPrimitives.ReadUInt32LittleEndian(bytes[16..]), BinaryPrimitives.ReadUInt32LittleEndian(bytes[20..]),
            BinaryPrimitives.ReadUInt32LittleEndian(bytes[24..]));
    }
}
