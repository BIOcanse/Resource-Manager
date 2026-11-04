using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace ResourceManager.ReadWrite.Windows.Devices.Battery;

internal static class WindowsBatteryNative
{
    private static readonly Guid BatteryInterface = new("72631e54-78a4-11d0-bcf7-00aa00b7b32a");
    [ResourceManager.ReadWrite.RuntimeRead(ResourceManager.ReadWrite.ReadVolatilityKind.EventDriven, "Windows PnP device properties changed")]
    internal static IReadOnlyList<string>? Enumerate()
    {
        if (!OperatingSystem.IsWindows()) return null;
        var id = BatteryInterface;
        var devices = SetupDiGetClassDevsW(ref id, null, 0, 0x12); // PRESENT | DEVICEINTERFACE.
        if (devices == new nint(-1)) return null;
        try
        {
            var paths = new List<string>();
            for (uint index = 0; index <= 32; index++)
            {
                var data = new DeviceInterfaceData { Size = (uint)Marshal.SizeOf<DeviceInterfaceData>() };
                if (!SetupDiEnumDeviceInterfaces(devices, 0, ref id, index, ref data))
                    return Marshal.GetLastWin32Error() == 259 ? paths : null;
                if (index == 32) return null;
                SetupDiGetDeviceInterfaceDetailW(devices, ref data, 0, 0, out var required, 0);
                if (Marshal.GetLastWin32Error() != 122 || required is < 6 or > 8192) return null;
                var buffer = Marshal.AllocHGlobal((int)required);
                try
                {
                    Marshal.WriteInt32(buffer, IntPtr.Size == 8 ? 8 : 6);
                    if (!SetupDiGetDeviceInterfaceDetailW(devices, ref data, buffer, required, out _, 0)) return null;
                    var bytes = new byte[(required - 4) & ~1U];
                    Marshal.Copy(buffer + 4, bytes, 0, bytes.Length);
                    var text = Encoding.Unicode.GetString(bytes);
                    var end = text.IndexOf('\0');
                    if (end <= 0) return null;
                    paths.Add(text[..end]);
                }
                finally { Marshal.FreeHGlobal(buffer); }
            }
            return null;
        }
        finally { SetupDiDestroyDeviceInfoList(devices); }
    }

    internal static IWindowsBatteryQuery? Open(string path)
    {
        // All three query IOCTLs require FILE_READ_ACCESS. No write access is requested.
        var handle = CreateFileW(path, 0x80000000, 3, 0, 3, 0, 0);
        if (!handle.IsInvalid) return new QueryHandle(handle);
        handle.Dispose();
        return null;
    }

    private sealed class QueryHandle(SafeFileHandle handle) : IWindowsBatteryQuery
    {
        public int LastError { get; private set; }
        public byte[]? Query(uint controlCode, byte[] input, int maximumOutputBytes)
        {
            if (controlCode is not (WindowsBatteryReader.QueryTag or WindowsBatteryReader.QueryInformation
                or WindowsBatteryReader.QueryStatus)) throw new ArgumentOutOfRangeException(nameof(controlCode));
            var output = new byte[maximumOutputBytes];
            var success = DeviceIoControl(handle, controlCode, input, (uint)input.Length, output,
                (uint)output.Length, out var written, 0);
            LastError = success ? 0 : Marshal.GetLastWin32Error();
            if (!success || written > output.Length) return null;
            return output[..(int)written];
        }
        public void Dispose() => handle.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceInterfaceData
    {
        internal uint Size;
        internal Guid ClassGuid;
        internal uint Flags;
        internal nint Reserved;
    }
    [DllImport("setupapi.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint SetupDiGetClassDevsW(ref Guid classGuid, string? enumerator, nint parent, uint flags);
    [DllImport("setupapi.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiEnumDeviceInterfaces(nint devices, nint device, ref Guid classGuid,
        uint index, ref DeviceInterfaceData data);
    [DllImport("setupapi.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceInterfaceDetailW(nint devices, ref DeviceInterfaceData data,
        nint detail, uint size, out uint required, nint info);
    [DllImport("setupapi.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiDestroyDeviceInfoList(nint devices);
    [DllImport("kernel32.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, nint security,
        uint disposition, uint flags, nint template);
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle handle, uint code, byte[] input, uint inputSize,
        [Out] byte[] output, uint outputSize, out uint written, nint overlapped);
}
