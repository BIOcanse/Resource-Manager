using System.Runtime.InteropServices;

namespace ResourceManager.App.Infrastructure.Windows;

internal static partial class NativeMethods
{
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetSystemTimes(out FileTime idleTime, out FileTime kernelTime, out FileTime userTime);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetPhysicallyInstalledSystemMemory(out ulong totalMemoryInKilobytes);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetLogicalProcessorInformationEx(
        int relationshipType,
        IntPtr buffer,
        ref int returnedLength);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetSystemCpuSetInformation(
        IntPtr information,
        int bufferLength,
        out int returnedLength,
        IntPtr process,
        int flags);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetProcessDefaultCpuSets(
        IntPtr process,
        IntPtr cpuSetIds,
        uint cpuSetIdCount,
        out uint requiredIdCount);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetProcessDefaultCpuSets(
        IntPtr process,
        IntPtr cpuSetIds,
        uint cpuSetIdCount);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetThreadSelectedCpuSets(
        IntPtr thread,
        IntPtr cpuSetIds,
        uint cpuSetIdCount,
        out uint requiredIdCount);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetThreadSelectedCpuSets(
        IntPtr thread,
        IntPtr cpuSetIds,
        uint cpuSetIdCount);

    [LibraryImport("kernel32.dll")]
    internal static partial uint GetProcessIdOfThread(IntPtr thread);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetThreadTimes(
        IntPtr thread,
        out FileTime creationTime,
        out FileTime exitTime,
        out FileTime kernelTime,
        out FileTime userTime);

    [LibraryImport("ntdll.dll")]
    internal static partial int NtQuerySystemInformation(
        int systemInformationClass,
        IntPtr systemInformation,
        int systemInformationLength,
        out int returnLength);

    [LibraryImport("kernel32.dll", EntryPoint = "GetSystemFirmwareTable", SetLastError = true)]
    internal static partial uint GetSystemFirmwareTableSize(
        uint firmwareTableProvider,
        uint firmwareTableId,
        IntPtr firmwareTableBuffer,
        uint bufferSize);

    [LibraryImport("kernel32.dll", EntryPoint = "GetSystemFirmwareTable", SetLastError = true)]
    internal static partial uint GetSystemFirmwareTable(
        uint firmwareTableProvider,
        uint firmwareTableId,
        [Out] byte[] firmwareTableBuffer,
        uint bufferSize);

    [LibraryImport("powrprof.dll")]
    internal static partial uint CallNtPowerInformation(
        int informationLevel,
        IntPtr inputBuffer,
        int inputBufferLength,
        [Out] ProcessorPowerInformation[] outputBuffer,
        int outputBufferLength);
}

[StructLayout(LayoutKind.Sequential)]
internal struct FileTime
{
    public uint LowDateTime;
    public uint HighDateTime;

    public readonly ulong ToUInt64()
    {
        return ((ulong)HighDateTime << 32) | LowDateTime;
    }
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
internal struct MemoryStatusEx
{
    public uint Length;
    public uint MemoryLoad;
    public ulong TotalPhys;
    public ulong AvailPhys;
    public ulong TotalPageFile;
    public ulong AvailPageFile;
    public ulong TotalVirtual;
    public ulong AvailVirtual;
    public ulong AvailExtendedVirtual;

    public static MemoryStatusEx Create()
    {
        return new MemoryStatusEx
        {
            Length = (uint)Marshal.SizeOf<MemoryStatusEx>()
        };
    }
}

[StructLayout(LayoutKind.Sequential)]
internal struct ProcessorPowerInformation
{
    public uint Number;
    public uint MaxMhz;
    public uint CurrentMhz;
    public uint MhzLimit;
    public uint MaxIdleState;
    public uint CurrentIdleState;
}

[StructLayout(LayoutKind.Sequential)]
internal struct SystemProcessorPerformanceInformation
{
    public long IdleTime;
    public long KernelTime;
    public long UserTime;
    public long DpcTime;
    public long InterruptTime;
    public uint InterruptCount;
}
