using System.Runtime.InteropServices;

namespace ResourceManager.ReadWrite.Windows.Devices;

internal interface IWindowsNetworkEndpointTableApi
{
    byte[]? Read(WindowsNetworkEndpointTableKind kind);
    IWindowsNetworkEndpointProcessLease? OpenProcess(uint processId);
}

internal interface IWindowsNetworkEndpointProcessLease : IDisposable
{
    uint ProcessId { get; }
    bool TryReadCreationFileTime(out ulong created);
}

internal sealed class WindowsNetworkEndpointTableApi : IWindowsNetworkEndpointTableApi
{
    internal delegate uint QueryTable(nint table, ref uint size, bool order, uint family, uint tableClass, uint reserved);
    public IWindowsNetworkEndpointProcessLease? OpenProcess(uint processId) => WindowsNetworkEndpointProcessLease.Open(processId);

    public byte[]? Read(WindowsNetworkEndpointTableKind kind)
    {
        if (!OperatingSystem.IsWindows()) return null;
        var (tcp, family) = kind switch {
            WindowsNetworkEndpointTableKind.TcpIpv4 => (true, 2U), WindowsNetworkEndpointTableKind.TcpIpv6 => (true, 23U),
            WindowsNetworkEndpointTableKind.UdpIpv4 => (false, 2U), WindowsNetworkEndpointTableKind.UdpIpv6 => (false, 23U),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)) };
        return ReadBufferedTable(tcp ? Native.GetExtendedTcpTable : Native.GetExtendedUdpTable, family, tcp ? 5U : 1U,
            WindowsNetworkEndpointTableDecoder.RowSize(kind));
    }

    internal static byte[]? ReadBufferedTable(QueryTable query, uint family, uint tableClass, int stride)
    {
        uint size = 0;
        var status = query(0, ref size, false, family, tableClass, 0);
        if (status == 232) return new byte[4]; // ERROR_NO_DATA: no entries, as in Microsoft's .NET consumer.
        if (status != 122) return null;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (size < 4 || size > WindowsNetworkEndpointTableDecoder.MaximumBytes(stride)) return null;
            var capacity = size;
            var buffer = Marshal.AllocHGlobal(checked((int)capacity));
            try
            {
                status = query(buffer, ref size, false, family, tableClass, 0);
                if (status == 232) return new byte[4];
                if (status == 0)
                {
                    if (size < 4 || size > capacity) return null;
                    var bytes = new byte[size]; Marshal.Copy(buffer, bytes, 0, bytes.Length); return bytes;
                }
                if (status != 122 || size <= capacity) return null;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        return null;
    }

    private static class Native
    {
        [DllImport("iphlpapi.dll", ExactSpelling = true)]
        internal static extern uint GetExtendedTcpTable(nint table, ref uint size, [MarshalAs(UnmanagedType.Bool)] bool order,
            uint family, uint tableClass, uint reserved);
        [DllImport("iphlpapi.dll", ExactSpelling = true)]
        internal static extern uint GetExtendedUdpTable(nint table, ref uint size, [MarshalAs(UnmanagedType.Bool)] bool order,
            uint family, uint tableClass, uint reserved);
    }
}
