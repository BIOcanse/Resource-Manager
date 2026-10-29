using System.ComponentModel;
using System.Runtime.InteropServices;
using ResourceManager.App.Application.Optimization;
using ResourceManager.App.Domain.Optimization;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.Windows;

namespace ResourceManager.App.Infrastructure.Optimization;

public sealed partial class WindowsProcessResourcePolicyWriter
{
    private readonly IThreadIdealProcessorNativeApi idealApi = new NativeThreadIdealProcessorApi();
    private readonly Func<int, RecoveryReadResult<ProcessInstanceRecoverySnapshot>> readIdealParent;
    private readonly Func<RecoveryReadResult<IReadOnlyDictionary<int, IReadOnlyList<int>>>> readIdealInventory = ReadIdealThreadInventory;

    internal WindowsProcessResourcePolicyWriter(NativeProcessPolicyBatchExecutor executor,
        IThreadIdealProcessorNativeApi api,
        Func<int, RecoveryReadResult<ProcessInstanceRecoverySnapshot>> readParent,
        Func<RecoveryReadResult<IReadOnlyDictionary<int, IReadOnlyList<int>>>> readInventory) : this(executor)
    {
        idealApi = api;
        readIdealParent = readParent;
        readIdealInventory = readInventory;
    }

    public IThreadIdealProcessorReadCycle CreateThreadIdealProcessorReadCycle()
        => new ThreadIdealProcessorReadCycle(readIdealInventory, readIdealParent, idealApi);

    public RecoveryReadResult<IReadOnlyList<ThreadIdealProcessorPolicySnapshot>> ReadProcessThreadIdealProcessors(
        int processId, ulong processStartKey, int maximumThreadCount)
        => CreateThreadIdealProcessorReadCycle().ReadProcessThreads(processId, processStartKey, maximumThreadCount);

    public RecoveryReadResult<ThreadIdealProcessorPolicySnapshot> ReadThreadIdealProcessor(
        int processId, ulong processStartKey, int threadId)
    {
        if (processId <= 4 || processStartKey == 0 || threadId <= 0)
            return RecoveryReadResult<ThreadIdealProcessorPolicySnapshot>.Unavailable(87, "Invalid thread identity.");
        var thread = idealApi.OpenThread(threadId, write: false);
        if (thread == IntPtr.Zero) return IdealReadFailure(idealApi.LastError, RecoveryReadAbsenceReason.TargetExited);
        try { return ReadIdealOnHandle(thread, processId, processStartKey, threadId); }
        finally { idealApi.CloseThread(thread); }
    }

    public ProcessResourcePolicyWriteResult TrySetThreadIdealProcessor(
        ThreadIdealProcessorPolicySnapshot expected, ThreadIdealProcessor desired)
    {
        if (desired.Group != expected.AllowedGroup || desired.Number >= 64
            || (expected.AllowedMask & (1UL << desired.Number)) == 0)
            return new(false, "Ideal processor is outside the observed thread group affinity.");
        var thread = idealApi.OpenThread(expected.ThreadId, write: true);
        if (thread == IntPtr.Zero) return new(false, new Win32Exception(idealApi.LastError).Message);
        try
        {
            var current = ReadIdealOnHandle(thread, expected.ProcessId, expected.ProcessStartKey, expected.ThreadId);
            if (current.Status != RecoveryReadStatus.Found || current.Value is not { } actual
                || actual.ThreadStartKey != expected.ThreadStartKey || actual.IdealProcessor != expected.IdealProcessor
                || actual.AllowedGroup != expected.AllowedGroup || actual.AllowedMask != expected.AllowedMask
                || !actual.SelectedCpuSetIds.SequenceEqual(expected.SelectedCpuSetIds))
                return new(false, "Thread identity, allowed range or ideal processor changed before the write.");
            if (!idealApi.TrySetIdeal(thread, desired))
                return new(false, new Win32Exception(idealApi.LastError).Message);
            return new(true, "Thread ideal processor hint applied; affinity and CPU Sets are unchanged.");
        }
        finally { idealApi.CloseThread(thread); }
    }

    private RecoveryReadResult<ThreadIdealProcessorPolicySnapshot> ReadIdealOnHandle(
        IntPtr thread, int processId, ulong processStartKey, int threadId)
    {
        if (idealApi.ReadProcessId(thread) != checked((uint)processId))
            return RecoveryReadResult<ThreadIdealProcessorPolicySnapshot>.IdentityChanged(0, "Thread belongs to another process.");
        var parent = VerifyIdealParent(readIdealParent(processId), processId, processStartKey);
        if (parent.Status != RecoveryReadStatus.Found || parent.Value is null)
            return new(parent.Status, null, parent.NativeErrorCode, parent.Message) { AbsenceReason = parent.AbsenceReason };
        return ReadIdealOnVerifiedHandle(idealApi, thread, processId, processStartKey, threadId, ownerVerified: true);
    }

    internal static RecoveryReadResult<ProcessInstanceRecoverySnapshot> VerifyIdealParent(
        RecoveryReadResult<ProcessInstanceRecoverySnapshot> parent, int processId, ulong processStartKey)
    {
        if (parent.Status != RecoveryReadStatus.Found) return parent;
        if (parent.Value is null)
            return RecoveryReadResult<ProcessInstanceRecoverySnapshot>.Unavailable(0, "Process creation identity could not be read.");
        if (parent.Value.ProcessId != processId || checked((ulong)parent.Value.StartedAt.ToFileTime()) != processStartKey)
            return RecoveryReadResult<ProcessInstanceRecoverySnapshot>.IdentityChanged(0, "Process creation identity changed.");
        return parent;
    }

    internal static RecoveryReadResult<ThreadIdealProcessorPolicySnapshot> ReadIdealOnVerifiedHandle(
        IThreadIdealProcessorNativeApi api, IntPtr thread, int processId, ulong processStartKey, int threadId,
        bool ownerVerified = false)
    {
        if (!ownerVerified && api.ReadProcessId(thread) != checked((uint)processId))
            return RecoveryReadResult<ThreadIdealProcessorPolicySnapshot>.IdentityChanged(0, "Thread belongs to another process.");
        if (!api.TryReadTimes(thread, out var creation, out var exit))
            return IdealReadFailure(api.LastError);
        if (creation < processStartKey)
            return RecoveryReadResult<ThreadIdealProcessorPolicySnapshot>.IdentityChanged(0, "Process creation identity changed.");
        if (exit != 0)
            return RecoveryReadResult<ThreadIdealProcessorPolicySnapshot>.Exited(0, "Thread exited.");
        if (!api.TryReadIdeal(thread, out var ideal) || !api.TryReadAffinity(thread, out var group, out var mask))
            return IdealReadFailure(api.LastError);
        IReadOnlyList<uint> selected = [];
        if (api.UsesCpuSets)
        {
            var sets = api.ReadSelectedCpuSets(thread);
            if (sets is null)
                return RecoveryReadResult<ThreadIdealProcessorPolicySnapshot>.Unavailable(0, "Thread CPU Sets could not be verified.");
            selected = sets;
        }
        return RecoveryReadResult<ThreadIdealProcessorPolicySnapshot>.Found(new(processId, processStartKey, threadId,
            creation, ideal, group, mask, selected));
    }

    internal static RecoveryReadResult<ThreadIdealProcessorPolicySnapshot> IdealReadFailure(int error,
        RecoveryReadAbsenceReason absenceReason = RecoveryReadAbsenceReason.Unspecified)
        => error is 87 or 1168
            ? RecoveryReadResult<ThreadIdealProcessorPolicySnapshot>.NotFoundOrExited(error, new Win32Exception(error).Message, absenceReason)
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

    private sealed class NativeThreadIdealProcessorApi : IThreadIdealProcessorNativeApi
    {
        public IntPtr OpenThread(int threadId, bool write) => NativeMethods.OpenThread(
            (write ? NativeMethods.ThreadSetInformation : 0U) | 0x0040 | NativeMethods.ThreadQueryLimitedInformation,
            false, checked((uint)threadId));
        public void CloseThread(IntPtr thread) => NativeMethods.CloseHandle(thread);
        public int LastError => Marshal.GetLastWin32Error();
        public uint ReadProcessId(IntPtr thread) => NativeMethods.GetProcessIdOfThread(thread);
        public bool TryReadTimes(IntPtr thread, out ulong creation, out ulong exit)
        {
            var read = NativeMethods.GetThreadTimes(thread, out var start, out var end, out _, out _);
            creation = start.ToUInt64(); exit = end.ToUInt64();
            return read;
        }
        public bool TryReadIdeal(IntPtr thread, out ThreadIdealProcessor ideal)
        {
            var read = GetThreadIdealProcessorEx(thread, out var value);
            ideal = new(value.Group, value.Number);
            return read;
        }
        public bool TryReadAffinity(IntPtr thread, out ushort group, out ulong mask)
        {
            var read = GetThreadGroupAffinity(thread, out var value);
            group = value.Group; mask = value.Mask.ToUInt64();
            return read;
        }
        public bool UsesCpuSets => OperatingSystem.IsWindowsVersionAtLeast(10);
        public IReadOnlyList<uint>? ReadSelectedCpuSets(IntPtr thread) => TryReadThreadSelectedCpuSetsOnHandle(thread);
        public bool TrySetIdeal(IntPtr thread, ThreadIdealProcessor desired)
        {
            var value = new IdealProcessorNumber { Group = desired.Group, Number = desired.Number };
            return SetThreadIdealProcessorEx(thread, in value, out _);
        }
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
