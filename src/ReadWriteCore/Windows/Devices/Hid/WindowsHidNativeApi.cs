using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace ResourceManager.ReadWrite.Windows.Devices.Hid;

internal sealed partial class WindowsHidNativeApi : IWindowsHidCollectionApi
{
    private const uint PresentInterfaces = 0x12;
    private const int NoMoreItems = 259;
    private const int InsufficientBuffer = 122;
    private const int MaximumDetailBytes = 16384;

    [ResourceManager.ReadWrite.RuntimeRead(ResourceManager.ReadWrite.ReadVolatilityKind.EventDriven, "Windows PnP device properties changed")]

    public IReadOnlyList<WindowsHidCollection>? Enumerate()
    {
        if (!OperatingSystem.IsWindows()) return null;
        Native.HidD_GetHidGuid(out var hidGuid);
        var set = Native.SetupDiGetClassDevs(in hidGuid, null, 0, PresentInterfaces);
        if (set == new nint(-1)) return null;
        try
        {
            var collections = new List<WindowsHidCollection>();
            for (uint index = 0; index <= WindowsHidInformationReader.MaximumCollections; index++)
            {
                var entry = new Native.InterfaceData { Size = (uint)Marshal.SizeOf<Native.InterfaceData>() };
                if (!Native.SetupDiEnumDeviceInterfaces(set, 0, in hidGuid, index, ref entry))
                    return Marshal.GetLastWin32Error() == NoMoreItems ? collections.AsReadOnly() : null;
                if (index == WindowsHidInformationReader.MaximumCollections) return null;
                var info = new Native.DeviceInfoData { Size = (uint)Marshal.SizeOf<Native.DeviceInfoData>() };
                if (Native.SetupDiGetDeviceInterfaceDetail(set, ref entry, 0, 0, out var required, ref info)
                    || Marshal.GetLastWin32Error() != InsufficientBuffer || required < 6 || required > MaximumDetailBytes) return null;
                var detail = Marshal.AllocHGlobal(checked((int)required));
                try
                {
                    // DevicePath begins at offset 4 in both ABIs; cbSize is 8 on x64 and 6 on x86.
                    Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                    if (!Native.SetupDiGetDeviceInterfaceDetail(set, ref entry, detail, required, out var actual, ref info)
                        || actual > required || actual < 6) return null;
                    var bytes = new byte[actual];
                    Marshal.Copy(detail, bytes, 0, bytes.Length);
                    var path = DecodeInterfacePath(bytes);
                    var id = new StringBuilder(512);
                    if (path is null || !Native.SetupDiGetDeviceInstanceId(set, ref info, id, (uint)id.Capacity, out _)
                        || string.IsNullOrWhiteSpace(id.ToString())) return null;
                    collections.Add(new(path, id.ToString()));
                }
                finally { Marshal.FreeHGlobal(detail); }
            }
            return null;
        }
        finally { _ = Native.SetupDiDestroyDeviceInfoList(set); }
    }

    internal static string? DecodeInterfacePath(byte[] details)
        => WindowsDeviceInterfacePathDecoder.Decode(details);

    public uint? ReadVersion() => ReadVersion(static () => {
        var status = Native.HidP_GetVersionInternal(out var version);
        return (status, version);
    });

    internal static uint? ReadVersion(Func<(int Status, uint Version)> read)
    {
        // Mirrors the official header-only HidP_GetVersion implementation.
        try { var result = read(); return result.Status >= 0 && result.Version >= 1 ? result.Version : null; }
        catch (EntryPointNotFoundException) { return 1; }
    }

    public IWindowsHidCollectionSession? Open(WindowsHidCollection collection)
    {
        var file = Native.CreateFile(collection.DevicePath, 0, 3, 0, 3, 0x80, 0);
        if (file.IsInvalid) { file.Dispose(); return null; }
        try
        {
            if (!Native.HidD_GetPreparsedData(file, out var preparsed) || preparsed == 0)
            { file.Dispose(); return null; }
            return new Session(file, preparsed);
        }
        catch { file.Dispose(); throw; }
    }

    private sealed class Session(SafeFileHandle file, nint preparsed) : IWindowsHidCollectionSession
    {
        private nint data = preparsed;
        public int ReadCaps(out WindowsHidCaps caps) => Native.HidP_GetCaps(data, out caps);
        public int ReadButtons(WindowsHidReportType report, WindowsHidButtonCaps[] caps, ref ushort count)
            => Native.HidP_GetButtonCaps(report, caps, ref count, data);
        public int ReadValues(WindowsHidReportType report, WindowsHidValueCaps[] caps, ref ushort count)
            => Native.HidP_GetValueCaps(report, caps, ref count, data);
        public void Dispose()
        {
            var previous = Interlocked.Exchange(ref data, 0);
            try { if (previous != 0) _ = Native.HidD_FreePreparsedData(previous); }
            finally { file.Dispose(); }
        }
    }
}
