using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace ResourceManager.App.Infrastructure.MemoryProtection;

internal static class HostMemoryProtectionNative
{
    internal const uint RequiredAccess = 0x0001 | 0x0008 | 0x0010 | 0x0400 | 0x0800 | 0x00100000;
    internal const uint MemCommit = 0x1000;
    internal const uint MemPrivate = 0x20000;
    internal const uint MemDecommit = 0x4000;

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetProcessTimes(SafeProcessHandle process, out ulong creation, out ulong exit, out ulong kernel, out ulong user);

    // ProcessBreakOnTermination (29) is the critical-process flag. Query failure is never treated as false.
    [DllImport("ntdll.dll")]
    internal static extern int NtQueryInformationProcess(SafeProcessHandle process, int informationClass, out uint information, uint length, out uint returnedLength);

    [DllImport("ntdll.dll")]
    internal static extern int NtSuspendProcess(SafeProcessHandle process);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, StringBuilder path, ref uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern nuint VirtualQueryEx(SafeProcessHandle process, nuint address, out MemoryBasicInformation information, nuint length);

    [DllImport("api-ms-win-core-memory-l1-1-4.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool QueryVirtualMemoryInformation(SafeProcessHandle process, nuint address, int informationClass,
        out MemoryRegionInformation information, nuint length, out nuint returnedLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ReadProcessMemory(SafeProcessHandle process, nuint address, byte[] buffer, nuint size, out nuint bytesRead);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool VirtualProtectEx(SafeProcessHandle process, nuint address, nuint size, uint protection, out uint previousProtection);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool VirtualFreeEx(SafeProcessHandle process, nuint address, nuint size, uint freeType);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool TerminateProcess(SafeProcessHandle process, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern uint WaitForSingleObject(SafeProcessHandle process, uint milliseconds);

    [DllImport("dbghelp.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool MiniDumpWriteDump(SafeProcessHandle process, int processId, SafeFileHandle file, uint dumpType,
        nint exception, nint userStreams, nint callback);

    [StructLayout(LayoutKind.Sequential)]
    internal struct MemoryBasicInformation
    {
        internal nuint BaseAddress;
        internal nuint AllocationBase;
        internal uint AllocationProtect;
        internal nuint RegionSize;
        internal uint State;
        internal uint Protect;
        internal uint Type;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MemoryRegionInformation
    {
        internal nuint AllocationBase;
        internal uint AllocationProtect;
        internal uint Flags;
        internal nuint RegionSize;
        internal nuint CommitSize;
    }
}
