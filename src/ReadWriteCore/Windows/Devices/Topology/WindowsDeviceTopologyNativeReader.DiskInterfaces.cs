using ResourceManager.ReadWrite.Windows.Devices.Models;
using System.Runtime.InteropServices;

namespace ResourceManager.ReadWrite.Windows.Devices;

internal interface IWindowsDiskInterfaceDirectoryCalls
{
    nint Open();
    bool Next(nint set, uint index, ref WindowsDeviceTopologyNativeReader.DiskInterfaceData entry);
    bool Detail(nint set, ref WindowsDeviceTopologyNativeReader.DiskInterfaceData entry, nint buffer, uint capacity,
        out uint required, ref WindowsDeviceTopologyNativeReader.SpDevInfoData info);
    string? Identity(nint set, ref WindowsDeviceTopologyNativeReader.SpDevInfoData info);
    bool Close(nint set);
    int LastError();
}

public static partial class WindowsDeviceTopologyNativeReader
{
    private static readonly Guid DiskInterfaceClass = new("53f56307-b6bf-11d0-94f2-00a0c91efb8b");
    internal static IReadOnlyList<WindowsDiskInterface>? ReadDiskInterfaces()
        => ReadDiskInterfaces(new DiskInterfaceDirectoryCalls());

    internal static IReadOnlyList<WindowsDiskInterface>? ReadDiskInterfaces(IWindowsDiskInterfaceDirectoryCalls calls)
    {
        var set = calls.Open();
        if (set == InvalidHandleValue || set == 0) return null;
        var complete = false;
        var result = new List<WindowsDiskInterface>();
        try
        {
            for (uint index = 0; index <= WindowsDiskLayoutInformationReader.MaximumDisks; index++)
            {
                var entry = new DiskInterfaceData { CbSize = (uint)Marshal.SizeOf<DiskInterfaceData>() };
                if (!calls.Next(set, index, ref entry))
                { complete = calls.LastError() == ErrorNoMoreItems; break; }
                if (index == WindowsDiskLayoutInformationReader.MaximumDisks) break;
                var info = new SpDevInfoData { CbSize = (uint)Marshal.SizeOf<SpDevInfoData>() };
                if (calls.Detail(set, ref entry, 0, 0, out var required, ref info)
                    || calls.LastError() != ErrorInsufficientBuffer || required < 6 || required > 16384) break;
                var detail = Marshal.AllocHGlobal((int)required);
                try
                {
                    Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                    if (!calls.Detail(set, ref entry, detail, required, out var actual, ref info)
                        || actual < 6 || actual > required) break;
                    var bytes = new byte[actual]; Marshal.Copy(detail, bytes, 0, bytes.Length);
                    var path = WindowsDeviceInterfacePathDecoder.Decode(bytes);
                    var identity = calls.Identity(set, ref info);
                    if (path is null || identity is null) break;
                    result.Add(new(path, identity));
                }
                finally { Marshal.FreeHGlobal(detail); }
            }
        }
        finally
        {
            if (!calls.Close(set)) complete = false;
        }
        return complete ? result.AsReadOnly() : null;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct DiskInterfaceData { public uint CbSize; public Guid ClassGuid; public uint Flags; public nint Reserved; }
    private sealed class DiskInterfaceDirectoryCalls : IWindowsDiskInterfaceDirectoryCalls
    {
        public nint Open() => SetupDiGetDiskClassDevs(in DiskInterfaceClass, null, 0, DigcfPresent | 0x10);
        public bool Next(nint set, uint index, ref DiskInterfaceData entry) => SetupDiEnumDeviceInterfaces(set, 0, in DiskInterfaceClass, index, ref entry);
        public bool Detail(nint set, ref DiskInterfaceData entry, nint buffer, uint capacity, out uint required, ref SpDevInfoData info)
            => SetupDiGetDeviceInterfaceDetailW(set, ref entry, buffer, capacity, out required, ref info);
        public string? Identity(nint set, ref SpDevInfoData info) => ReadDeviceInstanceId(set, ref info);
        public bool Close(nint set) => SetupDiDestroyDeviceInfoList(set);
        public int LastError() => Marshal.GetLastPInvokeError();
    }
    [DllImport("setupapi.dll", EntryPoint = "SetupDiGetClassDevsW", ExactSpelling = true, SetLastError = true, CharSet = CharSet.Unicode)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern nint SetupDiGetDiskClassDevs(in Guid guid, string? enumerator, nint parent, uint flags);
    [DllImport("setupapi.dll", ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiEnumDeviceInterfaces(nint set, nint device, in Guid guid, uint index, ref DiskInterfaceData entry);
    [DllImport("setupapi.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceInterfaceDetailW(nint set, ref DiskInterfaceData entry, nint detail, uint capacity, out uint required, ref SpDevInfoData info);
}
