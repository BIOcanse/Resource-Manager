using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace ResourceManager.ReadWrite.Windows.Devices.Hid;

internal sealed partial class WindowsHidNativeApi
{
    private static class Native
    {
        [StructLayout(LayoutKind.Sequential)]
        internal struct InterfaceData { internal uint Size; internal Guid ClassGuid; internal uint Flags; internal nint Reserved; }
        [StructLayout(LayoutKind.Sequential)]
        internal struct DeviceInfoData { internal uint Size; internal Guid ClassGuid; internal uint DevInst; internal nint Reserved; }

        [DllImport("hid.dll")]
        internal static extern void HidD_GetHidGuid(out Guid guid);
        [DllImport("hid.dll", EntryPoint = "HidP_GetVersionInternal")]
        internal static extern int HidP_GetVersionInternal(out uint version);
        [DllImport("hid.dll")]
        [return: MarshalAs(UnmanagedType.U1)]
        internal static extern bool HidD_GetPreparsedData(SafeFileHandle file, out nint data);
        [DllImport("hid.dll")]
        [return: MarshalAs(UnmanagedType.U1)]
        internal static extern bool HidD_FreePreparsedData(nint data);
        [DllImport("hid.dll")]
        internal static extern int HidP_GetCaps(nint data, out WindowsHidCaps caps);
        [DllImport("hid.dll")]
        internal static extern int HidP_GetButtonCaps(WindowsHidReportType report, [Out] WindowsHidButtonCaps[] caps,
            ref ushort count, nint data);
        [DllImport("hid.dll")]
        internal static extern int HidP_GetValueCaps(WindowsHidReportType report, [Out] WindowsHidValueCaps[] caps,
            ref ushort count, nint data);
        [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern SafeFileHandle CreateFile(string path, uint access, uint sharing, nint security,
            uint disposition, uint attributes, nint template);
        [DllImport("setupapi.dll", EntryPoint = "SetupDiGetClassDevsW", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern nint SetupDiGetClassDevs(in Guid guid, string? enumerator, nint parent, uint flags);
        [DllImport("setupapi.dll", SetLastError = true)]
        internal static extern bool SetupDiEnumDeviceInterfaces(nint set, nint device, in Guid guid, uint index, ref InterfaceData entry);
        [DllImport("setupapi.dll", EntryPoint = "SetupDiGetDeviceInterfaceDetailW", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern bool SetupDiGetDeviceInterfaceDetail(nint set, ref InterfaceData entry, nint detail,
            uint bytes, out uint required, ref DeviceInfoData device);
        [DllImport("setupapi.dll", EntryPoint = "SetupDiGetDeviceInstanceIdW", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern bool SetupDiGetDeviceInstanceId(nint set, ref DeviceInfoData device, StringBuilder id,
            uint characters, out uint required);
        [DllImport("setupapi.dll", SetLastError = true)]
        internal static extern bool SetupDiDestroyDeviceInfoList(nint set);
    }
}
