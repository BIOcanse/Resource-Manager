using System.ComponentModel;
using System.Runtime.InteropServices;

namespace ResourceManager.ReadWrite.Windows.Devices;

internal sealed class WindowsWlanSession : IWindowsWlanSession
{
    private nint handle;
    private WindowsWlanSession(nint handle) => this.handle = handle;

    internal static IWindowsWlanSession Open()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var code = Native.WlanOpenHandle(2, 0, out _, out var handle);
        if (code == 0 && handle != 0) return new WindowsWlanSession(handle);
        if (handle != 0) Native.WlanCloseHandle(handle, 0);
        throw new Win32Exception(unchecked((int)code), "WlanOpenHandle failed.");
    }

    public byte[] ReadInterfaces() => ReadOwnedBuffer(
        (out nint buffer) => Native.WlanEnumInterfaces(handle, 0, out buffer), Native.WlanFreeMemory,
        static buffer => {
            var count = unchecked((uint)Marshal.ReadInt32(buffer));
            if (count > 1024) throw new InvalidDataException("WLAN interface count exceeds capture limit.");
            return checked(8 + (int)count * 532);
        });

    public byte[] ReadCapability(Guid guid) => ReadOwnedBuffer(
        (out nint buffer) => Native.WlanGetInterfaceCapability(handle, in guid, 0, out buffer),
        Native.WlanFreeMemory, static _ => 276);

    public byte[] ReadCurrentConnection(Guid guid)
    {
        uint length = 0;
        return ReadOwnedBuffer(
            (out nint buffer) => Native.WlanQueryInterface(handle, in guid, 7, 0, out length, out buffer, out _),
            Native.WlanFreeMemory, _ => length == 604 ? 604 : throw new InvalidDataException("WLAN connection size invalid."));
    }

    internal delegate uint ReadBuffer(out nint buffer);
    internal static byte[] ReadOwnedBuffer(ReadBuffer read, Action<nint> free, Func<nint, int> size)
    {
        nint buffer = 0;
        try
        {
            var code = read(out buffer);
            if (code != 0) throw new Win32Exception(unchecked((int)code));
            if (buffer == 0) throw new InvalidDataException("WLAN returned a null buffer.");
            var bytes = new byte[size(buffer)];
            Marshal.Copy(buffer, bytes, 0, bytes.Length);
            return bytes;
        }
        finally { if (buffer != 0) free(buffer); }
    }

    public void Dispose()
    {
        var owned = Interlocked.Exchange(ref handle, 0);
        if (owned != 0) Native.WlanCloseHandle(owned, 0);
    }

    private static class Native
    {
        [DllImport("wlanapi.dll", ExactSpelling = true)]
        internal static extern uint WlanOpenHandle(uint clientVersion, nint reserved, out uint version, out nint handle);
        [DllImport("wlanapi.dll", ExactSpelling = true)]
        internal static extern uint WlanCloseHandle(nint handle, nint reserved);
        [DllImport("wlanapi.dll", ExactSpelling = true)]
        internal static extern uint WlanEnumInterfaces(nint handle, nint reserved, out nint list);
        [DllImport("wlanapi.dll", ExactSpelling = true)]
        internal static extern uint WlanGetInterfaceCapability(nint handle, in Guid guid, nint reserved, out nint capability);
        [DllImport("wlanapi.dll", ExactSpelling = true)]
        internal static extern uint WlanQueryInterface(nint handle, in Guid guid, uint opcode, nint reserved,
            out uint size, out nint data, out uint valueType);
        [DllImport("wlanapi.dll", ExactSpelling = true)]
        internal static extern void WlanFreeMemory(nint memory);
    }
}
