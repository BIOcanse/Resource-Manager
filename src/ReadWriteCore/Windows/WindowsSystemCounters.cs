using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace ResourceManager.ReadWrite.Windows;

public readonly record struct WindowsCpuCounters(bool IsAvailable, ulong IdleTicks, ulong KernelTicks, ulong UserTicks,
    ulong MonotonicTicks, ulong MonotonicTicksPerSecond, DateTimeOffset ObservedAt);
public readonly record struct WindowsPhysicalMemory(bool IsAvailable, ulong TotalBytes, ulong AvailableBytes);

/// <summary>The product's original read-only Windows primitives. Calling a read
/// explicitly collects; loading this type starts no worker and owns no OS handle.
/// GetSystemTimes retains its Windows processor-group scope, not per-core attribution.</summary>
[SupportedOSPlatform("windows")]
public static partial class WindowsSystemCounters
{
    public static WindowsCpuCounters ReadCpuCounters()
    {
        var observedAt = DateTimeOffset.UtcNow;
        if (!GetSystemTimes(out var idle, out var kernel, out var user))
            return new(false, 0, 0, 0, 0, 0, observedAt);
        return new(true, idle.ToUInt64(), kernel.ToUInt64(), user.ToUInt64(),
            checked((ulong)Stopwatch.GetTimestamp()), checked((ulong)Stopwatch.Frequency), observedAt);
    }

    public static WindowsPhysicalMemory ReadPhysicalMemory()
    {
        var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        return GlobalMemoryStatusEx(ref status) ? new(true, status.TotalPhys, status.AvailPhys) : new(false, 0, 0);
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetSystemTimes(out FileTime idleTime, out FileTime kernelTime, out FileTime userTime);
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        public uint LowDateTime;
        public uint HighDateTime;
        public readonly ulong ToUInt64() => ((ulong)HighDateTime << 32) | LowDateTime;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MemoryStatusEx
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
    }
}
