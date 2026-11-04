using System.Runtime.InteropServices;

namespace ResourceManager.ReadWrite.Windows.Cpu;

internal static partial class WindowsCpuTopologyNativeMethods
{
    internal const int ErrorInsufficientBuffer = 122;
    internal const int SystemProcessorPerformanceInformation = 8;
    internal const int RelationProcessorCore = 0;
    internal const int RelationNumaNode = 1;
    internal const int RelationCache = 2;
    internal const int RelationProcessorPackage = 3;
    internal const int RelationProcessorDie = 5;
    internal const int RelationNumaNodeEx = 6;
    internal const int CpuSetInformation = 0;

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

    [LibraryImport("ntdll.dll")]
    internal static partial int NtQuerySystemInformationEx(
        int systemInformationClass, ref ushort processorGroup, int inputLength,
        IntPtr systemInformation, int systemInformationLength, out int returnLength);

    [LibraryImport("kernel32.dll")]
    internal static partial ushort GetActiveProcessorGroupCount();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial uint GetActiveProcessorCount(ushort processorGroup);
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
