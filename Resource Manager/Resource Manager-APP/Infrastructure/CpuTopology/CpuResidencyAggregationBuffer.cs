namespace ResourceManager.App.Infrastructure.CpuTopology;

internal readonly record struct CpuExecutionTimeAggregate(
    long ProcessInstanceId,
    int ProcessId,
    long? ProcessStartKey,
    string ProcessName,
    long ThreadInstanceId,
    int ThreadId,
    int LogicalProcessorId,
    long ExecutionTimeQpc,
    int SwitchCount);

internal sealed record CpuResidencyAggregationSnapshot(
    long QpcFrequency,
    long WindowStartQpc,
    long MeasuredThroughQpc,
    DateTimeOffset? MeasuredThrough,
    bool IsComplete,
    IReadOnlyList<CpuExecutionTimeAggregate> Records);

internal readonly record struct CpuResidencyAggregationDiagnostics(
    long QpcFrequency,
    long WindowQpc,
    int MaximumClosedSlices,
    int ClosedSliceCount,
    int SegmentCount,
    int CurrentProcessCount,
    int CurrentThreadCount,
    int RetainedProcessIdentityCount,
    int RetainedThreadIdentityCount,
    long ContinuityEpoch,
    long CapacityResetCount);

internal sealed class CpuResidencyAggregationBuffer
{
    // Segments stay below the LOH threshold and become immutable before a snapshot sees them.
    private const int ClosedSliceSegmentCapacity = 1024;

    private readonly object sync = new();
    private readonly long qpcFrequency;
    private readonly long windowQpc;
    private readonly int maximumClosedSlices;
    private readonly Dictionary<int, ProcessState> currentProcesses = [];
    private readonly Dictionary<int, ThreadState> currentThreads = [];
    private readonly Dictionary<int, RunningState> runningByLogicalProcessor = [];
    private readonly Dictionary<int, long> continuousSinceByLogicalProcessor = [];
    private readonly Dictionary<int, ProcessorBoundary> lastBoundaryByLogicalProcessor = [];
    private readonly Queue<ClosedSliceSegment> closedSegments = [];
    private ClosedSliceSegment? writableSegment;
    private int closedSegmentHeadOffset;
    private int closedSliceCount;
    private long retainedWindowStartQpc = long.MinValue;
    private long latestEventQpc = -1;
    private long nextProcessInstanceId;
    private long nextThreadInstanceId;
    private long continuityEpoch;
    private long capacityResetCount;

    public CpuResidencyAggregationBuffer(
        long qpcFrequency,
        int observationWindowMilliseconds,
        int maximumClosedSlices)
    {
        if (qpcFrequency <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(qpcFrequency));
        }

        if (observationWindowMilliseconds <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(observationWindowMilliseconds));
        }

        if (maximumClosedSlices <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumClosedSlices));
        }

        this.qpcFrequency = qpcFrequency;
        windowQpc = checked(DivideRoundUp(
            checked(qpcFrequency * observationWindowMilliseconds),
            1000));
        this.maximumClosedSlices = maximumClosedSlices;
    }

    public void ObserveProcessStart(
        long eventQpc,
        DateTimeOffset eventTime,
        int processId,
        ulong uniqueProcessKey,
        long? processStartKey,
        string? processName)
    {
        if (processId <= 0)
        {
            return;
        }

        lock (sync)
        {
            if (!TryAdvanceEventTimeCore(eventQpc))
            {
                return;
            }

            if (currentProcesses.TryGetValue(processId, out var existing)
                && IsSameProcessInstance(existing.Identity, uniqueProcessKey, processStartKey))
            {
                existing.UpdateIdentity(uniqueProcessKey, processStartKey, processName);
                return;
            }

            if (existing is not null)
            {
                RetireProcessCore(existing);
            }

            var process = CreateProcessCore(
                processId,
                uniqueProcessKey,
                processStartKey,
                processName);
            currentProcesses[processId] = process;
        }
    }

    public void SeedProcess(
        int processId,
        long? processStartKey,
        string? processName)
    {
        if (processId <= 0)
        {
            return;
        }

        lock (sync)
        {
            if (currentProcesses.TryGetValue(processId, out var existing))
            {
                var identity = existing.Identity;
                if (identity.UniqueProcessKey != 0)
                {
                    existing.UpdateIdentity(0, processStartKey, processName);
                    return;
                }

                if (processStartKey is > 0
                    && identity.ProcessStartKey is > 0
                    && processStartKey != identity.ProcessStartKey)
                {
                    RetireProcessCore(existing);
                }
                else
                {
                    existing.UpdateIdentity(0, processStartKey, processName);
                    return;
                }
            }

            currentProcesses[processId] = CreateProcessCore(
                processId,
                0,
                processStartKey,
                processName);
        }
    }

    public void SeedThread(int processId, int threadId)
    {
        if (processId <= 0 || threadId <= 0)
        {
            return;
        }

        lock (sync)
        {
            if (!currentProcesses.TryGetValue(processId, out var process))
            {
                return;
            }

            if (currentThreads.TryGetValue(threadId, out var existing))
            {
                if (ReferenceEquals(existing.Process, process))
                {
                    return;
                }

                RetireThreadCore(existing);
            }

            AddThreadCore(process, threadId);
        }
    }

    public void ObserveProcessEnd(
        long eventQpc,
        DateTimeOffset eventTime,
        int processId,
        ulong uniqueProcessKey)
    {
        if (processId <= 0)
        {
            return;
        }

        lock (sync)
        {
            if (!TryAdvanceEventTimeCore(eventQpc)
                || !currentProcesses.TryGetValue(processId, out var process))
            {
                return;
            }

            var identity = process.Identity;
            if (uniqueProcessKey != 0
                && identity.UniqueProcessKey != 0
                && uniqueProcessKey != identity.UniqueProcessKey)
            {
                return;
            }

            RetireProcessCore(process);
        }
    }

    public void ObserveThreadStart(
        long eventQpc,
        DateTimeOffset eventTime,
        int processId,
        int threadId)
    {
        if (processId <= 0 || threadId <= 0)
        {
            return;
        }

        lock (sync)
        {
            if (!TryAdvanceEventTimeCore(eventQpc)
                || !currentProcesses.TryGetValue(processId, out var process))
            {
                return;
            }

            if (currentThreads.TryGetValue(threadId, out var existing))
            {
                if (ReferenceEquals(existing.Process, process))
                {
                    return;
                }

                RetireThreadCore(existing);
            }

            AddThreadCore(process, threadId);
        }
    }

    public void ObserveThreadEnd(
        long eventQpc,
        DateTimeOffset eventTime,
        int processId,
        int threadId)
    {
        if (processId <= 0 || threadId <= 0)
        {
            return;
        }

        lock (sync)
        {
            if (!TryAdvanceEventTimeCore(eventQpc)
                || !currentThreads.TryGetValue(threadId, out var thread)
                || thread.Process.Identity.ProcessId != processId)
            {
                return;
            }

            RetireThreadCore(thread);
        }
    }

    public void ObserveContextSwitch(
        long eventQpc,
        DateTimeOffset eventTime,
        int logicalProcessorId,
        int oldProcessId,
        int oldThreadId,
        int newProcessId,
        string? newProcessName,
        int newThreadId)
    {
        if (logicalProcessorId < 0)
        {
            return;
        }

        lock (sync)
        {
            if (!TryAdvanceEventTimeCore(eventQpc))
            {
                return;
            }

            var hasPrevious = runningByLogicalProcessor.TryGetValue(logicalProcessorId, out var previous);
            if (!hasPrevious)
            {
                continuousSinceByLogicalProcessor[logicalProcessorId] = eventQpc;
            }
            else if (previous.RawThreadId != oldThreadId
                || (previous.RawProcessId > 0
                    && oldProcessId > 0
                    && previous.RawProcessId != oldProcessId))
            {
                continuousSinceByLogicalProcessor[logicalProcessorId] = eventQpc;
            }
            else if (!previous.IsIdle)
            {
                if (previous.Process is null || previous.Thread is null)
                {
                    continuousSinceByLogicalProcessor[logicalProcessorId] = eventQpc;
                }
                else if (!AppendClosedSliceCore(
                    new ClosedSlice(
                        previous.StartQpc,
                        eventQpc,
                        previous.Process,
                        previous.Thread,
                        logicalProcessorId,
                        previous.SwitchInQpc),
                    logicalProcessorId,
                    eventQpc))
                {
                    capacityResetCount = checked(capacityResetCount + 1);
                    ResetContinuityCore();
                    continuousSinceByLogicalProcessor[logicalProcessorId] = eventQpc;
                }
            }

            runningByLogicalProcessor[logicalProcessorId] = ResolveRunningStateCore(
                eventQpc,
                newProcessId,
                newProcessName,
                newThreadId);
            lastBoundaryByLogicalProcessor[logicalProcessorId] = new ProcessorBoundary(eventQpc, eventTime);
        }
    }

    public CpuResidencyAggregationSnapshot Snapshot(
        IReadOnlyCollection<int> expectedLogicalProcessorIds)
    {
        ArgumentNullException.ThrowIfNull(expectedLogicalProcessorIds);

        SnapshotCapture capture;
        lock (sync)
        {
            if (expectedLogicalProcessorIds.Count == 0)
            {
                return IncompleteSnapshotCore();
            }

            var expected = new HashSet<int>(expectedLogicalProcessorIds);
            if (expected.Count != expectedLogicalProcessorIds.Count)
            {
                return IncompleteSnapshotCore();
            }

            var foundBoundary = false;
            var measuredThroughQpc = 0L;
            DateTimeOffset? measuredThrough = null;
            foreach (var logicalProcessorId in expected)
            {
                if (!lastBoundaryByLogicalProcessor.TryGetValue(logicalProcessorId, out var boundary))
                {
                    return IncompleteSnapshotCore();
                }

                if (!foundBoundary || boundary.Qpc < measuredThroughQpc)
                {
                    foundBoundary = true;
                    measuredThroughQpc = boundary.Qpc;
                    measuredThrough = boundary.Time;
                }
            }

            if (!foundBoundary || measuredThroughQpc < windowQpc)
            {
                return new CpuResidencyAggregationSnapshot(
                    qpcFrequency,
                    0,
                    measuredThroughQpc,
                    measuredThrough,
                    false,
                    []);
            }

            var windowStartQpc = measuredThroughQpc - windowQpc;
            AdvanceRetentionCore(windowStartQpc);
            foreach (var logicalProcessorId in expected)
            {
                if (!continuousSinceByLogicalProcessor.TryGetValue(logicalProcessorId, out var continuousSince)
                    || continuousSince > windowStartQpc)
                {
                    return new CpuResidencyAggregationSnapshot(
                        qpcFrequency,
                        windowStartQpc,
                        measuredThroughQpc,
                        measuredThrough,
                        false,
                        []);
                }
            }

            SealWritableSegmentCore();
            capture = new SnapshotCapture(
                continuityEpoch,
                qpcFrequency,
                windowStartQpc,
                measuredThroughQpc,
                measuredThrough,
                expected,
                CaptureRangesCore());
        }

        var records = Aggregate(capture);
        lock (sync)
        {
            if (continuityEpoch != capture.ContinuityEpoch)
            {
                return IncompleteSnapshotCore();
            }
        }

        return new CpuResidencyAggregationSnapshot(
            capture.QpcFrequency,
            capture.WindowStartQpc,
            capture.MeasuredThroughQpc,
            capture.MeasuredThrough,
            true,
            records);
    }

    public void ResetContinuity()
    {
        lock (sync)
        {
            ResetContinuityCore();
        }
    }

    public void Clear()
    {
        lock (sync)
        {
            currentProcesses.Clear();
            currentThreads.Clear();
            runningByLogicalProcessor.Clear();
            continuousSinceByLogicalProcessor.Clear();
            lastBoundaryByLogicalProcessor.Clear();
            ClearClosedSlicesCore();
            latestEventQpc = -1;
            nextProcessInstanceId = 0;
            nextThreadInstanceId = 0;
            continuityEpoch = checked(continuityEpoch + 1);
        }
    }

    internal CpuResidencyAggregationDiagnostics GetDiagnostics()
    {
        lock (sync)
        {
            var retainedProcesses = new HashSet<long>(
                currentProcesses.Values.Select(static process => process.Identity.InstanceId));
            var retainedThreads = new HashSet<long>(
                currentThreads.Values.Select(static thread => thread.InstanceId));
            foreach (var running in runningByLogicalProcessor.Values)
            {
                if (running.Process is not null)
                {
                    retainedProcesses.Add(running.Process.Identity.InstanceId);
                }

                if (running.Thread is not null)
                {
                    retainedThreads.Add(running.Thread.InstanceId);
                }
            }

            foreach (var slice in EnumerateClosedSlicesCore())
            {
                retainedProcesses.Add(slice.Process.Identity.InstanceId);
                retainedThreads.Add(slice.Thread.InstanceId);
            }

            return new CpuResidencyAggregationDiagnostics(
                qpcFrequency,
                windowQpc,
                maximumClosedSlices,
                closedSliceCount,
                closedSegments.Count + (writableSegment is null ? 0 : 1),
                currentProcesses.Count,
                currentThreads.Count,
                retainedProcesses.Count,
                retainedThreads.Count,
                continuityEpoch,
                capacityResetCount);
        }
    }

    private ProcessState CreateProcessCore(
        int processId,
        ulong uniqueProcessKey,
        long? processStartKey,
        string? processName)
        => new(new ProcessIdentity(
            checked(++nextProcessInstanceId),
            processId,
            uniqueProcessKey,
            processStartKey,
            NormalizeProcessName(processId, processName, null)));

    private void AddThreadCore(ProcessState process, int threadId)
    {
        var thread = new ThreadState(
            checked(++nextThreadInstanceId),
            threadId,
            process);
        currentThreads[threadId] = thread;
        process.Threads[threadId] = thread;
    }

    private bool TryAdvanceEventTimeCore(long eventQpc)
    {
        if (eventQpc < 0)
        {
            return false;
        }

        if (latestEventQpc >= 0 && eventQpc < latestEventQpc)
        {
            ResetContinuityCore();
            return false;
        }

        latestEventQpc = eventQpc;
        return true;
    }

    private RunningState ResolveRunningStateCore(
        long eventQpc,
        int processId,
        string? processName,
        int threadId)
    {
        if (threadId == 0)
        {
            return new RunningState(processId, 0, null, null, eventQpc, eventQpc, true);
        }

        if (threadId < 0
            || !currentThreads.TryGetValue(threadId, out var thread)
            || !currentProcesses.TryGetValue(thread.Process.Identity.ProcessId, out var currentProcess)
            || !ReferenceEquals(currentProcess, thread.Process)
            || (processId > 0 && thread.Process.Identity.ProcessId != processId))
        {
            return new RunningState(processId, threadId, null, null, eventQpc, eventQpc, false);
        }

        thread.Process.UpdateIdentity(0, null, processName);
        return new RunningState(
            thread.Process.Identity.ProcessId,
            threadId,
            thread.Process,
            thread,
            eventQpc,
            eventQpc,
            false);
    }

    private bool AppendClosedSliceCore(
        ClosedSlice slice,
        int logicalProcessorId,
        long prospectiveBoundaryQpc)
    {
        if (closedSliceCount == maximumClosedSlices)
        {
            AdvanceRetentionForProspectiveBoundaryCore(logicalProcessorId, prospectiveBoundaryQpc);
            if (closedSliceCount == maximumClosedSlices)
            {
                return false;
            }
        }

        writableSegment ??= new ClosedSliceSegment(
            Math.Min(ClosedSliceSegmentCapacity, maximumClosedSlices - closedSliceCount));
        writableSegment.Append(slice);
        closedSliceCount++;
        if (writableSegment.IsFull)
        {
            closedSegments.Enqueue(writableSegment);
            writableSegment = null;
        }

        return true;
    }

    private void AdvanceRetentionForProspectiveBoundaryCore(
        int logicalProcessorId,
        long prospectiveBoundaryQpc)
    {
        var foundBoundary = false;
        var minimumBoundary = 0L;
        foreach (var pair in lastBoundaryByLogicalProcessor)
        {
            var boundary = pair.Key == logicalProcessorId
                ? prospectiveBoundaryQpc
                : pair.Value.Qpc;
            if (!foundBoundary || boundary < minimumBoundary)
            {
                foundBoundary = true;
                minimumBoundary = boundary;
            }
        }

        if (!lastBoundaryByLogicalProcessor.ContainsKey(logicalProcessorId))
        {
            minimumBoundary = foundBoundary
                ? Math.Min(minimumBoundary, prospectiveBoundaryQpc)
                : prospectiveBoundaryQpc;
            foundBoundary = true;
        }

        if (foundBoundary && minimumBoundary >= windowQpc)
        {
            AdvanceRetentionCore(minimumBoundary - windowQpc);
        }
    }

    private void AdvanceRetentionCore(long windowStartQpc)
    {
        if (windowStartQpc <= retainedWindowStartQpc)
        {
            return;
        }

        retainedWindowStartQpc = windowStartQpc;
        SealWritableSegmentCore();
        while (closedSegments.TryPeek(out var segment))
        {
            while (closedSegmentHeadOffset < segment.Count
                && segment[closedSegmentHeadOffset].EndQpc <= windowStartQpc)
            {
                closedSegmentHeadOffset++;
                closedSliceCount--;
            }

            if (closedSegmentHeadOffset < segment.Count)
            {
                break;
            }

            closedSegments.Dequeue();
            closedSegmentHeadOffset = 0;
        }
    }

    private void SealWritableSegmentCore()
    {
        if (writableSegment is null)
        {
            return;
        }

        closedSegments.Enqueue(writableSegment);
        writableSegment = null;
    }

    private SliceRange[] CaptureRangesCore()
    {
        if (closedSegments.Count == 0)
        {
            return [];
        }

        var ranges = new SliceRange[closedSegments.Count];
        var index = 0;
        foreach (var segment in closedSegments)
        {
            var start = index == 0 ? closedSegmentHeadOffset : 0;
            ranges[index++] = new SliceRange(segment, start, segment.Count - start);
        }

        return ranges;
    }

    private IEnumerable<ClosedSlice> EnumerateClosedSlicesCore()
    {
        var segmentIndex = 0;
        foreach (var segment in closedSegments)
        {
            var start = segmentIndex++ == 0 ? closedSegmentHeadOffset : 0;
            for (var index = start; index < segment.Count; index++)
            {
                yield return segment[index];
            }
        }

        if (writableSegment is not null)
        {
            for (var index = 0; index < writableSegment.Count; index++)
            {
                yield return writableSegment[index];
            }
        }
    }

    private static IReadOnlyList<CpuExecutionTimeAggregate> Aggregate(SnapshotCapture capture)
    {
        var aggregates = new Dictionary<AggregateKey, MutableAggregate>();
        foreach (var range in capture.Ranges)
        {
            var end = range.Start + range.Count;
            for (var index = range.Start; index < end; index++)
            {
                var slice = range.Segment[index];
                if (slice.EndQpc <= capture.WindowStartQpc
                    || slice.StartQpc >= capture.MeasuredThroughQpc
                    || !capture.ExpectedLogicalProcessorIds.Contains(slice.LogicalProcessorId))
                {
                    continue;
                }

                var overlapStart = Math.Max(slice.StartQpc, capture.WindowStartQpc);
                var overlapEnd = Math.Min(slice.EndQpc, capture.MeasuredThroughQpc);
                var executionTimeQpc = overlapEnd > overlapStart
                    ? checked(overlapEnd - overlapStart)
                    : 0;
                var switchCount = slice.SwitchInQpc >= capture.WindowStartQpc
                    && slice.SwitchInQpc < capture.MeasuredThroughQpc
                        ? 1
                        : 0;
                var identity = slice.Process.Identity;
                var key = new AggregateKey(
                    identity.InstanceId,
                    slice.Thread.InstanceId,
                    slice.LogicalProcessorId);
                if (!aggregates.TryGetValue(key, out var aggregate))
                {
                    aggregate = new MutableAggregate(identity, slice.Thread.ThreadId);
                    aggregates.Add(key, aggregate);
                }

                aggregate.ExecutionTimeQpc = checked(aggregate.ExecutionTimeQpc + executionTimeQpc);
                aggregate.SwitchCount = checked(aggregate.SwitchCount + switchCount);
            }
        }

        return aggregates.Select(static pair => new CpuExecutionTimeAggregate(
            pair.Key.ProcessInstanceId,
            pair.Value.Process.ProcessId,
            pair.Value.Process.ProcessStartKey,
            pair.Value.Process.ProcessName,
            pair.Key.ThreadInstanceId,
            pair.Value.ThreadId,
            pair.Key.LogicalProcessorId,
            pair.Value.ExecutionTimeQpc,
            pair.Value.SwitchCount)).ToArray();
    }

    private void ResetContinuityCore()
    {
        runningByLogicalProcessor.Clear();
        continuousSinceByLogicalProcessor.Clear();
        lastBoundaryByLogicalProcessor.Clear();
        ClearClosedSlicesCore();
        continuityEpoch = checked(continuityEpoch + 1);
    }

    private void ClearClosedSlicesCore()
    {
        closedSegments.Clear();
        writableSegment = null;
        closedSegmentHeadOffset = 0;
        closedSliceCount = 0;
        retainedWindowStartQpc = long.MinValue;
    }

    private void RetireProcessCore(ProcessState process)
    {
        var identity = process.Identity;
        if (currentProcesses.TryGetValue(identity.ProcessId, out var current)
            && ReferenceEquals(current, process))
        {
            currentProcesses.Remove(identity.ProcessId);
        }

        foreach (var thread in process.Threads.Values.ToArray())
        {
            RetireThreadCore(thread);
        }
    }

    private void RetireThreadCore(ThreadState thread)
    {
        if (currentThreads.TryGetValue(thread.ThreadId, out var current)
            && ReferenceEquals(current, thread))
        {
            currentThreads.Remove(thread.ThreadId);
        }

        if (thread.Process.Threads.TryGetValue(thread.ThreadId, out var processThread)
            && ReferenceEquals(processThread, thread))
        {
            thread.Process.Threads.Remove(thread.ThreadId);
        }
    }

    private CpuResidencyAggregationSnapshot IncompleteSnapshotCore()
        => new(qpcFrequency, 0, 0, null, false, []);

    private static bool IsSameProcessInstance(
        ProcessIdentity process,
        ulong uniqueProcessKey,
        long? processStartKey)
    {
        if (process.UniqueProcessKey != 0 && uniqueProcessKey != 0)
        {
            return process.UniqueProcessKey == uniqueProcessKey;
        }

        if (process.ProcessStartKey is > 0 && processStartKey is > 0)
        {
            return process.ProcessStartKey == processStartKey;
        }

        return true;
    }

    private static string NormalizeProcessName(
        int processId,
        string? candidate,
        string? existing)
        => !string.IsNullOrWhiteSpace(candidate)
            ? candidate.Trim()
            : !string.IsNullOrWhiteSpace(existing)
                ? existing
                : $"PID {processId}";

    private static long DivideRoundUp(long numerator, long denominator)
        => checked((numerator + denominator - 1) / denominator);

    private readonly record struct ClosedSlice(
        long StartQpc,
        long EndQpc,
        ProcessState Process,
        ThreadState Thread,
        int LogicalProcessorId,
        long SwitchInQpc);

    private readonly record struct RunningState(
        int RawProcessId,
        int RawThreadId,
        ProcessState? Process,
        ThreadState? Thread,
        long StartQpc,
        long SwitchInQpc,
        bool IsIdle);

    private readonly record struct ProcessorBoundary(long Qpc, DateTimeOffset Time);

    private readonly record struct AggregateKey(
        long ProcessInstanceId,
        long ThreadInstanceId,
        int LogicalProcessorId);

    private sealed class MutableAggregate(ProcessIdentity process, int threadId)
    {
        public ProcessIdentity Process { get; } = process;
        public int ThreadId { get; } = threadId;
        public long ExecutionTimeQpc { get; set; }
        public int SwitchCount { get; set; }
    }

    private sealed class ProcessState(ProcessIdentity identity)
    {
        private ProcessIdentity identity = identity;

        public ProcessIdentity Identity => Volatile.Read(ref identity);
        public Dictionary<int, ThreadState> Threads { get; } = [];

        public void UpdateIdentity(
            ulong uniqueProcessKey,
            long? processStartKey,
            string? processName)
        {
            var current = Identity;
            var next = current with
            {
                UniqueProcessKey = current.UniqueProcessKey != 0
                    ? current.UniqueProcessKey
                    : uniqueProcessKey,
                ProcessStartKey = current.ProcessStartKey is > 0
                    ? current.ProcessStartKey
                    : processStartKey,
                ProcessName = NormalizeProcessName(
                    current.ProcessId,
                    processName,
                    current.ProcessName)
            };
            Volatile.Write(ref identity, next);
        }
    }

    private sealed record ProcessIdentity(
        long InstanceId,
        int ProcessId,
        ulong UniqueProcessKey,
        long? ProcessStartKey,
        string ProcessName);

    private sealed record ThreadState(
        long InstanceId,
        int ThreadId,
        ProcessState Process);

    private sealed class ClosedSliceSegment(int capacity)
    {
        private readonly ClosedSlice[] items = new ClosedSlice[capacity];

        public int Count { get; private set; }
        public bool IsFull => Count == items.Length;
        public ClosedSlice this[int index] => items[index];

        public void Append(ClosedSlice slice)
        {
            if (IsFull)
            {
                throw new InvalidOperationException("The CPU-residency slice segment is full.");
            }

            items[Count++] = slice;
        }
    }

    private readonly record struct SliceRange(
        ClosedSliceSegment Segment,
        int Start,
        int Count);

    private sealed record SnapshotCapture(
        long ContinuityEpoch,
        long QpcFrequency,
        long WindowStartQpc,
        long MeasuredThroughQpc,
        DateTimeOffset? MeasuredThrough,
        HashSet<int> ExpectedLogicalProcessorIds,
        SliceRange[] Ranges);
}
