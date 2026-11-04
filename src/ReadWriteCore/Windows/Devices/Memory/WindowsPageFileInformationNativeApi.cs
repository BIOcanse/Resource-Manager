using System.Runtime.InteropServices;
using System.Text;
using ResourceManager.ReadWrite.Windows.Devices.Models;

namespace ResourceManager.ReadWrite.Windows.Devices;

[UnmanagedFunctionPointer(CallingConvention.Winapi)]
[return: MarshalAs(UnmanagedType.Bool)]
internal delegate bool WindowsPageFileCallback(IntPtr context, IntPtr information, IntPtr filename);

internal interface IWindowsPageFileNativeCalls
{
    bool Enumerate(WindowsPageFileCallback callback);
    uint PageSize();
}

[StructLayout(LayoutKind.Sequential)]
internal struct WindowsPageFileNativeData
{
    internal uint Size;
    internal uint Reserved;
    internal nuint TotalSize;
    internal nuint TotalInUse;
    internal nuint PeakUsage;
}

[StructLayout(LayoutKind.Sequential)]
internal struct WindowsPageFileSystemInfo
{
    internal uint ArchitectureUnion;
    internal uint PageSize;
    internal IntPtr MinimumAddress;
    internal IntPtr MaximumAddress;
    internal nuint ProcessorMask;
    internal uint ProcessorCount;
    internal uint ProcessorType;
    internal uint AllocationGranularity;
    internal ushort ProcessorLevel;
    internal ushort ProcessorRevision;
}

internal sealed class WindowsPageFileInformationNativeApi(IWindowsPageFileNativeCalls calls) : IWindowsPageFileInformationApi
{
    internal WindowsPageFileInformationNativeApi() : this(new NativeCalls()) { }

    public IReadOnlyList<DeviceTopologyPageFileInformation>? Read()
    {
        uint? pageSize = null;
        try { if (calls.PageSize() is var size && size != 0) pageSize = size; }
        catch (Exception error) when (WindowsVolumeSpaceInformationReader.IsSourceFailure(error)) { }
        var files = new List<DeviceTopologyPageFileInformation>();
        var identities = new HashSet<string>(StringComparer.Ordinal);
        var failed = false;
        bool Capture(IntPtr _, IntPtr information, IntPtr filename)
        {
            if (failed) return false;
            // Never unwind a managed exception through the OS callback stack.
            try
            {
                var path = ReadFilename(filename);
                if (path is null || files.Count == WindowsPageFileInformationReader.MaximumFiles
                    || !identities.Add(path)) { failed = true; return false; }
                var file = new DeviceTopologyPageFileInformation("windows-pagefile|" + path) { PathReported = path, PageSizeBytes = pageSize };
                if (information != IntPtr.Zero && (uint)Marshal.ReadInt32(information) >= Marshal.SizeOf<WindowsPageFileNativeData>())
                {
                    var data = Marshal.PtrToStructure<WindowsPageFileNativeData>(information);
                    file = file with { CapacityPages = (ulong)data.TotalSize, UsedPages = (ulong)data.TotalInUse,
                        PeakUsedPages = (ulong)data.PeakUsage, PageSizeBytes = pageSize };
                }
                files.Add(file);
                return true;
            }
            catch (Exception) { failed = true; return false; }
        }
        // Both the API result and our callback's completion matter: an abort may return success.
        return calls.Enumerate(Capture) && !failed ? files.AsReadOnly() : null;
    }

    private static string? ReadFilename(IntPtr filename)
    {
        if (filename == IntPtr.Zero) return null;
        var text = new StringBuilder();
        for (var index = 0; index <= WindowsPageFileInformationReader.MaximumPathCharacters; index++)
        {
            var character = (char)(ushort)Marshal.ReadInt16(filename, index * sizeof(char));
            if (character == '\0') return text.Length == 0 ? null : text.ToString();
            if (index == WindowsPageFileInformationReader.MaximumPathCharacters) return null;
            text.Append(character);
        }
        return null;
    }

    private sealed class NativeCalls : IWindowsPageFileNativeCalls
    {
        public bool Enumerate(WindowsPageFileCallback callback) => K32EnumPageFilesW(callback, IntPtr.Zero);
        public uint PageSize() { GetNativeSystemInfo(out var info); return info.PageSize; }
    }

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool K32EnumPageFilesW(WindowsPageFileCallback callback, IntPtr context);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern void GetNativeSystemInfo(out WindowsPageFileSystemInfo info);
}
