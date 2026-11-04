using System.Runtime.InteropServices;

namespace ResourceManager.ReadWrite.Windows.Devices;

internal sealed class WindowsNetworkInterfaceTableApi : IWindowsNetworkInterfaceTableApi
{
    internal delegate uint ReadTable(out nint table);
    public byte[]? ReadInterfaces() => OperatingSystem.IsWindows()
        ? ReadOwnedTable(Native.GetIfTable2, Native.FreeMibTable, WindowsNetworkInterfaceTableDecoder.InterfaceRowSize,
            WindowsNetworkInterfaceTableDecoder.MaximumInterfaces) : null;
    public byte[]? ReadUnicast() => OperatingSystem.IsWindows()
        ? ReadOwnedTable(static (out nint table) => Native.GetUnicastIpAddressTable(0, out table), Native.FreeMibTable,
            WindowsNetworkInterfaceTableDecoder.UnicastRowSize, WindowsNetworkInterfaceTableDecoder.MaximumAddresses) : null;
    public byte[]? ReadRoutes() => OperatingSystem.IsWindows()
        ? ReadOwnedTable(static (out nint table) => Native.GetIpForwardTable2(0, out table), Native.FreeMibTable,
            WindowsNetworkInterfaceTableDecoder.RouteRowSize, WindowsNetworkInterfaceTableDecoder.MaximumRoutes) : null;

    internal static byte[]? ReadOwnedTable(ReadTable read, Action<nint> free, int stride, int maximum)
    {
        nint table = 0;
        try
        {
            if (read(out table) != 0 || table == 0) return null;
            var count = unchecked((uint)Marshal.ReadInt32(table));
            if (count > maximum) return null;
            var bytes = new byte[checked(WindowsNetworkInterfaceTableDecoder.TableOffset + (int)count * stride)];
            Marshal.Copy(table, bytes, 0, bytes.Length);
            return bytes;
        }
        finally { if (table != 0) free(table); }
    }

    private static class Native
    {
        [DllImport("iphlpapi.dll", ExactSpelling = true)]
        internal static extern uint GetIfTable2(out nint table);
        [DllImport("iphlpapi.dll", ExactSpelling = true)]
        internal static extern uint GetUnicastIpAddressTable(ushort family, out nint table);
        [DllImport("iphlpapi.dll", ExactSpelling = true)]
        internal static extern uint GetIpForwardTable2(ushort family, out nint table);
        [DllImport("iphlpapi.dll", ExactSpelling = true)]
        internal static extern void FreeMibTable(nint table);
    }
}
