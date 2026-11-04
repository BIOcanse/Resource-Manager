using System.Runtime.InteropServices;
using ResourceManager.ReadWrite.ProcessFields;

namespace ResourceManager.ReadWrite.Windows.Processes;

internal static class WindowsProcessResidentMemory
{
    // EX2 was added in the September 2023 cumulative updates for Windows 10/11 22H2.
    internal static bool SupportsPrivateWorkingSet =>
        OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621, 2361)
        || (Environment.OSVersion.Version.Build < 22000 && OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19045, 3448));

    internal static bool TryRead(int processId, long processStartKey, out ResidentMemory memory)
    {
        memory = default;
        if (!SupportsPrivateWorkingSet || processId <= 0 || processStartKey <= 0) return false;
        var observed = new WindowsProcessBasicMetricReader(new WindowsProcessBasicMetricNativeApi()).Read(processId, processStartKey,
            ProcessMetricReadMask.Memory | ProcessMetricReadMask.PrivateWorkingSet).Memory;
        if (observed?.PrivateWorkingSet is not { } resident || resident > long.MaxValue || observed.WorkingSet > long.MaxValue) return false;
        memory = new((long)resident, (long)observed.WorkingSet); return true;
    }

    internal static ProcessMemoryObservation? ReadNative(IntPtr handle, bool privateWorkingSet)
    {
        var extended = privateWorkingSet && SupportsPrivateWorkingSet;
        var size = extended ? checked((uint)Marshal.SizeOf<ProcessMemoryCountersEx2>())
            : checked((uint)Marshal.OffsetOf<ProcessMemoryCountersEx2>(nameof(ProcessMemoryCountersEx2.PrivateWorkingSetSize)).ToInt32());
        var counters = new ProcessMemoryCountersEx2 { Size = size };
        return GetProcessMemoryInfo(handle, ref counters, size)
            ? new((ulong)counters.WorkingSetSize, (ulong)counters.PrivateUsage, (ulong)counters.PeakPagefileUsage,
                extended ? (ulong)counters.PrivateWorkingSetSize : null) : null;
    }

    internal readonly record struct ResidentMemory(long PrivateResidentBytes, long WorkingSetBytes);

    [StructLayout(LayoutKind.Sequential)]
    internal struct ProcessMemoryCountersEx2
    {
        public uint Size, PageFaultCount;
        public nuint PeakWorkingSetSize, WorkingSetSize, QuotaPeakPagedPoolUsage, QuotaPagedPoolUsage,
            QuotaPeakNonPagedPoolUsage, QuotaNonPagedPoolUsage, PagefileUsage, PeakPagefileUsage, PrivateUsage,
            PrivateWorkingSetSize;
        public ulong SharedCommitUsage;
    }
    [DllImport("kernel32.dll", EntryPoint = "K32GetProcessMemoryInfo", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessMemoryInfo(IntPtr process, ref ProcessMemoryCountersEx2 counters, uint size);
}
