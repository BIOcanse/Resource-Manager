using System.Buffers.Binary;
using System.Net;
using ResourceManager.ReadWrite.Windows.Devices.Models;

namespace ResourceManager.ReadWrite.Windows.Devices;

internal sealed record WindowsNetworkRouteRow(ulong Luid, uint Index, NetworkRoute? Route);

internal static partial class WindowsNetworkInterfaceTableDecoder
{
    internal const int RouteRowSize = 104;
    internal const int MaximumRoutes = 16384;

    internal static IReadOnlyList<WindowsNetworkRouteRow>? Routes(byte[]? table)
    {
        if (Count(table, RouteRowSize, MaximumRoutes) is not { } count) return null;
        var rows = new WindowsNetworkRouteRow[count];
        for (var i = 0; i < count; i++)
        {
            var bytes = table!.AsSpan(TableOffset + i * RouteRowSize, RouteRowSize);
            var family = BinaryPrimitives.ReadUInt16LittleEndian(bytes[12..]);
            var nextFamily = BinaryPrimitives.ReadUInt16LittleEndian(bytes[44..]);
            var prefix = bytes[40];
            NetworkRoute? route = null;
            if (family == nextFamily && (family == 2 && prefix <= 32 || family == 23 && prefix <= 128))
            {
                var destination = new IPAddress(family == 2 ? bytes.Slice(16, 4) : bytes.Slice(20, 16));
                var nextHop = new IPAddress(family == 2 ? bytes.Slice(48, 4) : bytes.Slice(52, 16));
                route = new(family == 2 ? "ipv4" : "ipv6", destination.ToString(), prefix,
                    family == 23 ? U32(bytes, 36) : null, nextHop.ToString(), family == 23 ? U32(bytes, 68) : null,
                    bytes[72], U32(bytes, 76), U32(bytes, 80), U32(bytes, 84), U32(bytes, 88),
                    bytes[92] != 0, bytes[93] != 0, bytes[94] != 0, bytes[95] != 0, U32(bytes, 96), U32(bytes, 100));
            }
            rows[i] = new(BinaryPrimitives.ReadUInt64LittleEndian(bytes), U32(bytes, 8), route);
        }
        return Array.AsReadOnly(rows);
    }
}
