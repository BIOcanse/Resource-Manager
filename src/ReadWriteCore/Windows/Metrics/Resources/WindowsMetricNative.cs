using System.Runtime.InteropServices;
namespace ResourceManager.ReadWrite.Windows.Metrics.Resources;
internal static partial class WindowsMetricNative
{
    internal const int ProcessorInformation = 11;
    [LibraryImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetPerformanceInfo(out PerformanceInformation buffer, uint size);
    [DllImport("powrprof.dll")]
    internal static extern uint CallNtPowerInformation(int informationLevel, IntPtr inputBuffer, int inputBufferLength,
        [Out] ProcessorPowerInformation[] outputBuffer, int outputBufferLength);
}
[StructLayout(LayoutKind.Sequential)]
public struct PerformanceInformation
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
public struct ProcessorPowerInformation
{
    public uint Number;
    public uint MaxMhz;
    public uint CurrentMhz;
    public uint MhzLimit;
    public uint MaxIdleState;
    public uint CurrentIdleState;
}


