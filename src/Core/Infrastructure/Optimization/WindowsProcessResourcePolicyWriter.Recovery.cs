using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using ResourceManager.App.Application.Optimization;
using ResourceManager.App.Domain.Optimization;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.ProcessIdentity;
using ResourceManager.App.Infrastructure.Windows;

namespace ResourceManager.App.Infrastructure.Optimization;

public sealed partial class WindowsProcessResourcePolicyWriter
{
    private const uint StillActive = 259;

    public RecoveryReadResult<ProcessInstanceRecoverySnapshot> ReadProcessInstanceForRecovery(
        int processId)
    {
        if (processId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(processId));
        }

        var handle = NativeMethods.OpenProcess(
            NativeMethods.ProcessQueryLimitedInformation,
            false,
            processId);
        if (handle == IntPtr.Zero)
        {
            return ClassifyProcessInstanceOpenFailure(
                Marshal.GetLastWin32Error());
        }

        try
        {
            return ReadProcessInstanceForRecovery(handle, processId);
        }
        finally
        {
            NativeMethods.CloseHandle(handle);
        }
    }

    private static RecoveryReadResult<ProcessInstanceRecoverySnapshot> ReadProcessInstanceForRecovery(
        IntPtr handle,
        int processId)
    {
        if (!GetExitCodeProcess(handle, out var exitCode))
        {
            var nativeErrorCode = Marshal.GetLastWin32Error();
            return RecoveryReadResult<ProcessInstanceRecoverySnapshot>.Unavailable(
                nativeErrorCode,
                Win32ErrorText.Get(nativeErrorCode));
        }
        if (exitCode != StillActive)
        {
            return RecoveryReadResult<ProcessInstanceRecoverySnapshot>.Exited(
                0,
                "进程已经退出。");
        }
        if (!GetProcessTimes(handle, out var creation, out var exitedAt, out _, out _))
        {
            var nativeErrorCode = Marshal.GetLastWin32Error();
            return RecoveryReadResult<ProcessInstanceRecoverySnapshot>.Unavailable(
                nativeErrorCode,
                Win32ErrorText.Get(nativeErrorCode));
        }

        // STILL_ACTIVE is also a possible exit code; use the same handle's exit time.
        if (exitedAt.ToUInt64() != 0)
        {
            return RecoveryReadResult<ProcessInstanceRecoverySnapshot>.Exited(
                0,
                "进程已经退出。");
        }

        try
        {
            return RecoveryReadResult<ProcessInstanceRecoverySnapshot>.Found(
                new ProcessInstanceRecoverySnapshot(
                    processId,
                    DateTimeOffset.FromFileTime(checked((long)creation.ToUInt64()))));
        }
        catch (ArgumentOutOfRangeException exception)
        {
            return RecoveryReadResult<ProcessInstanceRecoverySnapshot>.Unavailable(
                exception.HResult,
                exception.Message);
        }
    }

    public RecoveryReadResult<ProcessPlacementRecoverySnapshot> ReadPlacementStateForRecovery(
        int processId,
        ProcessPlacementReadFields requiredFields)
    {
        if (processId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(processId));
        }
        if (requiredFields == ProcessPlacementReadFields.None
            || (requiredFields & ~(ProcessPlacementReadFields.Affinity
                | ProcessPlacementReadFields.DefaultCpuSets
                | ProcessPlacementReadFields.PriorityClass
                | ProcessPlacementReadFields.MemoryPriority)) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(requiredFields));
        }

        try
        {
            Process process;
            try { process = Process.GetProcessById(processId); }
            catch (ArgumentException exception)
            {
                return RecoveryReadResult<ProcessPlacementRecoverySnapshot>.Exited(exception.HResult, exception.Message);
            }
            using var processLease = process;
            if (process.HasExited)
            {
                return RecoveryReadResult<ProcessPlacementRecoverySnapshot>.Exited(
                    0,
                    "进程已经退出。");
            }

            var startedAt = new DateTimeOffset(process.StartTime);
            var processName = process.ProcessName;
            var executablePath = WindowsProcessImagePathReader.TryRead(process);

            long? affinity = null;
            if (requiredFields.HasFlag(ProcessPlacementReadFields.Affinity))
            {
                affinity = process.ProcessorAffinity.ToInt64();
                if (affinity == 0)
                {
                    return RecoveryReadResult<ProcessPlacementRecoverySnapshot>.Unavailable(
                        0,
                        "进程 affinity 返回无效值。");
                }
            }

            IReadOnlyList<uint>? defaultCpuSetIds = null;
            if (requiredFields.HasFlag(ProcessPlacementReadFields.DefaultCpuSets))
            {
                var cpuSets = ReadProcessDefaultCpuSetsForRecovery(processId);
                if (cpuSets.Status != RecoveryReadStatus.Found)
                {
                    return new RecoveryReadResult<ProcessPlacementRecoverySnapshot>(
                        cpuSets.Status,
                        null,
                        cpuSets.NativeErrorCode,
                        cpuSets.Message) { AbsenceReason = cpuSets.AbsenceReason };
                }
                defaultCpuSetIds = cpuSets.Value!;
            }

            string? priority = null;
            if (requiredFields.HasFlag(ProcessPlacementReadFields.PriorityClass))
            {
                priority = process.PriorityClass.ToString();
                if (string.IsNullOrWhiteSpace(priority))
                {
                    return RecoveryReadResult<ProcessPlacementRecoverySnapshot>.Unavailable(
                        0,
                        "进程优先级返回空值。");
                }
            }

            uint? memoryPriority = null;
            if (requiredFields.HasFlag(ProcessPlacementReadFields.MemoryPriority))
            {
                memoryPriority = TryReadMemoryPriority(processId)?.MemoryPriority;
                if (memoryPriority is null)
                {
                    return process.HasExited
                        ? RecoveryReadResult<ProcessPlacementRecoverySnapshot>.Exited(0, "进程已经退出。")
                        : RecoveryReadResult<ProcessPlacementRecoverySnapshot>.Unavailable(0, "进程内存优先级当前不可读。");
                }
            }

            return RecoveryReadResult<ProcessPlacementRecoverySnapshot>.Found(
                new ProcessPlacementRecoverySnapshot(
                    processId,
                    startedAt,
                    processName,
                    executablePath,
                    affinity,
                    defaultCpuSetIds,
                    priority,
                    memoryPriority));
        }
        catch (Exception ex) when (IsExpectedProcessException(ex))
        {
            return ClassifyProcessRecoveryFailure<ProcessPlacementRecoverySnapshot>(ex);
        }
    }

    public RecoveryReadResult<ThreadCpuSetPolicySnapshot> ReadThreadPlacementStateForRecovery(
        int processId,
        int threadId)
    {
        if (processId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(processId));
        }
        if (threadId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(threadId));
        }

        var handle = NativeMethods.OpenThread(
            NativeMethods.ThreadQueryLimitedInformation,
            false,
            checked((uint)threadId));
        if (handle == IntPtr.Zero)
        {
            return ClassifyWin32RecoveryFailure<ThreadCpuSetPolicySnapshot>(Marshal.GetLastWin32Error(),
                RecoveryReadAbsenceReason.TargetExited);
        }

        try
        {
            if (NativeMethods.GetProcessIdOfThread(handle) != checked((uint)processId))
            {
                return RecoveryReadResult<ThreadCpuSetPolicySnapshot>.IdentityChanged(
                    0,
                    "线程不再属于记录中的进程。");
            }
            if (!TryReadThreadCreatedAt(handle, out var createdAt))
            {
                return ClassifyWin32RecoveryFailure<ThreadCpuSetPolicySnapshot>(Marshal.GetLastWin32Error());
            }

            var initialRead = NativeMethods.GetThreadSelectedCpuSets(handle, IntPtr.Zero, 0, out var requiredCount);
            var initialError = initialRead ? 0 : Marshal.GetLastWin32Error();
            if (requiredCount == 0)
            {
                return initialRead
                    ? RecoveryReadResult<ThreadCpuSetPolicySnapshot>.Found(
                        new ThreadCpuSetPolicySnapshot(processId, threadId, createdAt, []))
                    : ClassifyWin32RecoveryFailure<ThreadCpuSetPolicySnapshot>(initialError);
            }

            var buffer = Marshal.AllocHGlobal(checked((int)requiredCount * sizeof(uint)));
            try
            {
                if (!NativeMethods.GetThreadSelectedCpuSets(handle, buffer, requiredCount, out var returnedCount))
                {
                    return ClassifyWin32RecoveryFailure<ThreadCpuSetPolicySnapshot>(Marshal.GetLastWin32Error());
                }

                var ids = new uint[returnedCount];
                for (var index = 0; index < ids.Length; index++)
                {
                    ids[index] = unchecked((uint)Marshal.ReadInt32(buffer, index * sizeof(uint)));
                }
                return RecoveryReadResult<ThreadCpuSetPolicySnapshot>.Found(
                    new ThreadCpuSetPolicySnapshot(
                        processId,
                        threadId,
                        createdAt,
                        ids.Distinct().Order().ToArray()));
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        finally
        {
            NativeMethods.CloseHandle(handle);
        }
    }

    private static RecoveryReadResult<IReadOnlyList<uint>> ReadProcessDefaultCpuSetsForRecovery(int processId)
    {
        var handle = NativeMethods.OpenProcess(NativeMethods.ProcessQueryLimitedInformation, false, processId);
        if (handle == IntPtr.Zero)
        {
            return ClassifyWin32RecoveryFailure<IReadOnlyList<uint>>(Marshal.GetLastWin32Error(),
                RecoveryReadAbsenceReason.TargetExited);
        }

        try
        {
            var initialRead = NativeMethods.GetProcessDefaultCpuSets(handle, IntPtr.Zero, 0, out var requiredCount);
            var initialError = initialRead ? 0 : Marshal.GetLastWin32Error();
            if (requiredCount == 0)
            {
                return initialRead
                    ? RecoveryReadResult<IReadOnlyList<uint>>.Found([])
                    : ClassifyWin32RecoveryFailure<IReadOnlyList<uint>>(initialError);
            }

            var buffer = Marshal.AllocHGlobal(checked((int)requiredCount * sizeof(uint)));
            try
            {
                if (!NativeMethods.GetProcessDefaultCpuSets(handle, buffer, requiredCount, out var returnedCount))
                {
                    return ClassifyWin32RecoveryFailure<IReadOnlyList<uint>>(Marshal.GetLastWin32Error());
                }

                var result = new uint[returnedCount];
                for (var index = 0; index < result.Length; index++)
                {
                    result[index] = unchecked((uint)Marshal.ReadInt32(buffer, index * sizeof(uint)));
                }
                return RecoveryReadResult<IReadOnlyList<uint>>.Found(result.Distinct().Order().ToArray());
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        finally
        {
            NativeMethods.CloseHandle(handle);
        }
    }

    private static RecoveryReadResult<T> ClassifyProcessRecoveryFailure<T>(Exception exception)
    {
        if (exception is ArgumentException)
        {
            return RecoveryReadResult<T>.NotFoundOrExited(exception.HResult, exception.Message);
        }
        if (exception is Win32Exception win32)
        {
            return ClassifyWin32RecoveryFailure<T>(win32.NativeErrorCode);
        }
        return RecoveryReadResult<T>.Unavailable(exception.HResult, exception.Message);
    }

    private static RecoveryReadResult<T> ClassifyWin32RecoveryFailure<T>(int nativeErrorCode,
        RecoveryReadAbsenceReason absenceReason = RecoveryReadAbsenceReason.Unspecified)
    {
        const int errorInvalidParameter = 87;
        const int errorNotFound = 1168;
        var message = Win32ErrorText.Get(nativeErrorCode);
        return nativeErrorCode is errorInvalidParameter or errorNotFound
            ? RecoveryReadResult<T>.NotFoundOrExited(nativeErrorCode, message, absenceReason)
            : RecoveryReadResult<T>.Unavailable(nativeErrorCode, message);
    }

    private static RecoveryReadResult<ProcessInstanceRecoverySnapshot> ClassifyProcessInstanceOpenFailure(
        int nativeErrorCode)
        => ClassifyWin32RecoveryFailure<ProcessInstanceRecoverySnapshot>(nativeErrorCode,
            RecoveryReadAbsenceReason.TargetExited);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetExitCodeProcess(IntPtr process, out uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(
        IntPtr process,
        out NativeFileTime creationTime,
        out NativeFileTime exitTime,
        out NativeFileTime kernelTime,
        out NativeFileTime userTime);

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct NativeFileTime
    {
        private readonly uint low;
        private readonly uint high;

        internal ulong ToUInt64() => ((ulong)high << 32) | low;
    }
}
