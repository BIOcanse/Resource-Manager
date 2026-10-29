using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ResourceManager.ReadWrite.Windows.Processes;

internal enum ProcessReadStatus { Current, Exited, Unavailable }
internal readonly record struct ProcessCreationRead(ProcessReadStatus Status, long? Creation);

internal static class WindowsProcessCreationQuery
{
    internal static ProcessCreationRead Read(int processId)
    {
        using var handle = OpenProcess(0x1000, false, processId);
        if (handle.IsInvalid)
            return new(Marshal.GetLastPInvokeError() == 87 ? ProcessReadStatus.Exited : ProcessReadStatus.Unavailable, null);
        if (!GetProcessTimes(handle, out var creation, out var exit, out _, out _))
            return new(ProcessReadStatus.Unavailable, null);
        if (exit != 0) return new(ProcessReadStatus.Exited, null);
        return creation is > 0 and <= long.MaxValue
            ? new(ProcessReadStatus.Current, (long)creation)
            : new(ProcessReadStatus.Unavailable, null);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int processId);
    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(SafeProcessHandle handle, out ulong creation, out ulong exit, out ulong kernel, out ulong user);
}
