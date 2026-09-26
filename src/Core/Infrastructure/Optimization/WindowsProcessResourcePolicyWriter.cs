using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using ResourceManager.App.Application.Optimization;
using ResourceManager.App.Domain.Optimization;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.Windows;

namespace ResourceManager.App.Infrastructure.Optimization;

public sealed partial class WindowsProcessResourcePolicyWriter : IProcessResourcePolicyWriter
{
    private readonly NativeProcessPolicyBatchExecutor nativeBatchExecutor;

    internal WindowsProcessResourcePolicyWriter(NativeProcessPolicyBatchExecutor nativeBatchExecutor)
    {
        this.nativeBatchExecutor = nativeBatchExecutor
            ?? throw new ArgumentNullException(nameof(nativeBatchExecutor));
    }

    public DateTimeOffset? TryReadProcessStartedAt(int processId)
    {
        if (processId <= 0)
        {
            return null;
        }

        var read = ReadProcessInstanceForRecovery(processId);
        return read.Status == RecoveryReadStatus.Found
            ? read.Value!.StartedAt
            : null;
    }

    public ProcessResourcePolicySnapshot? TryReadProcess(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            if (process.HasExited)
            {
                return null;
            }

            var processName = SafeRead(() => process.ProcessName) ?? $"PID {processId}";
            var executablePath = SafeRead(() => process.MainModule?.FileName);
            var startedAt = SafeRead(() => new DateTimeOffset(process.StartTime));
            var priority = SafeRead(() => process.PriorityClass.ToString()) ?? string.Empty;
            var affinity = SafeRead(() => process.ProcessorAffinity.ToInt64());
            if (startedAt == default || string.IsNullOrWhiteSpace(priority) || affinity <= 0)
            {
                return null;
            }

            var nativePolicies = TryReadNativePolicySnapshot(process.Id);
            var memoryPriority = nativePolicies.MemoryPriority;
            var powerThrottling = nativePolicies.PowerThrottling;

            return new ProcessResourcePolicySnapshot(
                process.Id,
                processName,
                executablePath,
                startedAt,
                priority,
                affinity,
                Environment.ProcessorCount,
                memoryPriority?.MemoryPriority,
                memoryPriority?.RawValue,
                memoryPriority?.DisplayValue,
                powerThrottling?.ControlMask,
                powerThrottling?.StateMask,
                powerThrottling?.RawValue,
                powerThrottling?.DisplayValue);
        }
        catch (Exception ex) when (IsExpectedProcessException(ex))
        {
            return null;
        }
    }

    public ProcessResourcePolicyWriteResult TrySetPriorityClass(
        int processId,
        string priorityClass)
    {
        if (!Enum.TryParse<ProcessPriorityClass>(priorityClass, ignoreCase: true, out var parsed))
        {
            return new ProcessResourcePolicyWriteResult(false, $"不支持的进程优先级：{priorityClass}。");
        }

        try
        {
            using var process = Process.GetProcessById(processId);
            if (process.HasExited)
            {
                return new ProcessResourcePolicyWriteResult(false, "进程已经退出。");
            }

            process.PriorityClass = parsed;
            return new ProcessResourcePolicyWriteResult(true, $"已设置进程优先级为 {parsed}。");
        }
        catch (Exception ex) when (IsExpectedProcessException(ex))
        {
            return new ProcessResourcePolicyWriteResult(false, ex.Message);
        }
    }

    public ProcessResourcePolicyWriteResult TrySetProcessorAffinity(
        int processId,
        long affinityMask)
    {
        if (affinityMask == 0)
        {
            return new ProcessResourcePolicyWriteResult(false, "CPU affinity mask 无效。");
        }

        try
        {
            using var process = Process.GetProcessById(processId);
            if (process.HasExited)
            {
                return new ProcessResourcePolicyWriteResult(false, "进程已经退出。");
            }

            process.ProcessorAffinity = new IntPtr(affinityMask);
            return new ProcessResourcePolicyWriteResult(
                true,
                $"已设置 CPU affinity 为 {CpuAffinityPlanner.FormatMask(affinityMask)}。");
        }
        catch (Exception ex) when (IsExpectedProcessException(ex))
        {
            return new ProcessResourcePolicyWriteResult(false, ex.Message);
        }
    }

    public ProcessMemoryPrioritySnapshot? TryReadMemoryPriority(int processId)
    {
        var handle = NativeMethods.OpenProcess(NativeMethods.ProcessQueryLimitedInformation, false, processId);
        if (handle == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            return TryReadMemoryPriority(handle, processId);
        }
        finally
        {
            NativeMethods.CloseHandle(handle);
        }
    }

    [Obsolete("Use the exact-process expected-current overload.")]
    public ProcessResourcePolicyWriteResult TrySetMemoryPriority(
        int processId,
        uint memoryPriority) =>
        new(false, "MemoryPriority 写入缺少精确进程实例与 expected-current；已拒绝执行。");

    public ProcessResourcePolicyWriteResult TrySetMemoryPriority(
        int processId,
        DateTimeOffset expectedStartedAt,
        uint expectedCurrentMemoryPriority,
        uint memoryPriority)
    {
        var batch = TryApplyBatch(
        [
            new ProcessResourcePolicyBatchRequest(
                ProcessId: processId,
                ExpectedStartedAt: expectedStartedAt,
                MemoryPriority: memoryPriority,
                ExpectedMemoryPriority: expectedCurrentMemoryPriority)
        ]);
        var field = batch.Count == 1
            ? batch[0].Find(ProcessResourcePolicyBatchFields.MemoryPriority)
            : null;
        return field is null
            ? new ProcessResourcePolicyWriteResult(false, "MemoryPriority 批处理没有返回字段结果。")
            : new ProcessResourcePolicyWriteResult(field.Succeeded, field.Message);
    }

    public ProcessPowerThrottlingSnapshot? TryReadPowerThrottling(int processId)
    {
        var handle = NativeMethods.OpenProcess(NativeMethods.ProcessQueryLimitedInformation, false, processId);
        if (handle == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            return TryReadPowerThrottling(handle, processId);
        }
        finally
        {
            NativeMethods.CloseHandle(handle);
        }
    }

    public ProcessResourcePolicyWriteResult TrySetPowerThrottling(
        int processId,
        uint controlMask,
        uint stateMask)
    {
        var handle = NativeMethods.OpenProcess(
            NativeMethods.ProcessQueryLimitedInformation | NativeMethods.ProcessSetInformation,
            false,
            processId);
        if (handle == IntPtr.Zero)
        {
            return new ProcessResourcePolicyWriteResult(false, $"进程不可打开：{LastWin32ErrorMessage()}");
        }

        try
        {
            var state = ProcessPowerThrottlingState.Create(controlMask, stateMask);
            if (!NativeMethods.SetProcessInformation(
                handle,
                NativeMethods.ProcessPowerThrottlingInformationClass,
                ref state,
                PowerThrottlingInformationSize))
            {
                return new ProcessResourcePolicyWriteResult(false, $"设置进程节流失败：{LastWin32ErrorMessage()}");
            }

            return new ProcessResourcePolicyWriteResult(
                true,
                $"已设置进程节流状态为 {DescribePowerThrottling(controlMask, stateMask)}。");
        }
        finally
        {
            NativeMethods.CloseHandle(handle);
        }
    }

    public IReadOnlyList<uint>? TryReadProcessDefaultCpuSets(int processId)
    {
        var handle = NativeMethods.OpenProcess(NativeMethods.ProcessQueryLimitedInformation, false, processId);
        if (handle == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            _ = NativeMethods.GetProcessDefaultCpuSets(handle, IntPtr.Zero, 0, out var requiredCount);
            if (requiredCount == 0)
            {
                return [];
            }

            var byteLength = checked((int)requiredCount * sizeof(uint));
            var buffer = Marshal.AllocHGlobal(byteLength);
            try
            {
                if (!NativeMethods.GetProcessDefaultCpuSets(handle, buffer, requiredCount, out var returnedCount))
                {
                    return null;
                }

                var result = new uint[returnedCount];
                for (var index = 0; index < result.Length; index++)
                {
                    result[index] = unchecked((uint)Marshal.ReadInt32(buffer, index * sizeof(uint)));
                }

                return result.Order().ToArray();
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

    public ProcessResourcePolicyWriteResult TrySetProcessDefaultCpuSets(
        int processId,
        IReadOnlyList<uint> cpuSetIds)
    {
        ArgumentNullException.ThrowIfNull(cpuSetIds);
        var cleanIds = cpuSetIds
            .Where(static id => id != 0)
            .Distinct()
            .Order()
            .ToArray();
        var handle = NativeMethods.OpenProcess(
            NativeMethods.ProcessSetInformation | NativeMethods.ProcessQueryLimitedInformation,
            false,
            processId);
        if (handle == IntPtr.Zero)
        {
            return new ProcessResourcePolicyWriteResult(false, new Win32Exception(Marshal.GetLastWin32Error()).Message);
        }

        IntPtr buffer = IntPtr.Zero;
        try
        {
            if (cleanIds.Length > 0)
            {
                buffer = Marshal.AllocHGlobal(cleanIds.Length * sizeof(uint));
                for (var index = 0; index < cleanIds.Length; index++)
                {
                    Marshal.WriteInt32(buffer, index * sizeof(uint), unchecked((int)cleanIds[index]));
                }
            }

            if (!NativeMethods.SetProcessDefaultCpuSets(handle, buffer, (uint)cleanIds.Length))
            {
                return new ProcessResourcePolicyWriteResult(false, new Win32Exception(Marshal.GetLastWin32Error()).Message);
            }

            return new ProcessResourcePolicyWriteResult(
                true,
                cleanIds.Length == 0
                    ? "已清除进程默认 CPU Sets。"
                    : $"已设置进程默认 CPU Sets：{string.Join(',', cleanIds)}。");
        }
        finally
        {
            if (buffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(buffer);
            }

            NativeMethods.CloseHandle(handle);
        }
    }

    public ThreadCpuSetPolicySnapshot? TryReadThreadSelectedCpuSets(
        int processId,
        int threadId)
    {
        if (processId <= 0 || threadId <= 0)
        {
            return null;
        }

        var handle = NativeMethods.OpenThread(
            NativeMethods.ThreadQueryLimitedInformation,
            false,
            (uint)threadId);
        if (handle == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            if (NativeMethods.GetProcessIdOfThread(handle) != (uint)processId
                || !TryReadThreadCreatedAt(handle, out var createdAt))
            {
                return null;
            }

            _ = NativeMethods.GetThreadSelectedCpuSets(handle, IntPtr.Zero, 0, out var requiredCount);
            if (requiredCount == 0)
            {
                return new ThreadCpuSetPolicySnapshot(processId, threadId, createdAt, []);
            }

            var buffer = Marshal.AllocHGlobal(checked((int)requiredCount * sizeof(uint)));
            try
            {
                if (!NativeMethods.GetThreadSelectedCpuSets(handle, buffer, requiredCount, out var returnedCount))
                {
                    return null;
                }

                var ids = new uint[returnedCount];
                for (var index = 0; index < ids.Length; index++)
                {
                    ids[index] = unchecked((uint)Marshal.ReadInt32(buffer, index * sizeof(uint)));
                }

                return new ThreadCpuSetPolicySnapshot(
                    processId,
                    threadId,
                    createdAt,
                    ids.Distinct().Order().ToArray());
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

    public ProcessResourcePolicyWriteResult TrySetThreadSelectedCpuSets(
        int processId,
        int threadId,
        DateTimeOffset expectedCreatedAt,
        IReadOnlyList<uint> cpuSetIds)
    {
        ArgumentNullException.ThrowIfNull(cpuSetIds);
        var cleanIds = cpuSetIds.Where(static id => id != 0).Distinct().Order().ToArray();
        var handle = NativeMethods.OpenThread(
            NativeMethods.ThreadSetInformation | NativeMethods.ThreadQueryLimitedInformation,
            false,
            (uint)threadId);
        if (handle == IntPtr.Zero)
        {
            return new ProcessResourcePolicyWriteResult(false, new Win32Exception(Marshal.GetLastWin32Error()).Message);
        }

        IntPtr buffer = IntPtr.Zero;
        try
        {
            if (NativeMethods.GetProcessIdOfThread(handle) != (uint)processId
                || !TryReadThreadCreatedAt(handle, out var createdAt)
                || Math.Abs((createdAt - expectedCreatedAt).TotalSeconds) > 1)
            {
                return new ProcessResourcePolicyWriteResult(false, "线程身份已经变化，拒绝写入 CPU Sets。");
            }

            if (cleanIds.Length > 0)
            {
                buffer = Marshal.AllocHGlobal(cleanIds.Length * sizeof(uint));
                for (var index = 0; index < cleanIds.Length; index++)
                {
                    Marshal.WriteInt32(buffer, index * sizeof(uint), unchecked((int)cleanIds[index]));
                }
            }

            if (!NativeMethods.SetThreadSelectedCpuSets(handle, buffer, (uint)cleanIds.Length))
            {
                return new ProcessResourcePolicyWriteResult(false, new Win32Exception(Marshal.GetLastWin32Error()).Message);
            }

            return new ProcessResourcePolicyWriteResult(
                true,
                cleanIds.Length == 0
                    ? $"已清除线程 {threadId} 的 Selected CPU Sets。"
                    : $"已设置线程 {threadId} 的 Selected CPU Sets：{string.Join(',', cleanIds)}。");
        }
        finally
        {
            if (buffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(buffer);
            }

            NativeMethods.CloseHandle(handle);
        }
    }

    private static bool TryReadThreadCreatedAt(IntPtr thread, out DateTimeOffset createdAt)
    {
        createdAt = default;
        if (!NativeMethods.GetThreadTimes(thread, out var creation, out _, out _, out _))
        {
            return false;
        }

        try
        {
            createdAt = DateTimeOffset.FromFileTime(checked((long)creation.ToUInt64()));
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    public static string FormatPowerThrottlingRaw(uint controlMask, uint stateMask)
    {
        return $"control={controlMask};state={stateMask}";
    }

    public static bool TryParsePowerThrottlingRaw(
        string? rawValue,
        out uint controlMask,
        out uint stateMask)
    {
        controlMask = 0;
        stateMask = 0;
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return false;
        }

        foreach (var part in rawValue.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = part.IndexOf('=');
            if (separator <= 0 || separator == part.Length - 1)
            {
                continue;
            }

            var name = part[..separator];
            var value = part[(separator + 1)..];
            if (name.Equals("control", StringComparison.OrdinalIgnoreCase)
                && uint.TryParse(value, out var parsedControl))
            {
                controlMask = parsedControl;
            }
            else if (name.Equals("state", StringComparison.OrdinalIgnoreCase)
                && uint.TryParse(value, out var parsedState))
            {
                stateMask = parsedState;
            }
        }

        return rawValue.Contains("control=", StringComparison.OrdinalIgnoreCase)
            && rawValue.Contains("state=", StringComparison.OrdinalIgnoreCase);
    }

    public static string DescribePowerThrottling(uint controlMask, uint stateMask)
    {
        var bit = NativeMethods.ProcessPowerThrottlingExecutionSpeed;
        if ((controlMask & bit) == 0)
        {
            return "未显式控制执行速度节流";
        }

        return (stateMask & bit) == 0
            ? "执行速度节流关闭"
            : "执行速度节流开启";
    }

    public static string DescribeMemoryPriority(uint memoryPriority)
    {
        return memoryPriority switch
        {
            NativeMethods.MemoryPriorityVeryLow => "VeryLow / 极低内存优先级",
            NativeMethods.MemoryPriorityLow => "Low / 低内存优先级",
            NativeMethods.MemoryPriorityMedium => "Medium / 中等内存优先级",
            NativeMethods.MemoryPriorityBelowNormal => "BelowNormal / 略低内存优先级",
            NativeMethods.MemoryPriorityNormal => "Normal / 默认内存优先级",
            _ => $"未知内存优先级 {memoryPriority}"
        };
    }

    private static T? SafeRead<T>(Func<T> read)
    {
        try
        {
            return read();
        }
        catch (Exception ex) when (IsExpectedProcessException(ex))
        {
            return default;
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
}
