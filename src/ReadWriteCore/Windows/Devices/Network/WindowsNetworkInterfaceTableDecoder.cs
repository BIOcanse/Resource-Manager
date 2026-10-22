using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using ResourceManager.ReadWrite.Windows.Devices.Models;

namespace ResourceManager.ReadWrite.Windows.Devices;

internal sealed record WindowsNetworkInterfaceRow(ulong Luid, uint Index, Guid Guid)
{
    // MIB_IF_ROW2.Description as declared by the interface provider; null when empty.
    public string? Description { get; init; }
}
internal sealed record WindowsNetworkUnicastRow(ulong Luid, uint Index, NetworkUnicastAddress? Address);

internal static partial class WindowsNetworkInterfaceTableDecoder
{
    // Windows SDK MIB layouts; no pointer members, and the same table alignment in both Windows ABIs.
    internal const int TableOffset = 8;
    internal const int InterfaceRowSize = 1352;
    internal const int UnicastRowSize = 80;
    internal const int MaximumInterfaces = 1024;
    internal const int MaximumAddresses = 8192;
    // MIB_IF_ROW2: Luid(8) Index(4) Guid(16) Alias[IF_MAX_STRING_SIZE + 1] then Description[IF_MAX_STRING_SIZE + 1], WCHAR.
    internal const int InterfaceDescriptionOffset = 542;
    internal const int InterfaceStringCharacters = 257;

    internal static IReadOnlyList<WindowsNetworkInterfaceRow>? Interfaces(byte[]? table)
    {
        if (Count(table, InterfaceRowSize, MaximumInterfaces) is not { } count) return null;
        var rows = new WindowsNetworkInterfaceRow[count];
        for (var i = 0; i < count; i++)
        {
            var bytes = table!.AsSpan(TableOffset + i * InterfaceRowSize, InterfaceRowSize);
            rows[i] = new(BinaryPrimitives.ReadUInt64LittleEndian(bytes), U32(bytes, 8), new Guid(bytes.Slice(12, 16)))
            {
                Description = Text(bytes.Slice(InterfaceDescriptionOffset, InterfaceStringCharacters * 2))
            };
        }
        return Array.AsReadOnly(rows);
    }

    internal static IReadOnlyList<WindowsNetworkUnicastRow>? Unicast(byte[]? table)
    {
        if (Count(table, UnicastRowSize, MaximumAddresses) is not { } count) return null;
        var rows = new WindowsNetworkUnicastRow[count];
        for (var i = 0; i < count; i++)
        {
            var bytes = table!.AsSpan(TableOffset + i * UnicastRowSize, UnicastRowSize);
            var family = BinaryPrimitives.ReadUInt16LittleEndian(bytes);
            var prefix = bytes[60];
            NetworkUnicastAddress? address = null;
            if (family == 2 && prefix <= 32 || family == 23 && prefix <= 128)
            {
                // Only the sockaddr family is little-endian; IP bytes retain network order.
                var ip = new IPAddress(family == 2 ? bytes.Slice(4, 4) : bytes.Slice(8, 16));
                address = new(family == 2 ? "ipv4" : "ipv6", ip.ToString(), prefix,
                    family == 23 ? U32(bytes, 24) : null, U32(bytes, 68), U32(bytes, 44), U32(bytes, 48), U32(bytes, 64),
                    U32(bytes, 52), U32(bytes, 56), bytes[61] != 0,
                    BinaryPrimitives.ReadInt64LittleEndian(bytes[72..]).ToString(CultureInfo.InvariantCulture));
            }
            rows[i] = new(BinaryPrimitives.ReadUInt64LittleEndian(bytes[32..]), U32(bytes, 40), address);
        }
        return Array.AsReadOnly(rows);
    }

    private static int? Count(byte[]? table, int stride, int maximum)
    {
        if (table is null || table.Length < TableOffset || table.Length > TableOffset + maximum * stride) return null;
        var count = BinaryPrimitives.ReadUInt32LittleEndian(table);
        return count <= maximum && TableOffset + (long)count * stride <= table.Length ? (int)count : null;
    }
    private static uint U32(ReadOnlySpan<byte> bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes[offset..]);
    private static string? Text(ReadOnlySpan<byte> utf16)
    {
        var text = System.Text.Encoding.Unicode.GetString(utf16);
        var end = text.IndexOf('\0');
        var value = (end < 0 ? text : text[..end]).Trim();
        return value.Length == 0 ? null : value;
    }
}
