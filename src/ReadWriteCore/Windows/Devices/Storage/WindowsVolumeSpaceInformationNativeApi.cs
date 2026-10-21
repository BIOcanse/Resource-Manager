using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;

namespace ResourceManager.ReadWrite.Windows.Devices;

internal interface IWindowsVolumeSpaceNativeCalls
{
    IntPtr First(StringBuilder root, uint capacity);
    bool Next(IntPtr handle, StringBuilder root, uint capacity);
    bool Close(IntPtr handle);
    int LastError();
    int Space(string root, out WindowsVolumeSpaceNativeData value);
    string? CallerSid();
}

internal sealed class WindowsVolumeSpaceInformationNativeApi(IWindowsVolumeSpaceNativeCalls calls) : IWindowsVolumeSpaceInformationApi
{
    internal const int NoMoreFiles = 18;
    internal WindowsVolumeSpaceInformationNativeApi() : this(new NativeCalls()) { }

    public IReadOnlyList<string>? EnumerateVolumeRoots()
    {
        var buffer = new StringBuilder(1024);
        var handle = calls.First(buffer, (uint)buffer.Capacity);
        if (handle == IntPtr.Zero || handle == new IntPtr(-1))
            return calls.LastError() == NoMoreFiles ? [] : null;
        var roots = new List<string>();
        var complete = false;
        try
        {
            while (roots.Count < WindowsVolumeSpaceInformationReader.MaximumVolumes)
            {
                roots.Add(buffer.ToString());
                buffer.Clear();
                if (calls.Next(handle, buffer, (uint)buffer.Capacity)) continue;
                complete = calls.LastError() == NoMoreFiles;
                break;
            }
        }
        finally { if (!calls.Close(handle)) complete = false; }
        return complete ? roots.AsReadOnly() : null;
    }

    public int ReadSpace(string root, out WindowsVolumeSpaceNativeData space, out string? callerSid)
    {
        try { callerSid = calls.CallerSid(); }
        catch (Exception error) when (WindowsVolumeSpaceInformationReader.IsSourceFailure(error)) { callerSid = null; }
        return calls.Space(root, out space);
    }

    private sealed class NativeCalls : IWindowsVolumeSpaceNativeCalls
    {
        public IntPtr First(StringBuilder root, uint capacity) => FindFirstVolumeW(root, capacity);
        public bool Next(IntPtr handle, StringBuilder root, uint capacity) => FindNextVolumeW(handle, root, capacity);
        public bool Close(IntPtr handle) => FindVolumeClose(handle);
        public int LastError() => Marshal.GetLastPInvokeError();
        public int Space(string root, out WindowsVolumeSpaceNativeData value) => GetDiskSpaceInformationW(root, out value);
        public string? CallerSid()
        {
            if (!OperatingSystem.IsWindows()) return null;
            using var identity = WindowsIdentity.GetCurrent();
            return identity.User?.Value;
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern IntPtr FindFirstVolumeW(StringBuilder root, uint capacity);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FindNextVolumeW(IntPtr handle, StringBuilder root, uint capacity);
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FindVolumeClose(IntPtr handle);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int GetDiskSpaceInformationW(string root, out WindowsVolumeSpaceNativeData value);
}
