using System.Buffers.Binary;
using System.Globalization;
using ResourceManager.ReadWrite.Windows.Devices.Models;

namespace ResourceManager.ReadWrite.Windows.Devices;

internal sealed partial class WindowsPciResourceInformationReader
{
    internal static DeviceTopologyPciResourceRange? Parse(uint type, ReadOnlySpan<byte> bytes)
    {
        // cfgmgr32 SDK uses pack(1); allocated headers have Count=0 and the exact range structure size.
        var headerSize = type switch { 1 or 7 => 32, 2 => 28, 6 => 20, _ => 0 };
        var rangeSize = type switch { 1 => 36, 2 or 7 => 40, 6 => 16, _ => 0 };
        if (headerSize == 0 || bytes.Length < headerSize || BinaryPrimitives.ReadUInt32LittleEndian(bytes) != 0
            || BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]) != rangeSize) return null;
        var start = type == 6 ? BinaryPrimitives.ReadUInt32LittleEndian(bytes[12..]) : BinaryPrimitives.ReadUInt64LittleEndian(bytes[8..]);
        var end = type == 6 ? BinaryPrimitives.ReadUInt32LittleEndian(bytes[16..]) : BinaryPrimitives.ReadUInt64LittleEndian(bytes[16..]);
        var flags = BinaryPrimitives.ReadUInt32LittleEndian(bytes[(type == 6 ? 8 : 24)..]);
        if (end < start) return null;
        return new(type, start.ToString(CultureInfo.InvariantCulture), end.ToString(CultureInfo.InvariantCulture),
            ((UInt128)end - start + 1).ToString(CultureInfo.InvariantCulture), flags);
    }
}
