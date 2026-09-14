using System.Net;
using System.Runtime.InteropServices;

namespace ResourceManager.App.Infrastructure.ResourceBreakdown;

public sealed partial class IpHelperNetworkAttributionReader
{
    private static IReadOnlyList<NetworkEndpoint> ReadEndpoints()
    {
        var endpoints = new List<NetworkEndpoint>();
        endpoints.AddRange(ReadTcp4Endpoints());
        endpoints.AddRange(ReadTcp6Endpoints());
        endpoints.AddRange(ReadUdp4Endpoints());
        endpoints.AddRange(ReadUdp6Endpoints());
        return endpoints;
    }

    private static IReadOnlyList<NetworkEndpoint> ReadTcp4Endpoints()
    {
        return ReadExtendedTable(
            (IntPtr buffer, ref int size) => GetExtendedTcpTable(buffer, ref size, true, AfInet, TcpTableOwnerPidAll, 0),
            buffer =>
            {
                var entries = Marshal.ReadInt32(buffer);
                var rowSize = Marshal.SizeOf<MibTcpRowOwnerPid>();
                var rows = new List<NetworkEndpoint>(Math.Max(0, entries));
                var rowPointer = IntPtr.Add(buffer, sizeof(int));
                for (var index = 0; index < entries; index++)
                {
                    var row = Marshal.PtrToStructure<MibTcpRowOwnerPid>(IntPtr.Add(rowPointer, rowSize * index));
                    var tcpRow = new MibTcpRow(row.State, row.LocalAddr, row.LocalPort, row.RemoteAddr, row.RemotePort);
                    rows.Add(NetworkEndpoint.Tcp(
                        $"tcp4:{row.OwningPid}:{row.State}:{row.LocalAddr}:{row.LocalPort}:{row.RemoteAddr}:{row.RemotePort}",
                        (int)row.OwningPid,
                        row.State == TcpStateEstablished ? 3 : 0.5,
                        tcpRow,
                        IsIpv4HostLocal(row.LocalAddr) || IsIpv4HostLocal(row.RemoteAddr),
                        !IsIpv4HostLocal(row.RemoteAddr) && row.RemoteAddr != 0,
                        ReadPort(row.LocalPort),
                        ReadPort(row.RemotePort)));
                }

                return rows;
            });
    }

    private static IReadOnlyList<NetworkEndpoint> ReadTcp6Endpoints()
    {
        return ReadExtendedTable(
            (IntPtr buffer, ref int size) => GetExtendedTcpTable(buffer, ref size, true, AfInet6, TcpTableOwnerPidAll, 0),
            buffer =>
            {
                var entries = Marshal.ReadInt32(buffer);
                var rowSize = Marshal.SizeOf<MibTcp6RowOwnerPid>();
                var rows = new List<NetworkEndpoint>(Math.Max(0, entries));
                var rowPointer = IntPtr.Add(buffer, sizeof(int));
                for (var index = 0; index < entries; index++)
                {
                    var row = Marshal.PtrToStructure<MibTcp6RowOwnerPid>(IntPtr.Add(rowPointer, rowSize * index));
                    rows.Add(NetworkEndpoint.Tcp(
                        $"tcp6:{row.OwningPid}:{row.State}:{row.LocalScopeId}:{row.LocalPort}:{row.RemoteScopeId}:{row.RemotePort}",
                        (int)row.OwningPid,
                        row.State == TcpStateEstablished ? 3 : 0.5,
                        null,
                        IsIpv6HostLocal(row.LocalAddr) || IsIpv6HostLocal(row.RemoteAddr),
                        !IsIpv6HostLocal(row.RemoteAddr) && !IsIpv6Unspecified(row.RemoteAddr),
                        ReadPort(row.LocalPort),
                        ReadPort(row.RemotePort)));
                }

                return rows;
            });
    }

    private static IReadOnlyList<NetworkEndpoint> ReadUdp4Endpoints()
    {
        return ReadExtendedTable(
            (IntPtr buffer, ref int size) => GetExtendedUdpTable(buffer, ref size, true, AfInet, UdpTableOwnerPid, 0),
            buffer =>
            {
                var entries = Marshal.ReadInt32(buffer);
                var rowSize = Marshal.SizeOf<MibUdpRowOwnerPid>();
                var rows = new List<NetworkEndpoint>(Math.Max(0, entries));
                var rowPointer = IntPtr.Add(buffer, sizeof(int));
                for (var index = 0; index < entries; index++)
                {
                    var row = Marshal.PtrToStructure<MibUdpRowOwnerPid>(IntPtr.Add(rowPointer, rowSize * index));
                    rows.Add(NetworkEndpoint.Udp(
                        $"udp4:{row.OwningPid}:{row.LocalAddr}:{row.LocalPort}",
                        (int)row.OwningPid,
                        IsIpv4HostLocal(row.LocalAddr),
                        !IsIpv4HostLocal(row.LocalAddr),
                        ReadPort(row.LocalPort)));
                }

                return rows;
            });
    }

    private static IReadOnlyList<NetworkEndpoint> ReadUdp6Endpoints()
    {
        return ReadExtendedTable(
            (IntPtr buffer, ref int size) => GetExtendedUdpTable(buffer, ref size, true, AfInet6, UdpTableOwnerPid, 0),
            buffer =>
            {
                var entries = Marshal.ReadInt32(buffer);
                var rowSize = Marshal.SizeOf<MibUdp6RowOwnerPid>();
                var rows = new List<NetworkEndpoint>(Math.Max(0, entries));
                var rowPointer = IntPtr.Add(buffer, sizeof(int));
                for (var index = 0; index < entries; index++)
                {
                    var row = Marshal.PtrToStructure<MibUdp6RowOwnerPid>(IntPtr.Add(rowPointer, rowSize * index));
                    rows.Add(NetworkEndpoint.Udp(
                        $"udp6:{row.OwningPid}:{row.LocalScopeId}:{row.LocalPort}",
                        (int)row.OwningPid,
                        IsIpv6HostLocal(row.LocalAddr),
                        !IsIpv6HostLocal(row.LocalAddr),
                        ReadPort(row.LocalPort)));
                }

                return rows;
            });
    }

    private static IReadOnlyList<NetworkEndpoint> ReadExtendedTable(
        ExtendedTableReader reader,
        Func<IntPtr, IReadOnlyList<NetworkEndpoint>> parse)
    {
        var size = 0;
        var result = reader(IntPtr.Zero, ref size);
        if (result != ErrorInsufficientBuffer || size <= 0)
        {
            return [];
        }

        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            result = reader(buffer, ref size);
            return result == ErrorSuccess ? parse(buffer) : [];
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static bool IsIpv4HostLocal(uint address)
    {
        var bytes = BitConverter.GetBytes(address);
        return bytes.Length >= 4 && (bytes[0] == 127 || bytes[3] == 127);
    }

    private static bool IsIpv6HostLocal(byte[]? address)
    {
        return address is { Length: 16 }
            && address[..15].All(static value => value == 0)
            && address[15] == 1;
    }

    private static bool IsIpv6Unspecified(byte[]? address)
    {
        return address is not { Length: 16 } || address.All(static value => value == 0);
    }

    private static int ReadPort(uint port)
    {
        return Math.Max(0, (int)(ushort)IPAddress.NetworkToHostOrder((short)port));
    }
}
