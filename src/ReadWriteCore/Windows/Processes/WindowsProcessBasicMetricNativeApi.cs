using System.Runtime.InteropServices;

namespace ResourceManager.ReadWrite.Windows.Processes;

internal sealed partial class WindowsProcessBasicMetricNativeApi : IWindowsProcessBasicMetricApi
{
    public IntPtr Open(int processId) => OpenProcess(0x1000, false, processId);
    public bool Times(IntPtr handle, out ulong creation, out ulong kernel, out ulong user)
    {
        var success = GetProcessTimes(handle, out var created, out _, out var kernelTime, out var userTime);
        creation = created.ToUInt64(); kernel = kernelTime.ToUInt64(); user = userTime.ToUInt64(); return success;
    }
    public ProcessMemoryObservation? Memory(IntPtr handle, bool privateWorkingSet) => WindowsProcessResidentMemory.ReadNative(handle, privateWorkingSet);
    public ProcessIoObservation? Io(IntPtr handle)
        => GetProcessIoCounters(handle, out var value)
            ? new(value.ReadOperationCount, value.WriteOperationCount, value.OtherOperationCount, value.ReadTransferCount, value.WriteTransferCount, value.OtherTransferCount) : null;
    public uint? HandleCount(IntPtr handle) => GetProcessHandleCount(handle, out var value) ? value : null;
    public bool Close(IntPtr handle) => CloseHandle(handle);

    [StructLayout(LayoutKind.Sequential)]
    internal struct FileTime
    {
        public uint LowDateTime, HighDateTime;
        public readonly ulong ToUInt64() => ((ulong)HighDateTime << 32) | LowDateTime;
    }

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static extern IntPtr OpenProcess(uint processAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, int processId);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetProcessTimes(IntPtr process, out FileTime creationTime,
        out FileTime exitTime, out FileTime kernelTime, out FileTime userTime);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CloseHandle(IntPtr handle);

    [StructLayout(LayoutKind.Sequential)]
    internal struct IoCounters
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessIoCounters(IntPtr handle, out IoCounters counters);
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessHandleCount(IntPtr handle, out uint count);
}
