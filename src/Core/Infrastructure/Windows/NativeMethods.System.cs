using System.Runtime.InteropServices;

namespace ResourceManager.App.Infrastructure.Windows;

internal static partial class NativeMethods
{
    [LibraryImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetPerformanceInfo(out PerformanceInformation buffer, uint size);






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



    [LibraryImport("kernel32.dll")]
    internal static partial ushort GetActiveProcessorGroupCount();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial uint GetActiveProcessorCount(ushort processorGroup);





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

[StructLayout(LayoutKind.Sequential)]
internal struct PerformanceInformation
{
    public uint Size;
    public nuint CommitTotal;
    public nuint CommitLimit;
    public nuint CommitPeak;
    public nuint PhysicalTotal;
    public nuint PhysicalAvailable;
    public nuint SystemCache;
    public nuint KernelTotal;
    public nuint KernelPaged;
    public nuint KernelNonpaged;
    public nuint PageSize;
    public uint HandleCount;
    public uint ProcessCount;
    public uint ThreadCount;
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

