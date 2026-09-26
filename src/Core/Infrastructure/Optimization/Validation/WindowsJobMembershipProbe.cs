using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ResourceManager.App.Infrastructure.Optimization;

internal sealed partial class WindowsJobMembershipProbe : IWindowsJobMembershipProbe
{
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint JobObjectQuery = 0x0004;

    public WindowsJobMembershipResult Probe(
        string jobName,
        HostManagerComputeProcessIdentity identity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobName);
        if (!OperatingSystem.IsWindows())
        {
            return new(WindowsJobMembershipStatus.Unknown, 0);
        }
        if (identity.ProcessId <= 0 || identity.ProcessStartKey == 0)
        {
            return new(WindowsJobMembershipStatus.IdentityMismatch, 0);
        }

        using var process = OpenProcess(
            ProcessQueryLimitedInformation,
            inheritHandle: false,
            checked((uint)identity.ProcessId));
        if (process.IsInvalid)
        {
            return new(
                WindowsJobMembershipStatus.Unknown,
                Marshal.GetLastPInvokeError());
        }
        if (!GetProcessTimes(
                process,
                out var creationTime,
                out _,
                out _,
                out _))
        {
            return new(
                WindowsJobMembershipStatus.Unknown,
                Marshal.GetLastPInvokeError());
        }
        if (creationTime.ToUInt64() != identity.ProcessStartKey)
        {
            return new(WindowsJobMembershipStatus.IdentityMismatch, 0);
        }

        using var job = OpenJobObject(JobObjectQuery, inheritHandle: false, jobName);
        if (job.IsInvalid)
        {
            return new(
                WindowsJobMembershipStatus.Unknown,
                Marshal.GetLastPInvokeError());
        }
        if (!IsProcessInJob(process, job, out var isMember))
        {
            return new(
                WindowsJobMembershipStatus.Unknown,
                Marshal.GetLastPInvokeError());
        }
        return new(
            isMember
                ? WindowsJobMembershipStatus.ExactMember
                : WindowsJobMembershipStatus.NotMember,
            0);
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial SafeProcessHandle OpenProcess(
        uint desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
        uint processId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetProcessTimes(
        SafeProcessHandle process,
        out NativeFileTime creationTime,
        out NativeFileTime exitTime,
        out NativeFileTime kernelTime,
        out NativeFileTime userTime);

    [LibraryImport(
        "kernel32.dll",
        EntryPoint = "OpenJobObjectW",
        SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle OpenJobObject(
        uint desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
        string name);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsProcessInJob(
        SafeProcessHandle process,
        SafeFileHandle job,
        [MarshalAs(UnmanagedType.Bool)] out bool result);

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct NativeFileTime
    {
        private readonly uint low;
        private readonly uint high;

        internal ulong ToUInt64() => ((ulong)high << 32) | low;
    }
}
