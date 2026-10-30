using System.Runtime.InteropServices;
using ResourceManager.App.Infrastructure.CpuTopology;

namespace Resource_Manager_APP.Tests.Oracles.KernelEtw;

internal sealed class OracleCpuResidencyAggregationBuffer
{
    private readonly object sync = new();
    private readonly long qpcFrequency, windowQpc, bucketWidthQpc;
    private readonly int maximumActivePairs, bucketCount, maximumBucketCount;
    private OracleCaptureBucketStorage storage;
    private readonly Dictionary<int, ProcessState> currentProcesses = [];
    private readonly Dictionary<int, ThreadState> currentThreads = [];
    private readonly Dictionary<int, RunningState> runningByLogicalProcessor = [];
    private readonly Dictionary<int, long> continuousSinceByLogicalProcessor = [];
    private readonly Dictionary<int, ProcessorBoundary> lastBoundaryByLogicalProcessor = [];
    private readonly Dictionary<AggregateKey, PairTotals> pairs = [];
    private readonly long maximumBucketSlots;
    private long retainedBucketSlots;
    private OracleCaptureBucketStorage.Position? pendingBuckets;
    private int pendingBucketCount;
    private long retainedWindowStartQpc = long.MinValue;
    private long lastPrunedBucket = -1, lastRetentionAttemptBucket = -1, latestEventQpc = -1;
    private long nextProcessInstanceId, nextThreadInstanceId, continuityEpoch, inventoryVersion, capacityResetCount;
    private CpuResidencyCapacityResetDiagnostics? lastCapacityReset;

    public OracleCpuResidencyAggregationBuffer(long qpcFrequency, int observationWindowMilliseconds,
        int maximumActivePairs, int bucketWidthMilliseconds = 50,
        Func<int, OracleCaptureBucketMemory>? allocateBuckets = null)
    {
        if (qpcFrequency <= 0) throw new ArgumentOutOfRangeException(nameof(qpcFrequency));
        if (observationWindowMilliseconds <= 0) throw new ArgumentOutOfRangeException(nameof(observationWindowMilliseconds));
        if (maximumActivePairs <= 0) throw new ArgumentOutOfRangeException(nameof(maximumActivePairs));
        if (bucketWidthMilliseconds <= 0) throw new ArgumentOutOfRangeException(nameof(bucketWidthMilliseconds));
        this.qpcFrequency = qpcFrequency;
        windowQpc = DivideRoundUp(checked(qpcFrequency * observationWindowMilliseconds), 1000);
        bucketWidthQpc = DivideRoundUp(checked(qpcFrequency * bucketWidthMilliseconds), 1000);
        bucketCount = checked((int)DivideRoundUp(windowQpc, bucketWidthQpc) + 2);
        maximumBucketCount = checked(bucketCount * 2);
        this.maximumActivePairs = maximumActivePairs;
        maximumBucketSlots = checked((long)maximumActivePairs * bucketCount);
        _ = checked(maximumBucketSlots * Marshal.SizeOf<BucketTotal>());
        storage = new(allocateBuckets);
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
            SeedProcessCore(processId, processStartKey, processName);
        }
    }

    private ProcessState SeedProcessCore(int processId, long? processStartKey, string? processName)
    {
        if (currentProcesses.TryGetValue(processId, out var existing))
        {
            var identity = existing.Identity;
            if (identity.UniqueProcessKey != 0)
            {
                // A later PID inventory cannot attach a raw birth to an unverified event instance.
                if (identity.ProcessStartKey is > 0 && identity.ProcessStartKey == processStartKey)
                    existing.UpdateIdentity(0, processStartKey, processName);
                return existing;
            }

            if (processStartKey is > 0
                && (identity.ProcessStartKey is not > 0 || processStartKey != identity.ProcessStartKey))
            {
                RetireProcessCore(existing);
            }
            else
            {
                existing.UpdateIdentity(0, processStartKey, processName);
                return existing;
            }
        }

        var created = CreateProcessCore(processId, 0, processStartKey, processName);
        currentProcesses[processId] = created;
        return created;
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

            SeedThreadCore(process, threadId);
        }
    }

    private void SeedThreadCore(ProcessState process, int threadId)
    {
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

    internal InventorySeed CaptureInventorySeed()
    {
        lock (sync) return new InventorySeed(this, inventoryVersion);
    }

    public void ResetAfterEventLoss()
    {
        lock (sync)
        {
            inventoryVersion = checked(inventoryVersion + 1);
            currentProcesses.Clear(); currentProcesses.TrimExcess();
            currentThreads.Clear(); currentThreads.TrimExcess();
            ResetContinuityCore();
            // Keep the QPC floor and monotonically increasing identity IDs.
            // A delayed event or a reused PID must not become the old instance again.
        }
    }

    internal sealed class InventorySeed(OracleCpuResidencyAggregationBuffer owner, long version)
    {
        internal SeededProcess? SeedProcess(int processId, long? startKey, string? name)
        {
            if (processId <= 0) return null;
            lock (owner.sync)
            {
                if (owner.inventoryVersion != version) return null;
                if (startKey is > 0
                    && owner.currentProcesses.TryGetValue(processId, out var current)
                    && current.Identity.ProcessStartKey is > 0
                    && current.Identity.ProcessStartKey != startKey) return null;
                if (owner.currentProcesses.TryGetValue(processId, out var eventProcess)
                    && eventProcess.Identity.UniqueProcessKey != 0 && eventProcess.Identity.ProcessStartKey is not > 0) return null;
                var process = owner.SeedProcessCore(processId, startKey, name);
                return new SeededProcess(owner, version, processId, process.Identity.InstanceId);
            }
        }
    }

    internal sealed class SeededProcess(OracleCpuResidencyAggregationBuffer owner, long version, int processId, long instanceId)
    {
        internal void SeedThread(int threadId)
        {
            if (threadId <= 0) return;
            lock (owner.sync)
            {
                if (owner.inventoryVersion != version
                    || !owner.currentProcesses.TryGetValue(processId, out var process)
                    || process.Identity.InstanceId != instanceId) return;
                owner.SeedThreadCore(process, threadId);
            }
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
            if (pendingBuckets is not null) ResetContinuityCore();
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
            else if (!previous.IsIdle && previous.Process is not null && previous.Thread is not null)
            {
                // Unattributed execution is omitted; it is not a gap in context-switch continuity.
                bool appended;
                try
                {
                    appended = AppendClosedTotalsCore(in previous, logicalProcessorId, eventQpc);
                }
                catch { ResetContinuityCore(); throw; }
                if (!appended)
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

        lock (sync)
        {
            if (pendingBuckets is not null || expectedLogicalProcessorIds.Count == 0) return IncompleteSnapshotCore();
            var expected = new HashSet<int>(expectedLogicalProcessorIds);
            if (expected.Count != expectedLogicalProcessorIds.Count) return IncompleteSnapshotCore();
            var measuredThroughQpc = long.MaxValue;
            DateTimeOffset? measuredThrough = null;
            foreach (var id in expected)
            {
                if (!lastBoundaryByLogicalProcessor.TryGetValue(id, out var boundary)) return IncompleteSnapshotCore();
                if (measuredThrough is null || boundary.Qpc < measuredThroughQpc)
                { measuredThroughQpc = boundary.Qpc; measuredThrough = boundary.Time; }
            }
            var start = Math.Max(0, measuredThroughQpc - windowQpc);
            AdvanceRetentionCore(start);
            if (measuredThroughQpc < windowQpc || expected.Any(id =>
                !continuousSinceByLogicalProcessor.TryGetValue(id, out var since) || since > start))
                return new(qpcFrequency, start, measuredThroughQpc, measuredThrough, false, []);
            var rows = new List<CpuExecutionTimeAggregate>(pairs.Count);
            var firstBucket = start / bucketWidthQpc;
            var lastBucket = (measuredThroughQpc - 1) / bucketWidthQpc;
            foreach (var pair in pairs)
            {
                if (!expected.Contains(pair.Key.LogicalProcessorId)) continue;
                long duration = 0; int switches = 0;
                var cells = Cells(pair.Value);
                var boundary = lastBoundaryByLogicalProcessor[pair.Key.LogicalProcessorId].Qpc;
                var slot = (int)(firstBucket % cells.Length);
                for (var id = firstBucket; id <= lastBucket; id++)
                {
                    ref var cell = ref cells[slot];
                    if (++slot == cells.Length) slot = 0;
                    if (cell.Id != id) continue;
                    if (id > firstBucket && id < lastBucket)
                    {
                        duration = checked(duration + cell.Duration);
                        switches = checked(switches + cell.Switches);
                        continue;
                    }
                    var bucketStart = id * bucketWidthQpc;
                    var coverageStart = bucketStart;
                    var coverageEnd = bucketStart > long.MaxValue - bucketWidthQpc
                        ? boundary : Math.Min(boundary, bucketStart + bucketWidthQpc);
                    var from = Math.Max(start, coverageStart);
                    var to = Math.Min(measuredThroughQpc, coverageEnd);
                    var covered = coverageEnd - coverageStart;
                    if (covered <= 0 || to <= from) continue;
                    var overlap = to - from;
                    duration = checked(duration + (overlap == covered ? cell.Duration
                        : (long)((Int128)cell.Duration * overlap / covered)));
                    if (bucketStart >= start) switches = checked(switches + cell.Switches);
                }
                if (duration == 0 && switches == 0) continue;
                var identity = pair.Value.Process.Identity;
                rows.Add(new(identity.InstanceId, identity.ProcessId, identity.ProcessStartKey, identity.ProcessName,
                    pair.Key.ThreadInstanceId, pair.Value.Thread.ThreadId, pair.Key.LogicalProcessorId, duration, switches));
            }
            return new(qpcFrequency, start, measuredThroughQpc, measuredThrough, true, rows.ToArray());
        }
    }

    // A published/paused window has no reader of these totals. Keep the run's
    // process/thread identities; the next window resets its own continuity.
    public void ReleaseWindowBuckets()
    {
        lock (sync) ClearBucketTotalsCore();
    }

    internal void SetBucketStorage(OracleCaptureBucketStorage next)
    {
        lock (sync)
        {
            if (ReferenceEquals(storage, next)) return;
            ClearBucketTotalsCore();
            storage = next;
        }
    }

    private Span<BucketTotal> Cells(PairTotals totals)
        => MemoryMarshal.Cast<byte, BucketTotal>(storage.Bytes(totals.Position));

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
            inventoryVersion = checked(inventoryVersion + 1);
            currentProcesses.Clear(); currentProcesses.TrimExcess();
            currentThreads.Clear(); currentThreads.TrimExcess();
            runningByLogicalProcessor.Clear();
            continuousSinceByLogicalProcessor.Clear();
            lastBoundaryByLogicalProcessor.Clear();
            ClearBucketTotalsCore();
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

            foreach (var slice in pairs.Values)
            {
                retainedProcesses.Add(slice.Process.Identity.InstanceId);
                retainedThreads.Add(slice.Thread.InstanceId);
            }

            return new CpuResidencyAggregationDiagnostics(
                qpcFrequency,
                windowQpc,
                maximumActivePairs,
                pairs.Count,
                bucketCount,
                maximumBucketCount,
                bucketWidthQpc,
                (retainedBucketSlots + pendingBucketCount) * Marshal.SizeOf<BucketTotal>(),
                (maximumBucketSlots + maximumBucketCount) * Marshal.SizeOf<BucketTotal>(),
                currentProcesses.Count,
                currentThreads.Count,
                retainedProcesses.Count,
                retainedThreads.Count,
                continuityEpoch,
                capacityResetCount)
            { LastCapacityReset = lastCapacityReset };
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
            return new RunningState(processId, 0, null, null, eventQpc, true);
        }

        // Add/retire updates both current indexes under sync; a current thread already owns a current process.
        if (threadId < 0 || !currentThreads.TryGetValue(threadId, out var thread))
            return new RunningState(processId, threadId, null, null, eventQpc, false);
        var identity = thread.Process.Identity;
        if (processId > 0 && identity.ProcessId != processId)
            return new RunningState(processId, threadId, null, null, eventQpc, false);
        if (processName is not null && !ReferenceEquals(processName, identity.ProcessName))
            thread.Process.UpdateIdentity(0, null, processName);
        return new RunningState(
            identity.ProcessId,
            threadId,
            thread.Process,
            thread,
            eventQpc,
            false);
    }

    private bool AppendClosedTotalsCore(in RunningState previous, int logicalProcessorId, long prospectiveBoundaryQpc)
    {
        var eventBucket = prospectiveBoundaryQpc / bucketWidthQpc;
        if (eventBucket != lastRetentionAttemptBucket || pairs.Count >= maximumActivePairs)
        {
            AdvanceRetentionForProspectiveBoundaryCore(logicalProcessorId, prospectiveBoundaryQpc);
            lastRetentionAttemptBucket = eventBucket;
        }
        var start = Math.Max(previous.StartQpc, Math.Max(0, retainedWindowStartQpc));
        if (prospectiveBoundaryQpc <= start) return true;
        var thread = previous.Thread!;
        var totals = thread.LastTotals;
        if (totals is null || totals.LogicalProcessorId != logicalProcessorId)
        {
            var key = new AggregateKey(thread.InstanceId, logicalProcessorId);
            if (!pairs.TryGetValue(key, out totals))
            {
                if (pairs.Count >= maximumActivePairs)
                    return RejectCapacityCore("active-pair-limit", prospectiveBoundaryQpc, bucketCount);
                if (retainedBucketSlots + bucketCount > maximumBucketSlots)
                    return RejectCapacityCore("bucket-storage-limit", prospectiveBoundaryQpc, bucketCount);
                var position = storage.Allocate(checked(bucketCount * Marshal.SizeOf<BucketTotal>()));
                try
                {
                    totals = new PairTotals(previous.Process!, thread, logicalProcessorId, position);
                    pairs.Add(key, totals);
                }
                catch { storage.Release(position); throw; }
                retainedBucketSlots += bucketCount;
            }
            thread.LastTotals = totals;
        }
        var firstId = start / bucketWidthQpc;
        var lastId = (prospectiveBoundaryQpc - 1) / bucketWidthQpc;
        var retainedId = Math.Max(0, retainedWindowStartQpc) / bucketWidthQpc;
        var required = lastId - retainedId + 1;
        if (required > maximumBucketCount)
            return RejectCapacityCore("bucket-retention-span", prospectiveBoundaryQpc, required);
        if (required > totals.CellCount && !ResizeTotalsCore(totals, (int)required, retainedId))
            return RejectCapacityCore("bucket-resize-limit", prospectiveBoundaryQpc, required);
        var cells = Cells(totals);
        for (var id = firstId; id <= lastId; id++)
        {
            ref var cell = ref cells[(int)(id % cells.Length)];
            if (cell.Id != id)
            {
                // A lagging processor can hold the common boundary back. Never overwrite still-needed totals.
                if (cell.Duration != 0 && cell.Id >= Math.Max(0, retainedWindowStartQpc) / bucketWidthQpc)
                    return RejectCapacityCore("bucket-collision", prospectiveBoundaryQpc, required);
                cell = new BucketTotal { Id = id };
            }
            var bucketStart = id * bucketWidthQpc;
            var end = bucketStart > long.MaxValue - bucketWidthQpc ? prospectiveBoundaryQpc : Math.Min(prospectiveBoundaryQpc, bucketStart + bucketWidthQpc);
            cell.Duration = checked(cell.Duration + end - Math.Max(start, bucketStart));
            if (previous.StartQpc >= bucketStart && previous.StartQpc - bucketStart < bucketWidthQpc)
                cell.Switches = checked(cell.Switches + 1);
        }
        totals.LastEndQpc = prospectiveBoundaryQpc;
        return true;
    }

    private bool RejectCapacityCore(string reason, long eventQpc, long requiredBucketCount)
    {
        // Keep one bounded failure snapshot before ResetContinuityCore clears
        // the pairs and buckets. Post-reset counts cannot identify which limit
        // rejected the closed interval.
        lastCapacityReset = new(reason, eventQpc,
            retainedWindowStartQpc == long.MinValue ? null : retainedWindowStartQpc,
            pairs.Count, maximumActivePairs, retainedBucketSlots, maximumBucketSlots,
            requiredBucketCount, maximumBucketCount, currentProcesses.Count, currentThreads.Count);
        return false;
    }

    private bool ResizeTotalsCore(PairTotals totals, int count, long retainedId)
    {
        var previousCount = totals.CellCount;
        if (retainedBucketSlots - previousCount + count > maximumBucketSlots) return false;
        // One replacement for the entire capture, retained until both copy and old close succeed.
        if (pendingBuckets is not null) throw new InvalidOperationException("CPU bucket storage requires close retry.");
        pendingBuckets = storage.Allocate(checked(count * Marshal.SizeOf<BucketTotal>()));
        pendingBucketCount = count;
        var next = MemoryMarshal.Cast<byte, BucketTotal>(storage.Bytes(pendingBuckets.Value));
        foreach (var cell in Cells(totals))
            if (cell.Duration != 0 && cell.Id >= retainedId) next[(int)(cell.Id % next.Length)] = cell;
        storage.Release(totals.Position);
        totals.Position = pendingBuckets.Value; pendingBuckets = null; pendingBucketCount = 0; totals.CellCount = count;
        retainedBucketSlots += count - previousCount;
        return true;
    }

    private void AdvanceRetentionForProspectiveBoundaryCore(int logicalProcessorId, long prospectiveBoundaryQpc)
    {
        var minimum = prospectiveBoundaryQpc;
        foreach (var pair in lastBoundaryByLogicalProcessor)
            if (pair.Key != logicalProcessorId) minimum = Math.Min(minimum, pair.Value.Qpc);
        if (minimum >= windowQpc) AdvanceRetentionCore(minimum - windowQpc);
    }

    private void AdvanceRetentionCore(long start)
    {
        if (start <= retainedWindowStartQpc) return;
        retainedWindowStartQpc = start;
        var bucket = start / bucketWidthQpc;
        if (bucket <= lastPrunedBucket) return;
        lastPrunedBucket = bucket;
        // Dictionary removal during value enumeration is supported; disposal precedes removal so failures retain ownership.
        foreach (var pair in pairs)
            if (pair.Value.LastEndQpc <= bucket * bucketWidthQpc)
            { RetirePairCore(pair.Key, pair.Value); }
            else if (pair.Value.CellCount > bucketCount
                && (pair.Value.LastEndQpc - 1) / bucketWidthQpc - bucket + 1 <= bucketCount)
                ResizeTotalsCore(pair.Value, bucketCount, bucket);
        if (pairs.EnsureCapacity(0) > Math.Max(16, pairs.Count * 4)) pairs.TrimExcess();
    }

    private void RetirePairCore(AggregateKey key, PairTotals totals)
    {
        var count = totals.CellCount;
        storage.Release(totals.Position);
        if (ReferenceEquals(totals.Thread.LastTotals, totals)) totals.Thread.LastTotals = null;
        pairs.Remove(key); retainedBucketSlots -= count;
    }

    private void ResetContinuityCore()
    {
        runningByLogicalProcessor.Clear();
        continuousSinceByLogicalProcessor.Clear();
        lastBoundaryByLogicalProcessor.Clear();
        ClearBucketTotalsCore();
        continuityEpoch = checked(continuityEpoch + 1);
    }

    private void ClearBucketTotalsCore()
    {
        if (pendingBuckets is { } pending) { storage.Release(pending); pendingBuckets = null; pendingBucketCount = 0; }
        foreach (var pair in pairs)
        { RetirePairCore(pair.Key, pair.Value); }
        pairs.TrimExcess();
        retainedWindowStartQpc = long.MinValue;
        lastPrunedBucket = -1; lastRetentionAttemptBucket = -1;
    }

    private void RetireProcessCore(ProcessState process)
    {
        var identity = process.Identity;
        if (currentProcesses.TryGetValue(identity.ProcessId, out var current)
            && ReferenceEquals(current, process))
        {
            currentProcesses.Remove(identity.ProcessId);
            if (currentProcesses.EnsureCapacity(0) > Math.Max(16, currentProcesses.Count * 4)) currentProcesses.TrimExcess();
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
            if (currentThreads.EnsureCapacity(0) > Math.Max(16, currentThreads.Count * 4)) currentThreads.TrimExcess();
        }

        if (thread.Process.Threads.TryGetValue(thread.ThreadId, out var processThread)
            && ReferenceEquals(processThread, thread))
        {
            thread.Process.Threads.Remove(thread.ThreadId);
            if (thread.Process.Threads.EnsureCapacity(0) > Math.Max(16, thread.Process.Threads.Count * 4)) thread.Process.Threads.TrimExcess();
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

        return false;
    }

    private static string NormalizeProcessName(
        int processId,
        string? candidate,
        string? existing)
    {
        if (!string.IsNullOrWhiteSpace(candidate))
        {
            var normalized = candidate.AsSpan().Trim();
            if (existing is not null
                && normalized.Equals(existing.AsSpan(), StringComparison.Ordinal))
            {
                return existing;
            }

            return normalized.Length == candidate.Length
                ? candidate
                : normalized.ToString();
        }

        return !string.IsNullOrWhiteSpace(existing)
            ? existing
            : $"PID {processId}";
    }

    private static long DivideRoundUp(long numerator, long denominator)
        => checked((numerator + denominator - 1) / denominator);

    private readonly record struct RunningState(
        int RawProcessId,
        int RawThreadId,
        ProcessState? Process,
        ThreadState? Thread,
        long StartQpc,
        bool IsIdle);

    private readonly record struct ProcessorBoundary(long Qpc, DateTimeOffset Time);

    // Thread instance IDs are global within this buffer and already identify the owning process instance.
    private readonly record struct AggregateKey(long ThreadInstanceId, int LogicalProcessorId);

    private struct BucketTotal
    {
        internal long Id, Duration;
        internal int Switches;
    }

    private sealed class PairTotals(ProcessState process, ThreadState thread, int logicalProcessorId, OracleCaptureBucketStorage.Position position)
    {
        internal ProcessState Process { get; } = process;
        internal ThreadState Thread { get; } = thread;
        internal int LogicalProcessorId { get; } = logicalProcessorId;
        internal OracleCaptureBucketStorage.Position Position { get; set; } = position;
        internal int CellCount { get; set; } = position.Length / Marshal.SizeOf<BucketTotal>();
        internal long LastEndQpc;
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
            var nextUniqueProcessKey = current.UniqueProcessKey != 0
                ? current.UniqueProcessKey
                : uniqueProcessKey;
            var nextProcessStartKey = current.ProcessStartKey is > 0
                ? current.ProcessStartKey
                : processStartKey;
            var nextProcessName = NormalizeProcessName(
                current.ProcessId,
                processName,
                current.ProcessName);
            if (nextUniqueProcessKey == current.UniqueProcessKey
                && nextProcessStartKey == current.ProcessStartKey
                && string.Equals(nextProcessName, current.ProcessName, StringComparison.Ordinal))
            {
                return;
            }

            var next = current with
            {
                UniqueProcessKey = nextUniqueProcessKey,
                ProcessStartKey = nextProcessStartKey,
                ProcessName = nextProcessName
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

    private sealed record ThreadState(long InstanceId, int ThreadId, ProcessState Process)
    {
        // One reference, not another history. Retirement clears it; migrated threads use the main pair index.
        internal PairTotals? LastTotals;
    }

}



// The active window/run owns the chunks. Aggregates retain only a position.
// Allocation policy may borrow a region from its enclosing lexical Ledger scope.
internal sealed class OracleCaptureBucketStorage(Func<int, OracleCaptureBucketMemory>? allocate = null) : IDisposable
{
    private readonly Dictionary<long, OracleCaptureBucketMemory> chunks = [];
    private long next;
    private bool disposed;
    internal readonly record struct Position(long Id, int Length);
    internal Position Allocate(int length)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var id = checked(++next);
        var memory = (allocate ?? OracleCaptureBucketMemory.AllocateManaged)(length);
        var position = new Position(id, length);
        try { chunks.Add(position.Id, memory); }
        catch { memory.Dispose(); throw; }
        return position;
    }
    internal Span<byte> Bytes(Position position)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        return chunks[position.Id].Bytes[..position.Length];
    }
    internal void Release(Position position)
    {
        if (!chunks.TryGetValue(position.Id, out var memory)) return;
        memory.Dispose();
        chunks.Remove(position.Id);
        if (chunks.Count == 0) chunks.TrimExcess();
    }
    public void Dispose()
    {
        foreach (var chunk in chunks)
        {
            chunk.Value.Dispose();
            chunks.Remove(chunk.Key);
        }
        chunks.TrimExcess();
        disposed = true;
    }
}
