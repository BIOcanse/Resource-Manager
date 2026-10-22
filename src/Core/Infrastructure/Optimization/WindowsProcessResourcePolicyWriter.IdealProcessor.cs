using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using ResourceManager.App.Application.Optimization;
using ResourceManager.App.Domain.Optimization;
using ResourceManager.App.Infrastructure.Windows;

namespace ResourceManager.App.Infrastructure.Optimization;

public sealed partial class WindowsProcessResourcePolicyWriter
{
    public RecoveryReadResult<IReadOnlyList<ThreadIdealProcessorPolicySnapshot>> ReadProcessThreadIdealProcessors(
        int processId, ulong processStartKey, int maximumThreadCount)
    {
        if (processId <= 4 || processStartKey == 0 || maximumThreadCount <= 0)
            return RecoveryReadResult<IReadOnlyList<ThreadIdealProcessorPolicySnapshot>>.Unavailable(87, "Invalid thread enumeration boundary.");
        var process = ReadProcessInstanceForRecovery(processId);
        if (process.Status != RecoveryReadStatus.Found || process.Value is null)
            return new(process.Status, null, process.NativeErrorCode, process.Message);
        if (checked((ulong)process.Value.StartedAt.ToFileTime()) != processStartKey)
            return RecoveryReadResult<IReadOnlyList<ThreadIdealProcessorPolicySnapshot>>.NotFoundOrExited(0, "Process creation identity changed.");
        try
        {
            using var live = Process.GetProcessById(processId);
            var threads = live.Threads;
            try
            {
                if (threads.Count > maximumThreadCount)
                    return RecoveryReadResult<IReadOnlyList<ThreadIdealProcessorPolicySnapshot>>.Unavailable(122, "Thread enumeration exceeds the placement capacity.");
                var results = new List<ThreadIdealProcessorPolicySnapshot>(threads.Count);
                foreach (ProcessThread thread in threads)
                {
                    var read = ReadThreadIdealProcessor(processId, processStartKey, thread.Id);
                    if (read.Status == RecoveryReadStatus.Found && read.Value is not null) results.Add(read.Value);
                    // Exited and inaccessible threads receive no new effect. Existing receipts are read separately.
                }
                return RecoveryReadResult<IReadOnlyList<ThreadIdealProcessorPolicySnapshot>>.Found(results.OrderBy(static t => t.ThreadId).ToArray());
            }
            finally { foreach (ProcessThread thread in threads) thread.Dispose(); }
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException or ArgumentException)
        {
            return RecoveryReadResult<IReadOnlyList<ThreadIdealProcessorPolicySnapshot>>.Unavailable(error.HResult, error.Message);
        }
    }

    public RecoveryReadResult<ThreadIdealProcessorPolicySnapshot> ReadThreadIdealProcessor(
        int processId, ulong processStartKey, int threadId)
    {
        if (processId <= 4 || processStartKey == 0 || threadId <= 0)
            return RecoveryReadResult<ThreadIdealProcessorPolicySnapshot>.Unavailable(87, "Invalid thread identity.");
        var thread = NativeMethods.OpenThread(0x0040 | NativeMethods.ThreadQueryLimitedInformation, false, checked((uint)threadId));
        if (thread == IntPtr.Zero) return IdealReadFailure(Marshal.GetLastWin32Error());
        try { return ReadIdealOnHandle(thread, processId, processStartKey, threadId); }
        finally { NativeMethods.CloseHandle(thread); }
    }

    public ProcessResourcePolicyWriteResult TrySetThreadIdealProcessor(
        ThreadIdealProcessorPolicySnapshot expected, ThreadIdealProcessor desired)
    {
        if (desired.Group != expected.AllowedGroup || desired.Number >= 64
            || (expected.AllowedMask & (1UL << desired.Number)) == 0)
            return new(false, "Ideal processor is outside the observed thread group affinity.");
        var thread = NativeMethods.OpenThread(NativeMethods.ThreadSetInformation | 0x0040
            | NativeMethods.ThreadQueryLimitedInformation, false, checked((uint)expected.ThreadId));
        if (thread == IntPtr.Zero) return new(false, new Win32Exception(Marshal.GetLastWin32Error()).Message);
        try
        {
            var current = ReadIdealOnHandle(thread, expected.ProcessId, expected.ProcessStartKey, expected.ThreadId);
            if (current.Status != RecoveryReadStatus.Found || current.Value is not { } actual
                || actual.ThreadStartKey != expected.ThreadStartKey || actual.IdealProcessor != expected.IdealProcessor
                || actual.AllowedGroup != expected.AllowedGroup || actual.AllowedMask != expected.AllowedMask
                || !actual.SelectedCpuSetIds.SequenceEqual(expected.SelectedCpuSetIds))
                return new(false, "Thread identity, allowed range or ideal processor changed before the write.");
            var value = new IdealProcessorNumber { Group = desired.Group, Number = desired.Number };
            if (!SetThreadIdealProcessorEx(thread, in value, out _))
                return new(false, new Win32Exception(Marshal.GetLastWin32Error()).Message);
            return new(true, "Thread ideal processor hint applied; affinity and CPU Sets are unchanged.");
        }
        finally { NativeMethods.CloseHandle(thread); }
    }

    private RecoveryReadResult<ThreadIdealProcessorPolicySnapshot> ReadIdealOnHandle(
        IntPtr thread, int processId, ulong processStartKey, int threadId)
    {
        if (NativeMethods.GetProcessIdOfThread(thread) != checked((uint)processId))
            return RecoveryReadResult<ThreadIdealProcessorPolicySnapshot>.NotFoundOrExited(0, "Thread belongs to another process.");
        var parent = ReadProcessInstanceForRecovery(processId);
        if (parent.Status != RecoveryReadStatus.Found || parent.Value is null)
            return new(parent.Status, null, parent.NativeErrorCode, parent.Message);
        if (checked((ulong)parent.Value.StartedAt.ToFileTime()) != processStartKey)
            return RecoveryReadResult<ThreadIdealProcessorPolicySnapshot>.NotFoundOrExited(0, "Process creation identity changed.");
        if (!NativeMethods.GetThreadTimes(thread, out var creation, out var exit, out _, out _))
            return IdealReadFailure(Marshal.GetLastWin32Error());
        if (exit.ToUInt64() != 0)
            return RecoveryReadResult<ThreadIdealProcessorPolicySnapshot>.NotFoundOrExited(0, "Thread exited.");
        if (!GetThreadIdealProcessorEx(thread, out var ideal) || !GetThreadGroupAffinity(thread, out var affinity))
            return IdealReadFailure(Marshal.GetLastWin32Error());
        IReadOnlyList<uint> selected = [];
        if (OperatingSystem.IsWindowsVersionAtLeast(10))
        {
            var sets = TryReadThreadSelectedCpuSets(processId, threadId);
            if (sets is null || checked((ulong)sets.CreatedAt.ToFileTime()) != creation.ToUInt64())
                return RecoveryReadResult<ThreadIdealProcessorPolicySnapshot>.Unavailable(0, "Thread CPU Sets could not be verified.");
            selected = sets.CpuSetIds;
        }
        return RecoveryReadResult<ThreadIdealProcessorPolicySnapshot>.Found(new(processId, processStartKey, threadId,
            creation.ToUInt64(), new(ideal.Group, ideal.Number), affinity.Group, affinity.Mask.ToUInt64(), selected));
    }

    private static RecoveryReadResult<ThreadIdealProcessorPolicySnapshot> IdealReadFailure(int error)
        => error is 87 or 1168
            ? RecoveryReadResult<ThreadIdealProcessorPolicySnapshot>.NotFoundOrExited(error, new Win32Exception(error).Message)
            : RecoveryReadResult<ThreadIdealProcessorPolicySnapshot>.Unavailable(error, new Win32Exception(error).Message);

    [StructLayout(LayoutKind.Sequential)]
    private struct IdealProcessorNumber { internal ushort Group; internal byte Number; internal byte Reserved; }
    [StructLayout(LayoutKind.Sequential)]
    private struct IdealGroupAffinity
    {
        internal UIntPtr Mask;
        internal ushort Group;
        internal ushort Reserved0;
        internal ushort Reserved1;
        internal ushort Reserved2;
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetThreadIdealProcessorEx(IntPtr thread, out IdealProcessorNumber value);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetThreadIdealProcessorEx(IntPtr thread, in IdealProcessorNumber value, out IdealProcessorNumber previous);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetThreadGroupAffinity(IntPtr thread, out IdealGroupAffinity value);
}
