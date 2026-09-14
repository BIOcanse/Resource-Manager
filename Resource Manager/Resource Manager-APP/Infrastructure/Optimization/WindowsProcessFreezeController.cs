using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using ResourceManager.App.Application.Optimization;
using ResourceManager.App.Domain.Optimization;
using ResourceManager.App.Infrastructure.Windows;

namespace ResourceManager.App.Infrastructure.Optimization;

public sealed class WindowsProcessFreezeController(IProcessResourcePolicyWriter policyWriter) : IProcessFreezeController
{
    private const uint SuspendThreadFailed = uint.MaxValue;

    public ProcessFreezeTarget? TryReadTarget(int processId)
    {
        var process = policyWriter.TryReadProcess(processId);
        if (process is null)
        {
            return null;
        }

        var threadIds = TryReadThreadIds(processId);
        return new ProcessFreezeTarget(process, threadIds);
    }

    public ProcessFreezeWriteResult TryFreezeProcess(int processId)
    {
        var target = TryReadTarget(processId);
        if (target is null)
        {
            return new ProcessFreezeWriteResult(false, "进程不可读取或已经退出。", [], false, false, null);
        }

        var suspended = new List<ProcessThreadSuspendRecord>();
        foreach (var threadId in target.ThreadIds)
        {
            var result = TrySuspendThread(threadId);
            if (result is not null)
            {
                suspended.Add(result);
            }
        }

        var trim = TryTrimWorkingSet(processId);
        if (suspended.Count == 0)
        {
            return new ProcessFreezeWriteResult(
                false,
                "没有成功挂起的线程。",
                [],
                trim.Attempted,
                trim.Succeeded,
                trim.Message);
        }

        return new ProcessFreezeWriteResult(
            true,
            $"已挂起 {suspended.Count} 个线程。{trim.Message}",
            suspended,
            trim.Attempted,
            trim.Succeeded,
            trim.Message);
    }

    public ProcessFreezeRestoreResult TryResumeThreads(IReadOnlyList<ProcessThreadSuspendRecord> threads)
    {
        if (threads.Count == 0)
        {
            return new ProcessFreezeRestoreResult(false, "没有可恢复的线程记录。", []);
        }

        var results = threads
            .Select(static thread => TryResumeThread(thread.ThreadId))
            .ToArray();
        var succeededCount = results.Count(static result => result.Succeeded);
        return new ProcessFreezeRestoreResult(
            succeededCount > 0,
            succeededCount > 0
                ? $"已恢复 {succeededCount} 个线程。"
                : "没有成功恢复的线程。",
            results);
    }

    private static IReadOnlyList<int> TryReadThreadIds(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.Threads
                .Cast<ProcessThread>()
                .Select(static thread => thread.Id)
                .Where(static id => id > 0)
                .Distinct()
                .Order()
                .ToArray();
        }
        catch (Exception ex) when (IsExpectedProcessException(ex))
        {
            return [];
        }
    }

    private static ProcessThreadSuspendRecord? TrySuspendThread(int threadId)
    {
        var handle = NativeMethods.OpenThread(
            NativeMethods.ThreadSuspendResume | NativeMethods.ThreadQueryLimitedInformation,
            false,
            (uint)threadId);
        if (handle == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var previous = NativeMethods.SuspendThread(handle);
            if (previous == SuspendThreadFailed)
            {
                return null;
            }

            return new ProcessThreadSuspendRecord(
                threadId,
                previous,
                DateTimeOffset.Now,
                $"挂起前 suspend count：{previous}。");
        }
        finally
        {
            NativeMethods.CloseHandle(handle);
        }
    }

    private static ProcessThreadResumeRecord TryResumeThread(int threadId)
    {
        var handle = NativeMethods.OpenThread(NativeMethods.ThreadSuspendResume, false, (uint)threadId);
        if (handle == IntPtr.Zero)
        {
            return new ProcessThreadResumeRecord(threadId, false, null, $"线程不可打开：{LastWin32ErrorMessage()}");
        }

        try
        {
            var previous = NativeMethods.ResumeThread(handle);
            if (previous == SuspendThreadFailed)
            {
                return new ProcessThreadResumeRecord(threadId, false, null, $"恢复失败：{LastWin32ErrorMessage()}");
            }

            return new ProcessThreadResumeRecord(threadId, previous > 0, previous, $"恢复前 suspend count：{previous}。");
        }
        finally
        {
            NativeMethods.CloseHandle(handle);
        }
    }

    private static WorkingSetTrimResult TryTrimWorkingSet(int processId)
    {
        var handle = NativeMethods.OpenProcess(
            NativeMethods.ProcessQueryLimitedInformation | NativeMethods.ProcessSetQuota,
            false,
            processId);
        if (handle == IntPtr.Zero)
        {
            return new WorkingSetTrimResult(true, false, $"工作集裁剪失败：{LastWin32ErrorMessage()}");
        }

        try
        {
            var succeeded = NativeMethods.EmptyWorkingSet(handle);
            return new WorkingSetTrimResult(
                true,
                succeeded,
                succeeded ? "工作集裁剪已请求。" : $"工作集裁剪失败：{LastWin32ErrorMessage()}");
        }
        finally
        {
            NativeMethods.CloseHandle(handle);
        }
    }

    private static string LastWin32ErrorMessage()
    {
        return new Win32Exception(Marshal.GetLastWin32Error()).Message;
    }

    private static bool IsExpectedProcessException(Exception ex)
    {
        return ex is InvalidOperationException
            or ArgumentException
            or Win32Exception
            or NotSupportedException;
    }

    private sealed record WorkingSetTrimResult(
        bool Attempted,
        bool Succeeded,
        string Message);
}
