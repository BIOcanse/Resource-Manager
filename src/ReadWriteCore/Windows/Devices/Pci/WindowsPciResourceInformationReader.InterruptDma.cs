using System.Buffers.Binary;
using System.Globalization;
using ResourceManager.ReadWrite.Windows.Devices.Models;

namespace ResourceManager.ReadWrite.Windows.Devices;

internal sealed partial class WindowsPciResourceInformationReader
{
    internal static DeviceTopologyPciInterrupt? ParseInterrupt(int pointerBytes, ReadOnlySpan<byte> bytes)
    {
        var headerSize = pointerBytes switch { 4 => 20, 8 => 24, _ => 0 };
        if (!IsAllocatedHeader(bytes, headerSize)) return null;
        // Windows 10 SDK NT_PROCESSOR_GROUPS layout; raw flags/group, no processor-list inference.
        var mask = pointerBytes == 4 ? BinaryPrimitives.ReadUInt32LittleEndian(bytes[16..]) : BinaryPrimitives.ReadUInt64LittleEndian(bytes[16..]);
        return new(BinaryPrimitives.ReadUInt32LittleEndian(bytes[12..]), BinaryPrimitives.ReadUInt16LittleEndian(bytes[10..]),
            mask.ToString(CultureInfo.InvariantCulture), BinaryPrimitives.ReadUInt16LittleEndian(bytes[8..]));
    }

    internal static DeviceTopologyPciDmaChannel? ParseDmaChannel(ReadOnlySpan<byte> bytes)
        => IsAllocatedHeader(bytes, 16)
            ? new(BinaryPrimitives.ReadUInt32LittleEndian(bytes[12..]), BinaryPrimitives.ReadUInt32LittleEndian(bytes[8..])) : null;

    private static bool IsAllocatedHeader(ReadOnlySpan<byte> bytes, int headerSize)
        => headerSize != 0 && bytes.Length >= headerSize && BinaryPrimitives.ReadUInt32LittleEndian(bytes) == 0
            && BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]) == 12;
}
