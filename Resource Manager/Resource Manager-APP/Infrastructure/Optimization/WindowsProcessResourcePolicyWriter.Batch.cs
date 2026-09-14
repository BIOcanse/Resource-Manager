using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using ResourceManager.App.Application.Optimization;
using ResourceManager.App.Domain.Optimization;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.Windows;

namespace ResourceManager.App.Infrastructure.Optimization;

public sealed partial class WindowsProcessResourcePolicyWriter
{
    public IReadOnlyList<ProcessResourcePolicyBatchWriteResult> TryApplyBatch(
        IReadOnlyList<ProcessResourcePolicyBatchRequest> requests)
    {
        ArgumentNullException.ThrowIfNull(requests);
        if (requests.Count == 0)
        {
            return [];
        }

        var fieldsByRequest = new List<ProcessResourcePolicyBatchFieldResult>[requests.Count];
        var nativeItems = new List<NativeProcessPolicyBatchItem>(requests.Count);
        var requestIndexes = new List<int>(requests.Count);
        var cpuSetWrites = new List<PreparedCpuSetWrite>();
        for (var index = 0; index < requests.Count; index++)
        {
            fieldsByRequest[index] = [];
            if (TryPrepareCpuSetWrite(
                    requests[index],
                    index,
                    fieldsByRequest[index],
                    out var cpuSetWrite))
            {
                cpuSetWrites.Add(cpuSetWrite);
            }
            if (TryPrepareNativeItem(requests[index], fieldsByRequest[index], out var item))
            {
                nativeItems.Add(item);
                requestIndexes.Add(index);
            }
        }

        if (nativeItems.Count > 0)
        {
            ApplyPreparedNativeItems(nativeItems, requestIndexes, fieldsByRequest);
        }
        ApplyPreparedCpuSetWrites(cpuSetWrites, fieldsByRequest);

        var results = new ProcessResourcePolicyBatchWriteResult[requests.Count];
        for (var index = 0; index < requests.Count; index++)
        {
            results[index] = new ProcessResourcePolicyBatchWriteResult(
                requests[index].ProcessId,
                fieldsByRequest[index]);
        }

        return results;
    }

    private static bool TryPrepareNativeItem(
        ProcessResourcePolicyBatchRequest request,
        List<ProcessResourcePolicyBatchFieldResult> validationResults,
        out NativeProcessPolicyBatchItem item)
    {
        var nativeFields = NativeProcessPolicyFields.None;
        uint priorityClass = 0;
        ulong affinityMask = 0;
        var memoryPriority = request.MemoryPriority ?? 0;
        var expectedMemoryPriority = request.ExpectedMemoryPriority ?? 0;

        if (!string.IsNullOrWhiteSpace(request.PriorityClass))
        {
            if (Enum.TryParse<ProcessPriorityClass>(request.PriorityClass, true, out var parsedPriority))
            {
                priorityClass = (uint)parsedPriority;
                nativeFields |= NativeProcessPolicyFields.PriorityClass;
            }
            else
            {
                validationResults.Add(Failure(
                    ProcessResourcePolicyBatchFields.PriorityClass,
                    $"不支持的进程优先级：{request.PriorityClass}。"));
            }
        }

        if (request.AffinityMask is not null && request.CpuSetIds is null)
        {
            if (request.AffinityMask.Value != 0)
            {
                affinityMask = unchecked((ulong)request.AffinityMask.Value);
                nativeFields |= NativeProcessPolicyFields.AffinityMask;
            }
            else
            {
                validationResults.Add(Failure(
                    ProcessResourcePolicyBatchFields.AffinityMask,
                    "CPU affinity mask 无效。"));
            }
        }

        if (request.MemoryPriority is not null)
        {
            if (memoryPriority is >= NativeMethods.MemoryPriorityVeryLow
                    and <= NativeMethods.MemoryPriorityNormal
                && expectedMemoryPriority is >= NativeMethods.MemoryPriorityVeryLow
                    and <= NativeMethods.MemoryPriorityNormal
                && request.ExpectedStartedAt is not null)
            {
                nativeFields |= NativeProcessPolicyFields.MemoryPriority;
            }
            else
            {
                var message = memoryPriority is < NativeMethods.MemoryPriorityVeryLow
                        or > NativeMethods.MemoryPriorityNormal
                    ? $"不支持的内存优先级：{memoryPriority}。"
                    : expectedMemoryPriority is < NativeMethods.MemoryPriorityVeryLow
                            or > NativeMethods.MemoryPriorityNormal
                        ? "MemoryPriority 写入缺少有效的 expected-current。"
                        : "MemoryPriority 写入缺少精确进程开始时间。";
                validationResults.Add(Failure(
                    ProcessResourcePolicyBatchFields.MemoryPriority,
                    message));
            }
        }
        else if (request.ExpectedMemoryPriority is not null)
        {
            validationResults.Add(Failure(
                ProcessResourcePolicyBatchFields.MemoryPriority,
                "ExpectedMemoryPriority 只能与 MemoryPriority 写入同时提供。"));
        }

        var hasPowerControl = request.PowerControlMask is not null;
        var hasPowerState = request.PowerStateMask is not null;
        if (hasPowerControl || hasPowerState)
        {
            if (hasPowerControl && hasPowerState)
            {
                nativeFields |= NativeProcessPolicyFields.PowerThrottling;
            }
            else
            {
                validationResults.Add(Failure(
                    ProcessResourcePolicyBatchFields.PowerThrottling,
                    "进程节流 control/state 必须成对提供。"));
            }
        }

        if (request.TrimWorkingSet)
        {
            nativeFields |= NativeProcessPolicyFields.TrimWorkingSet;
        }

        if (request.ProcessId <= 0)
        {
            AddFailureForNativeFields(validationResults, nativeFields, "进程 ID 无效。");
            item = default;
            return false;
        }

        if (nativeFields == NativeProcessPolicyFields.None)
        {
            item = default;
            return false;
        }

        var expectedStartFileTime = request.ExpectedStartedAt is null
            ? 0UL
            : checked((ulong)request.ExpectedStartedAt.Value.ToFileTime());
        item = NativeProcessPolicyBatchExecutor.CreateItem(
            checked((uint)request.ProcessId),
            nativeFields,
            priorityClass,
            affinityMask,
            memoryPriority,
            expectedMemoryPriority,
            request.PowerControlMask ?? 0,
            request.PowerStateMask ?? 0,
            request.TrimWorkingSet,
            expectedStartFileTime);
        return true;
    }

    private static bool TryPrepareCpuSetWrite(
        ProcessResourcePolicyBatchRequest request,
        int requestIndex,
        List<ProcessResourcePolicyBatchFieldResult> validationResults,
        out PreparedCpuSetWrite prepared)
    {
        prepared = default;
        if (request.CpuSetIds is null)
        {
            return false;
        }

        if (request.AffinityMask is not null)
        {
            const string message = "CPU affinity mask 与 CPU Sets 不能在同一请求中同时写入。";
            validationResults.Add(Failure(ProcessResourcePolicyBatchFields.AffinityMask, message));
            validationResults.Add(Failure(ProcessResourcePolicyBatchFields.CpuSets, message));
            return false;
        }

        if (request.ProcessId <= 0)
        {
            validationResults.Add(Failure(
                ProcessResourcePolicyBatchFields.CpuSets,
                "进程 ID 无效。"));
            return false;
        }

        if (request.CpuSetIds.Count == 0
            || request.CpuSetIds.Any(static id => id == 0)
            || request.CpuSetIds.Distinct().Count() != request.CpuSetIds.Count)
        {
            validationResults.Add(Failure(
                ProcessResourcePolicyBatchFields.CpuSets,
                "CPU Sets 必须是非空、非零且不重复的 CPU Set ID 集合。"));
            return false;
        }

        prepared = new PreparedCpuSetWrite(
            requestIndex,
            request.ProcessId,
            request.ExpectedStartedAt,
            request.CpuSetIds.Order().ToArray());
        return true;
    }

    private void ApplyPreparedCpuSetWrites(
        IReadOnlyList<PreparedCpuSetWrite> writes,
        List<ProcessResourcePolicyBatchFieldResult>[] fieldsByRequest)
    {
        foreach (var write in writes)
        {
            var result = TrySetProcessDefaultCpuSetsExact(write);
            fieldsByRequest[write.RequestIndex].Add(new ProcessResourcePolicyBatchFieldResult(
                ProcessResourcePolicyBatchFields.CpuSets,
                result.Succeeded,
                result.Message));
        }
    }

    private static ProcessResourcePolicyWriteResult TrySetProcessDefaultCpuSetsExact(
        PreparedCpuSetWrite write)
    {
        var handle = NativeMethods.OpenProcess(
            NativeMethods.ProcessSetInformation | NativeMethods.ProcessQueryLimitedInformation,
            false,
            write.ProcessId);
        if (handle == IntPtr.Zero)
        {
            return new ProcessResourcePolicyWriteResult(
                false,
                new Win32Exception(Marshal.GetLastWin32Error()).Message);
        }

        IntPtr buffer = IntPtr.Zero;
        try
        {
            if (write.ExpectedStartedAt is { } expectedStartedAt)
            {
                if (!GetProcessTimes(handle, out var creation, out _, out _, out _))
                {
                    return new ProcessResourcePolicyWriteResult(
                        false,
                        new Win32Exception(Marshal.GetLastWin32Error()).Message);
                }

                if (creation.ToUInt64() != checked((ulong)expectedStartedAt.ToFileTime()))
                {
                    return new ProcessResourcePolicyWriteResult(
                        false,
                        "进程实例已经变化，拒绝写入 CPU Sets。");
                }
            }

            buffer = Marshal.AllocHGlobal(checked(write.CpuSetIds.Length * sizeof(uint)));
            for (var index = 0; index < write.CpuSetIds.Length; index++)
            {
                Marshal.WriteInt32(
                    buffer,
                    index * sizeof(uint),
                    unchecked((int)write.CpuSetIds[index]));
            }

            if (!NativeMethods.SetProcessDefaultCpuSets(
                    handle,
                    buffer,
                    checked((uint)write.CpuSetIds.Length)))
            {
                return new ProcessResourcePolicyWriteResult(
                    false,
                    new Win32Exception(Marshal.GetLastWin32Error()).Message);
            }

            return new ProcessResourcePolicyWriteResult(
                true,
                $"已设置进程默认 CPU Sets：{string.Join(',', write.CpuSetIds)}。");
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

    private void ApplyPreparedNativeItems(
        List<NativeProcessPolicyBatchItem> nativeItems,
        List<int> requestIndexes,
        List<ProcessResourcePolicyBatchFieldResult>[] fieldsByRequest)
    {
        var items = nativeItems.ToArray();
        var nativeResults = new NativeProcessPolicyBatchItemResult[items.Length];
        NativeCoreResultCode callResult;
        try
        {
            callResult = nativeBatchExecutor.Apply(items, nativeResults);
        }
        catch (Exception exception) when (exception is DllNotFoundException
            or EntryPointNotFoundException
            or BadImageFormatException
            or TypeInitializationException
            or InvalidOperationException)
        {
            for (var index = 0; index < items.Length; index++)
            {
                AddFailureForNativeFields(
                    fieldsByRequest[requestIndexes[index]],
                    items[index].Fields,
                    $"NativeCore 进程策略执行器不可用：{exception.GetBaseException().Message}");
            }
            return;
        }

        if (callResult != NativeCoreResultCode.Ok)
        {
            for (var index = 0; index < items.Length; index++)
            {
                AddFailureForNativeFields(
                    fieldsByRequest[requestIndexes[index]],
                    items[index].Fields,
                    $"NativeCore 进程策略批处理失败：{callResult}。");
            }
            return;
        }

        for (var index = 0; index < items.Length; index++)
        {
            AppendNativeResult(
                fieldsByRequest[requestIndexes[index]],
                items[index],
                nativeResults[index]);
        }
    }

    private static void AppendNativeResult(
        List<ProcessResourcePolicyBatchFieldResult> target,
        NativeProcessPolicyBatchItem item,
        NativeProcessPolicyBatchItemResult result)
    {
        if (result.OpenError != 0)
        {
            AddFailureForNativeFields(
                target,
                result.RequestedFields,
                $"进程不可打开：{Win32Message(result.OpenError)}",
                result.OpenError);
            return;
        }

        if (result.IdentityError != 0)
        {
            var message = result.IdentityError == 1168
                ? "进程实例已经变化，拒绝写入策略。"
                : $"无法验证进程实例：{Win32Message(result.IdentityError)}";
            AddFailureForNativeFields(
                target,
                result.RequestedFields,
                message,
                result.IdentityError);
            return;
        }

        AppendField(target, result, NativeProcessPolicyFields.PriorityClass, result.PriorityError, "进程优先级已写入。");
        AppendField(target, result, NativeProcessPolicyFields.AffinityMask, result.AffinityError, "CPU affinity 已写入。");
        AppendMemoryPriorityField(target, item, result);
        AppendField(target, result, NativeProcessPolicyFields.PowerThrottling, result.PowerError, "进程节流状态已写入。");
        AppendField(target, result, NativeProcessPolicyFields.TrimWorkingSet, result.TrimError, "工作集裁剪已请求。");
    }

    private static void AppendMemoryPriorityField(
        List<ProcessResourcePolicyBatchFieldResult> target,
        NativeProcessPolicyBatchItem item,
        NativeProcessPolicyBatchItemResult result)
    {
        if ((result.RequestedFields & NativeProcessPolicyFields.MemoryPriority) == 0)
        {
            return;
        }

        target.Add(MapMemoryPriorityFieldResult(item, result));
    }

    internal static ProcessResourcePolicyBatchFieldResult MapMemoryPriorityFieldResult(
        NativeProcessPolicyBatchItem item,
        NativeProcessPolicyBatchItemResult result)
    {
        var succeeded =
            (result.SucceededFields & NativeProcessPolicyFields.MemoryPriority) != 0;
        var conflict = result.MemoryError == NativeProcessPolicyBatchAbi.MemoryPriorityConflictError;
        var status = succeeded
            ? ProcessResourcePolicyBatchFieldStatus.Succeeded
            : conflict
                ? ProcessResourcePolicyBatchFieldStatus.Conflict
                : ProcessResourcePolicyBatchFieldStatus.Failed;
        var message = succeeded
            ? "内存优先级已写入。"
            : conflict
                ? $"内存优先级已被其他来源修改：预期 {item.ExpectedMemoryPriority}，实际 {result.ObservedMemoryPriority}；拒绝写入。"
                : Win32Message(result.MemoryError);
        return new ProcessResourcePolicyBatchFieldResult(
            ProcessResourcePolicyBatchFields.MemoryPriority,
            status,
            message)
        {
            ErrorCode = succeeded ? 0 : result.MemoryError
        };
    }

    private static void AppendField(
        List<ProcessResourcePolicyBatchFieldResult> target,
        NativeProcessPolicyBatchItemResult result,
        NativeProcessPolicyFields nativeField,
        uint error,
        string successMessage)
    {
        if ((result.RequestedFields & nativeField) == 0)
        {
            return;
        }

        var field = ToPublicField(nativeField);
        var succeeded = (result.SucceededFields & nativeField) != 0;
        target.Add(new ProcessResourcePolicyBatchFieldResult(
            field,
            succeeded,
            succeeded ? successMessage : Win32Message(error))
        {
            ErrorCode = succeeded ? 0 : error
        });
    }

    private static void AddFailureForNativeFields(
        List<ProcessResourcePolicyBatchFieldResult> target,
        NativeProcessPolicyFields fields,
        string message,
        uint errorCode = 0)
    {
        foreach (var nativeField in OrderedNativeFields)
        {
            if ((fields & nativeField) != 0)
            {
                target.Add(Failure(
                    ToPublicField(nativeField),
                    message,
                    errorCode));
            }
        }
    }

    private static ProcessResourcePolicyBatchFieldResult Failure(
        ProcessResourcePolicyBatchFields field,
        string message,
        uint errorCode = 0)
    {
        return new ProcessResourcePolicyBatchFieldResult(field, false, message)
        {
            ErrorCode = errorCode
        };
    }

    private static string Win32Message(uint error)
    {
        return error == 0
            ? "Windows 未返回具体错误。"
            : new Win32Exception(unchecked((int)error)).Message;
    }

    private static ProcessResourcePolicyBatchFields ToPublicField(NativeProcessPolicyFields field)
    {
        return field switch
        {
            NativeProcessPolicyFields.PriorityClass => ProcessResourcePolicyBatchFields.PriorityClass,
            NativeProcessPolicyFields.AffinityMask => ProcessResourcePolicyBatchFields.AffinityMask,
            NativeProcessPolicyFields.MemoryPriority => ProcessResourcePolicyBatchFields.MemoryPriority,
            NativeProcessPolicyFields.PowerThrottling => ProcessResourcePolicyBatchFields.PowerThrottling,
            NativeProcessPolicyFields.TrimWorkingSet => ProcessResourcePolicyBatchFields.TrimWorkingSet,
            _ => ProcessResourcePolicyBatchFields.None
        };
    }

    private static readonly NativeProcessPolicyFields[] OrderedNativeFields =
    [
        NativeProcessPolicyFields.PriorityClass,
        NativeProcessPolicyFields.AffinityMask,
        NativeProcessPolicyFields.MemoryPriority,
        NativeProcessPolicyFields.PowerThrottling,
        NativeProcessPolicyFields.TrimWorkingSet
    ];

    private readonly record struct PreparedCpuSetWrite(
        int RequestIndex,
        int ProcessId,
        DateTimeOffset? ExpectedStartedAt,
        uint[] CpuSetIds);
}
