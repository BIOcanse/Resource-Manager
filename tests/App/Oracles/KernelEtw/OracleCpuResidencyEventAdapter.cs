using ResourceManager.App.Infrastructure.CpuTopology;
using ResourceManager.ReadWrite.Windows.Processes;
using ResourceManager.App.Infrastructure.Telemetry.Etw;

namespace Resource_Manager_APP.Tests.Oracles.KernelEtw;

internal static class OracleCpuResidencyEventAdapter
{
    internal static OracleCpuResidencyAggregationBuffer CreateAggregationBuffer(
        CpuResidencyCaptureConfiguration plan,
        long qpcFrequency, Func<int, OracleCaptureBucketMemory>? allocateBuckets = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var published = plan.RequirePublished();
        return new OracleCpuResidencyAggregationBuffer(
            qpcFrequency,
            published.ObservationWindowMilliseconds,
            published.MaximumActivePairs, published.BucketWidthMilliseconds, allocateBuckets);
    }

    internal static void BindKernelEventCallbacks(
        OracleKernelEtwEventSource kernel,
        OracleCpuResidencyAggregationBuffer buffer,
        Func<bool> shouldObserve,
        Func<int, long?> readProcessStartKey,
        Func<int, int?>? resolveProcessorId = null)
    {
        ArgumentNullException.ThrowIfNull(readProcessStartKey);
        BindKernelEventCallbacks(kernel, buffer, shouldObserve, (pid, _) => readProcessStartKey(pid), resolveProcessorId);
    }

    internal static void BindKernelEventCallbacks(
        OracleKernelEtwEventSource kernel,
        OracleCpuResidencyAggregationBuffer buffer,
        Func<bool> shouldObserve,
        Func<int, DateTimeOffset, long?> readProcessStartKey,
        Func<int, int?>? resolveProcessorId = null,
        Action<int, long>? boundaryObserved = null,
        Action<long, DateTimeOffset, int, int, int, int, string?, int>? observeContextSwitch = null,
        bool includeContextSwitch = true)
    {
        ArgumentNullException.ThrowIfNull(kernel);
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentNullException.ThrowIfNull(shouldObserve);
        ArgumentNullException.ThrowIfNull(readProcessStartKey);
        kernel.ProcessStartGroup += data =>
        {
            if (!shouldObserve())
            {
                return;
            }

            buffer.ObserveProcessStart(
                data.TimeStampQPC,
                ToTimestamp(data.TimeStamp),
                data.ProcessID,
                unchecked((ulong)data.UniqueProcessKey),
                readProcessStartKey(data.ProcessID, ToTimestamp(data.TimeStamp)),
                FirstNonEmpty(data.ProcessName, data.ImageFileName));
        };
        kernel.ProcessEndGroup += data =>
        {
            if (!shouldObserve())
            {
                return;
            }

            buffer.ObserveProcessEnd(
                data.TimeStampQPC,
                ToTimestamp(data.TimeStamp),
                data.ProcessID,
                unchecked((ulong)data.UniqueProcessKey));
        };
        kernel.ThreadStartGroup += data =>
        {
            if (shouldObserve())
            {
                buffer.ObserveThreadStart(
                    data.TimeStampQPC,
                    ToTimestamp(data.TimeStamp),
                    data.ProcessID,
                    data.ThreadID);
            }
        };
        kernel.ThreadEndGroup += data =>
        {
            if (shouldObserve())
            {
                buffer.ObserveThreadEnd(
                    data.TimeStampQPC,
                    ToTimestamp(data.TimeStamp),
                    data.ProcessID,
                    data.ThreadID);
            }
        };
        if (!includeContextSwitch) return;
        BindContextSwitchCallbacks(kernel, buffer, shouldObserve, resolveProcessorId, boundaryObserved, observeContextSwitch);
    }

    private static void BindContextSwitchCallbacks(OracleKernelEtwEventSource kernel, OracleCpuResidencyAggregationBuffer buffer,
        Func<bool> shouldObserve, Func<int, int?>? resolveProcessorId = null, Action<int, long>? boundaryObserved = null,
        Action<long, DateTimeOffset, int, int, int, int, string?, int>? observeContextSwitch = null)
    {
        kernel.ThreadCSwitch += data =>
        {
            var processorId = resolveProcessorId is null ? data.ProcessorNumber : resolveProcessorId(data.ProcessorNumber);
            if (shouldObserve() && processorId is { } logicalProcessorId)
            {
                // A composition may serialize and gate finite CSwitch intake while keeping metadata live.
                if (observeContextSwitch is not null)
                {
                    observeContextSwitch(data.TimeStampQPC, ToTimestamp(data.TimeStamp), logicalProcessorId,
                        data.OldProcessID, data.OldThreadID, data.NewProcessID, data.NewProcessName, data.NewThreadID);
                    return;
                }
                buffer.ObserveContextSwitch(
                    data.TimeStampQPC,
                    ToTimestamp(data.TimeStamp),
                    logicalProcessorId,
                    data.OldProcessID,
                    data.OldThreadID,
                    data.NewProcessID,
                    data.NewProcessName,
                    data.NewThreadID);
                boundaryObserved?.Invoke(logicalProcessorId, data.TimeStampQPC);
            }
        };

    }

    internal static int? ResolveEventLogicalProcessorId(int eventProcessorIndex, IReadOnlyList<int> processorIds)
        => eventProcessorIndex >= 0 && eventProcessorIndex < processorIds.Count ? processorIds[eventProcessorIndex] : null;

    private static DateTimeOffset ToTimestamp(DateTime timestamp)
        => new(timestamp.Kind == DateTimeKind.Utc ? timestamp : timestamp.ToUniversalTime());

    private static string? FirstNonEmpty(string? first, string? second)
        => !string.IsNullOrWhiteSpace(first)
            ? first
            : !string.IsNullOrWhiteSpace(second)
                ? second
                : null;

}
