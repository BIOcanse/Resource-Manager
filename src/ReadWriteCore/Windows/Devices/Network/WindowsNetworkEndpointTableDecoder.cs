using System.Buffers.Binary;
using System.Net;
using ResourceManager.ReadWrite.Windows.Devices.Models;

namespace ResourceManager.ReadWrite.Windows.Devices;

internal enum WindowsNetworkEndpointTableKind { TcpIpv4, TcpIpv6, UdpIpv4, UdpIpv6 }

internal static class WindowsNetworkEndpointTableDecoder
{
    internal const int TableOffset = 4;
    internal const int MaximumRows = 16384;
    // SDK SIZEOF table macros permit extra alignment bytes after the last row.
    internal static int MaximumBytes(int stride) => TableOffset + MaximumRows * stride + 8;
    internal static int RowSize(WindowsNetworkEndpointTableKind kind) => kind switch {
        WindowsNetworkEndpointTableKind.TcpIpv4 => 24, WindowsNetworkEndpointTableKind.TcpIpv6 => 56,
        WindowsNetworkEndpointTableKind.UdpIpv4 => 12, WindowsNetworkEndpointTableKind.UdpIpv6 => 28,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)) };

    internal static IReadOnlyList<NetworkTcpEndpoint>? Tcp(byte[]? table, bool ipv6)
    {
        var stride = ipv6 ? 56 : 24;
        if (Count(table, stride) is not { } count) return null;
        var rows = new NetworkTcpEndpoint[count];
        for (var i = 0; i < count; i++)
        {
            var bytes = table!.AsSpan(TableOffset + i * stride, stride);
            var state = U32(bytes, ipv6 ? 48 : 0);
            var local = ipv6 ? Socket(bytes, 0, 16, 20) : Socket(bytes, 4, null, 8);
            var remote = state == 2 ? null : ipv6 ? Socket(bytes, 24, 40, 44) : Socket(bytes, 12, null, 16);
            rows[i] = new(ipv6 ? "ipv6" : "ipv4", local, remote, state, U32(bytes, ipv6 ? 52 : 20));
        }
        return Array.AsReadOnly(rows);
    }

    internal static IReadOnlyList<NetworkUdpEndpoint>? Udp(byte[]? table, bool ipv6)
    {
        var stride = ipv6 ? 28 : 12;
        if (Count(table, stride) is not { } count) return null;
        var rows = new NetworkUdpEndpoint[count];
        for (var i = 0; i < count; i++)
        {
            var bytes = table!.AsSpan(TableOffset + i * stride, stride);
            rows[i] = new(ipv6 ? "ipv6" : "ipv4", ipv6 ? Socket(bytes, 0, 16, 20) : Socket(bytes, 0, null, 4),
                U32(bytes, ipv6 ? 24 : 8));
        }
        return Array.AsReadOnly(rows);
    }

    private static NetworkSocketAddress Socket(ReadOnlySpan<byte> bytes, int address, int? scope, int port)
        => new(new IPAddress(bytes.Slice(address, scope is null ? 4 : 16)).ToString(),
            scope is { } offset ? U32(bytes, offset) : null, BinaryPrimitives.ReadUInt16BigEndian(bytes[port..]));
    private static int? Count(byte[]? table, int stride)
    {
        if (table is null || table.Length < TableOffset || table.Length > MaximumBytes(stride)) return null;
        var count = BinaryPrimitives.ReadUInt32LittleEndian(table);
        return count <= MaximumRows && TableOffset + (long)count * stride <= table.Length ? (int)count : null;
    }
    private static uint U32(ReadOnlySpan<byte> bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes[offset..]);
}
