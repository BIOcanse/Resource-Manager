using System.Collections.Frozen;
using System.Globalization;
using ResourceManager.App.Domain.CpuTopology;
using ResourceManager.App.Infrastructure.CpuTopology;

namespace ResourceManager.ReadWrite.Windows.Processes;

public sealed partial class EtwCpuResidencyCaptureOwner
{
    // Native rows arrive sorted by (process instance, thread instance, logical
    // processor id), so every process and every thread is one contiguous run.
    // The projection relies on that grouping: it walks the rows once, keeps the
    // current process in arrays indexed by processor position (and by physical
    // core and CCD index), and creates each thread row when its run ends. Rows
    // out of that order are rejected rather than silently split.
    internal static IReadOnlyList<CpuProcessCoreResidency> BuildProcessSnapshot(
        CpuResidencyCaptureConfiguration plan,
        long sessionGeneration,
        CpuResidencyAggregationSnapshot aggregate)
    {
        var windowQpc = checked(aggregate.MeasuredThroughQpc - aggregate.WindowStartQpc);
        if (!aggregate.IsComplete || windowQpc <= 0 || aggregate.QpcFrequency <= 0)
        {
            throw new InvalidDataException("Physical-core usage requires a complete positive execution-time window.");
        }
        var projection = new ResidencyProjection(plan.PhysicalCoreByLogicalProcessor,
            sessionGeneration, aggregate.QpcFrequency, windowQpc);
        foreach (var item in aggregate.Records) projection.Add(item);
        return projection.Complete();
    }

    private sealed class ResidencyProjection
    {
        private readonly long generation, qpcFrequency, windowQpc;
        // Plan: positions are the logical processor ids in ascending order.
        private readonly int[] logicalIds;
        private readonly CpuResidencyCoreAccounting[] coreByPosition, coreByIndex;
        private readonly int[] coreIndexByPosition, coreIdClassByPosition, ccdIndexByPosition;
        // Current process.
        private bool hasProcess;
        private CpuExecutionTimeAggregate processFirst;
        private long processDurationQpc;
        private int processSwitches;
        private readonly long[] logicalDurationQpc;
        private readonly int[] logicalSwitches, logicalTouched;
        private int logicalTouchedCount;
        private readonly long[] coreDurationQpc;
        private readonly int[] coreSwitches, coreTouched;
        private int coreTouchedCount;
        private readonly double[] ccdMilliseconds, ccdShare;
        private readonly int[] ccdSwitches, ccdTouched;
        private readonly string?[] ccdNames;
        private int ccdTouchedCount;
        private Ordered<CpuThreadCoreResidency>[] threadRows = new Ordered<CpuThreadCoreResidency>[16];
        private int threadRowCount;
        // Current thread.
        private bool hasThread;
        private long threadInstanceId;
        private int threadId, threadSwitches;
        private long threadDurationQpc;
        private readonly long[] threadLogicalDurationQpc;
        private readonly int[] threadTouched;
        private int threadTouchedCount;
        private readonly int[] coreIdStamp;
        private int threadStamp;
        private readonly string[] coreIdWorkspace;
        // Ordering check and results.
        private bool hasKey;
        private long keyProcessInstanceId, keyThreadInstanceId;
        private readonly List<Ordered<CpuProcessCoreResidency>> processes = [];

        internal ResidencyProjection(FrozenDictionary<int, CpuResidencyCoreAccounting> physicalByLogicalId,
            long generation, long qpcFrequency, long windowQpc)
        {
            this.generation = generation;
            this.qpcFrequency = qpcFrequency;
            this.windowQpc = windowQpc;
            var count = physicalByLogicalId.Count;
            logicalIds = new int[count];
            physicalByLogicalId.Keys.CopyTo(logicalIds, 0);
            Array.Sort(logicalIds);
            coreByPosition = new CpuResidencyCoreAccounting[count];
            coreIndexByPosition = new int[count];
            coreIdClassByPosition = new int[count];
            ccdIndexByPosition = new int[count];
            // Physical cores group by accounting identity (record equality), thread core ids by
            // case-insensitive id, CCDs by case-insensitive non-blank id, as the rows report them.
            var coreIndex = new Dictionary<CpuResidencyCoreAccounting, int>(count);
            var coreIdClass = new Dictionary<string, int>(count, StringComparer.OrdinalIgnoreCase);
            var ccdIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var coreList = new List<CpuResidencyCoreAccounting>(count);
            for (var position = 0; position < count; position++)
            {
                var core = physicalByLogicalId[logicalIds[position]];
                coreByPosition[position] = core;
                if (!coreIndex.TryGetValue(core, out var index))
                {
                    coreIndex.Add(core, index = coreIndex.Count);
                    coreList.Add(core);
                }
                coreIndexByPosition[position] = index;
                if (!coreIdClass.TryGetValue(core.PhysicalCoreId, out var idClass)) coreIdClass.Add(core.PhysicalCoreId, idClass = coreIdClass.Count);
                coreIdClassByPosition[position] = idClass;
                if (string.IsNullOrWhiteSpace(core.CcdId)) ccdIndexByPosition[position] = -1;
                else
                {
                    if (!ccdIndex.TryGetValue(core.CcdId, out var ccd)) ccdIndex.Add(core.CcdId, ccd = ccdIndex.Count);
                    ccdIndexByPosition[position] = ccd;
                }
            }
            coreByIndex = coreList.ToArray();
            logicalDurationQpc = new long[count];
            logicalSwitches = new int[count];
            logicalTouched = new int[count];
            coreDurationQpc = new long[coreIndex.Count];
            coreSwitches = new int[coreIndex.Count];
            coreTouched = new int[coreIndex.Count];
            ccdMilliseconds = new double[ccdIndex.Count];
            ccdShare = new double[ccdIndex.Count];
            ccdSwitches = new int[ccdIndex.Count];
            ccdTouched = new int[ccdIndex.Count];
            ccdNames = new string?[ccdIndex.Count];
            threadLogicalDurationQpc = new long[count];
            threadTouched = new int[count];
            coreIdStamp = new int[coreIdClass.Count];
            coreIdWorkspace = new string[coreIdClass.Count];
        }

        internal void Add(CpuExecutionTimeAggregate item)
        {
            if (hasKey)
            {
                if (item.ProcessInstanceId != keyProcessInstanceId)
                {
                    if (item.ProcessInstanceId < keyProcessInstanceId)
                        throw new InvalidDataException("CPU residency rows are not grouped by process instance.");
                    CloseThread();
                    CloseProcess();
                }
                else if (item.ThreadInstanceId != keyThreadInstanceId)
                {
                    if (item.ThreadInstanceId < keyThreadInstanceId)
                        throw new InvalidDataException("CPU residency rows are not grouped by thread instance.");
                    CloseThread();
                }
            }
            hasKey = true;
            keyProcessInstanceId = item.ProcessInstanceId;
            keyThreadInstanceId = item.ThreadInstanceId;
            if (item.ExecutionTimeQpc <= 0) return;
            var position = Array.BinarySearch(logicalIds, item.LogicalProcessorId);
            if (position < 0) return;

            if (!hasProcess)
            {
                hasProcess = true;
                processFirst = item;
            }
            processDurationQpc = checked(processDurationQpc + item.ExecutionTimeQpc);
            processSwitches = checked(processSwitches + item.SwitchCount);
            if (logicalDurationQpc[position] == 0) logicalTouched[logicalTouchedCount++] = position;
            logicalDurationQpc[position] = checked(logicalDurationQpc[position] + item.ExecutionTimeQpc);
            logicalSwitches[position] = checked(logicalSwitches[position] + item.SwitchCount);
            var core = coreIndexByPosition[position];
            if (coreDurationQpc[core] == 0) coreTouched[coreTouchedCount++] = core;
            coreDurationQpc[core] = checked(coreDurationQpc[core] + item.ExecutionTimeQpc);
            coreSwitches[core] = checked(coreSwitches[core] + item.SwitchCount);

            if (!hasThread)
            {
                hasThread = true;
                threadInstanceId = item.ThreadInstanceId;
                threadId = item.ThreadId;
            }
            threadDurationQpc = checked(threadDurationQpc + item.ExecutionTimeQpc);
            threadSwitches = checked(threadSwitches + item.SwitchCount);
            if (threadLogicalDurationQpc[position] == 0) threadTouched[threadTouchedCount++] = position;
            threadLogicalDurationQpc[position] = checked(threadLogicalDurationQpc[position] + item.ExecutionTimeQpc);
        }

        internal IReadOnlyList<CpuProcessCoreResidency> Complete()
        {
            CloseThread();
            CloseProcess();
            var ordered = processes.ToArray();
            Array.Sort(ordered, static (left, right) =>
            {
                var byDuration = right.Row.ExecutionTimeMilliseconds.CompareTo(left.Row.ExecutionTimeMilliseconds);
                if (byDuration != 0) return byDuration;
                var byProcessId = left.Row.ProcessId.CompareTo(right.Row.ProcessId);
                return byProcessId != 0 ? byProcessId : left.Order.CompareTo(right.Order);
            });
            var result = new CpuProcessCoreResidency[ordered.Length];
            for (var index = 0; index < result.Length; index++) result[index] = ordered[index].Row;
            return result;
        }

        private void CloseThread()
        {
            if (!hasThread) return;
            var primaryPosition = -1;
            var primaryDurationQpc = long.MinValue;
            threadStamp++;
            var coreIdCount = 0;
            for (var index = 0; index < threadTouchedCount; index++)
            {
                var position = threadTouched[index];
                var duration = threadLogicalDurationQpc[position];
                // Longest processor; equal durations go to the lower logical id.
                if (duration > primaryDurationQpc
                    || duration == primaryDurationQpc && logicalIds[position] < logicalIds[primaryPosition])
                {
                    primaryPosition = position;
                    primaryDurationQpc = duration;
                }
                var idClass = coreIdClassByPosition[position];
                if (coreIdStamp[idClass] != threadStamp)
                {
                    coreIdStamp[idClass] = threadStamp;
                    coreIdWorkspace[coreIdCount++] = coreByPosition[position].PhysicalCoreId;
                }
                threadLogicalDurationQpc[position] = 0;
            }
            var physicalCoreIds = new string[coreIdCount];
            Array.Copy(coreIdWorkspace, physicalCoreIds, coreIdCount);
            Array.Sort(physicalCoreIds, StringComparer.OrdinalIgnoreCase);
            var core = coreByPosition[primaryPosition];
            var row = new CpuThreadCoreResidency(
                FormatInstanceId(generation, threadInstanceId),
                threadId,
                ToMilliseconds(threadDurationQpc, qpcFrequency),
                threadSwitches,
                string.IsNullOrWhiteSpace(core.CcdId) ? null : core.CcdId,
                core.PhysicalCoreId,
                logicalIds[primaryPosition],
                physicalCoreIds);
            if (threadRowCount == threadRows.Length) Array.Resize(ref threadRows, threadRows.Length * 2);
            threadRows[threadRowCount] = new(row, threadRowCount);
            threadRowCount++;
            hasThread = false;
            threadDurationQpc = 0;
            threadSwitches = 0;
            threadTouchedCount = 0;
        }

        private void CloseProcess()
        {
            if (!hasProcess) return;
            var total = processDurationQpc;

            // Order carries the processor position here; logical ids are unique within a process.
            var logical = new Ordered<CpuLogicalProcessorResidency>[logicalTouchedCount];
            for (var index = 0; index < logical.Length; index++)
            {
                var position = logicalTouched[index];
                var core = coreByPosition[position];
                logical[index] = new(new CpuLogicalProcessorResidency(
                    logicalIds[position],
                    core.PhysicalCoreId,
                    core.CcdId,
                    ToMilliseconds(logicalDurationQpc[position], qpcFrequency),
                    logicalSwitches[position],
                    Share(logicalDurationQpc[position], total)), position);
                logicalDurationQpc[position] = 0;
                logicalSwitches[position] = 0;
            }
            Array.Sort(logical, static (left, right) =>
            {
                var byDuration = right.Row.ExecutionTimeMilliseconds.CompareTo(left.Row.ExecutionTimeMilliseconds);
                return byDuration != 0 ? byDuration : left.Row.LogicalProcessorId.CompareTo(right.Row.LogicalProcessorId);
            });
            var logicalProcessors = new CpuLogicalProcessorResidency[logical.Length];
            for (var index = 0; index < logical.Length; index++) logicalProcessors[index] = logical[index].Row;

            var physical = new Ordered<CpuPhysicalCoreResidency>[coreTouchedCount];
            for (var index = 0; index < physical.Length; index++)
            {
                var coreIndex = coreTouched[index];
                var core = coreByIndex[coreIndex];
                var duration = coreDurationQpc[coreIndex];
                physical[index] = new(new CpuPhysicalCoreResidency(
                    core.PhysicalCoreId,
                    core.CcdId,
                    ToMilliseconds(duration, qpcFrequency),
                    coreSwitches[coreIndex],
                    Share(duration, total),
                    100.0 * ((double)duration / windowQpc) * core.LogicalProcessorShare), index);
                coreDurationQpc[coreIndex] = 0;
                coreSwitches[coreIndex] = 0;
            }
            Array.Sort(physical, static (left, right) =>
            {
                var byDuration = right.Row.ExecutionTimeMilliseconds.CompareTo(left.Row.ExecutionTimeMilliseconds);
                if (byDuration != 0) return byDuration;
                var byCoreId = StringComparer.OrdinalIgnoreCase.Compare(left.Row.PhysicalCoreId, right.Row.PhysicalCoreId);
                return byCoreId != 0 ? byCoreId : left.Order.CompareTo(right.Order);
            });
            var physicalCores = new CpuPhysicalCoreResidency[physical.Length];
            for (var index = 0; index < physical.Length; index++) physicalCores[index] = physical[index].Row;

            // CCD sums follow the sorted logical processor rows, in that order.
            for (var index = 0; index < logical.Length; index++)
            {
                var ccd = ccdIndexByPosition[logical[index].Order];
                if (ccd < 0) continue;
                var row = logical[index].Row;
                if (ccdNames[ccd] is null)
                {
                    ccdNames[ccd] = row.CcdId;
                    ccdTouched[ccdTouchedCount++] = ccd;
                }
                ccdMilliseconds[ccd] += row.ExecutionTimeMilliseconds;
                ccdSwitches[ccd] = checked(ccdSwitches[ccd] + row.SwitchCount);
                ccdShare[ccd] += row.SharePercent;
            }
            var ccdOrder = new Ordered<int>[ccdTouchedCount];
            for (var index = 0; index < ccdOrder.Length; index++) ccdOrder[index] = new(ccdTouched[index], index);
            if (ccdOrder.Length > 1)
            {
                Array.Sort(ccdOrder, (left, right) =>
                {
                    var byDuration = Math.Round(ccdMilliseconds[right.Row], 3).CompareTo(Math.Round(ccdMilliseconds[left.Row], 3));
                    if (byDuration != 0) return byDuration;
                    var byCcdId = StringComparer.OrdinalIgnoreCase.Compare(ccdNames[left.Row], ccdNames[right.Row]);
                    return byCcdId != 0 ? byCcdId : left.Order.CompareTo(right.Order);
                });
            }
            var ccds = new CpuCcdResidency[ccdOrder.Length];
            for (var index = 0; index < ccds.Length; index++)
            {
                var ccd = ccdOrder[index].Row;
                ccds[index] = new CpuCcdResidency(
                    ccdNames[ccd]!,
                    Math.Round(ccdMilliseconds[ccd], 3),
                    ccdSwitches[ccd],
                    Math.Round(ccdShare[ccd], 2));
            }
            for (var index = 0; index < ccdTouchedCount; index++)
            {
                var ccd = ccdTouched[index];
                ccdNames[ccd] = null;
                ccdMilliseconds[ccd] = 0;
                ccdSwitches[ccd] = 0;
                ccdShare[ccd] = 0;
            }

            Array.Sort(threadRows, 0, threadRowCount, ThreadOrder.Instance);
            var threads = new CpuThreadCoreResidency[threadRowCount];
            for (var index = 0; index < threads.Length; index++)
            {
                threads[index] = threadRows[index].Row;
                threadRows[index] = default;
            }

            var first = processFirst;
            processes.Add(new(new CpuProcessCoreResidency(
                FormatInstanceId(generation, first.ProcessInstanceId),
                first.ProcessId,
                first.ProcessStartKey?.ToString(CultureInfo.InvariantCulture),
                first.ProcessName,
                ToMilliseconds(total, qpcFrequency),
                processSwitches,
                threads.Length,
                ccds.Length == 0 ? null : ccds[0].CcdId,
                physicalCores.Length == 0 ? null : physicalCores[0].PhysicalCoreId,
                logicalProcessors.Length == 0 ? null : logicalProcessors[0].LogicalProcessorId,
                ccds,
                physicalCores,
                logicalProcessors,
                threads), processes.Count));
            hasProcess = false;
            processDurationQpc = 0;
            processSwitches = 0;
            logicalTouchedCount = 0;
            coreTouchedCount = 0;
            ccdTouchedCount = 0;
            threadRowCount = 0;
        }

        private sealed class ThreadOrder : IComparer<Ordered<CpuThreadCoreResidency>>
        {
            internal static readonly ThreadOrder Instance = new();
            public int Compare(Ordered<CpuThreadCoreResidency> left, Ordered<CpuThreadCoreResidency> right)
            {
                var byDuration = right.Row.ExecutionTimeMilliseconds.CompareTo(left.Row.ExecutionTimeMilliseconds);
                if (byDuration != 0) return byDuration;
                var byThreadId = left.Row.ThreadId.CompareTo(right.Row.ThreadId);
                return byThreadId != 0 ? byThreadId : left.Order.CompareTo(right.Order);
            }
        }
    }

    // A row with its first-appearance order, the last sort key.
    private readonly record struct Ordered<T>(T Row, int Order);
}
