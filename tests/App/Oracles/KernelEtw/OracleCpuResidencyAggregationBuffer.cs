using ResourceManager.App.Infrastructure.CpuTopology;

namespace Resource_Manager_APP.Tests.Oracles.KernelEtw;

// Managed equivalence oracle of the native CPU residency window (ledger-residency-window design section 1).
// The expected processor set is fixed for the buffer's lifetime. A window begins with a QPC floor and
// a slot count: 0 = finite window [S, S + W); 1..2047 = continuous window of n slots per W, read as the
// last n complete slots. Pair storage exists only while a window measures; identities outlive windows.
internal sealed class OracleCpuResidencyAggregationBuffer
{
    internal const int MaximumSlotsPerWindow = 2047;
    internal const int DefaultMaximumProcessors = 4096;
    // Native storage model: 24 B pair records in fixed 2,730-record chunks (65,520 B), and per-pair
    // rings of R = 2n + 2 cells of 16 B in chunks of max(1, floor(65,536 / 16R)) rings.
    internal const int PairRecordBytes = 24;
    internal const int RecordsPerChunk = 2730;
    internal const int RingCellBytes = 16;
    private const int RingChunkBytes = 65536;
    private readonly object sync = new();
    private readonly long qpcFrequency, windowQpc;
    private readonly int maximumActivePairs;
    private readonly int[] logicalProcessorIds;
    private readonly Dictionary<int, int> processorIndexById;
    private readonly ProcessorState[] processors;
    private readonly Dictionary<int, ProcessState> currentProcesses = [];
    private readonly Dictionary<int, ThreadState> currentThreads = [];
    private readonly Dictionary<AggregateKey, PairState> pairs = [];
    private int knownProcessorCount, slotsPerWindow, ringSlots, peakPairCount;
    private long floorQpc;
    // S, E (finite), the cached live floor slot F and the slot of the last F recomputation (continuous).
    private bool measuring;
    private long startQpc, endQpc, liveFloorSlot, lastCrossingSlot;
    private DateTimeOffset startTime;
    private long latestEventQpc = -1;
    private long nextProcessInstanceId, nextThreadInstanceId, continuityEpoch, inventoryVersion, capacityResetCount;
    private CpuResidencyCapacityResetDiagnostics? lastCapacityReset;

    public OracleCpuResidencyAggregationBuffer(long qpcFrequency, int observationWindowMilliseconds,
        int maximumActivePairs, IReadOnlyList<int> logicalProcessorIds,
        int maximumProcessors = DefaultMaximumProcessors)
    {
        if (qpcFrequency <= 0) throw new ArgumentOutOfRangeException(nameof(qpcFrequency));
        if (observationWindowMilliseconds <= 0) throw new ArgumentOutOfRangeException(nameof(observationWindowMilliseconds));
        if (maximumActivePairs <= 0) throw new ArgumentOutOfRangeException(nameof(maximumActivePairs));
        if (maximumProcessors <= 0) throw new ArgumentOutOfRangeException(nameof(maximumProcessors));
        ArgumentNullException.ThrowIfNull(logicalProcessorIds);
        // Native InvalidConfig: an empty set, a negative id or a duplicate id.
        if (logicalProcessorIds.Count == 0)
            throw new ArgumentException("The expected processor set is empty.", nameof(logicalProcessorIds));
        // Native `processors` capacity failure.
        if (logicalProcessorIds.Count > maximumProcessors)
            throw new InvalidOperationException(
                $"CPU residency processors capacity exceeded: {logicalProcessorIds.Count} > {maximumProcessors}.");
        processorIndexById = new(logicalProcessorIds.Count);
        for (var index = 0; index < logicalProcessorIds.Count; index++)
        {
            var id = logicalProcessorIds[index];
            if (id < 0 || !processorIndexById.TryAdd(id, index))
                throw new ArgumentException($"Invalid or duplicate logical processor id {id}.", nameof(logicalProcessorIds));
        }

        this.qpcFrequency = qpcFrequency;
        windowQpc = DivideRoundUp(checked(qpcFrequency * observationWindowMilliseconds), 1000);
        this.maximumActivePairs = maximumActivePairs;
        this.logicalProcessorIds = [.. logicalProcessorIds];
        processors = new ProcessorState[this.logicalProcessorIds.Length];
    }

    public IReadOnlyList<int> LogicalProcessorIds => logicalProcessorIds;

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
            // Native order: a delayed record below the window floor is not a discontinuity in the new
            // window; then the event-time monotonic check; then the expected processor set.
            if (eventQpc < floorQpc
                || !TryAdvanceEventTimeCore(eventQpc)
                || !processorIndexById.TryGetValue(logicalProcessorId, out var index))
            {
                return;
            }

            var restart = false;
            if (processors[index].Known)
            {
                var previous = processors[index].Run;
                if (previous.RawThreadId != oldThreadId
                    || (previous.RawProcessId > 0
                        && oldProcessId > 0
                        && previous.RawProcessId != oldProcessId))
                {
                    // The processor's run is dropped. A measuring window restarts at this switch,
                    // unless it is finite and this processor had already covered E.
                    restart = measuring && (slotsPerWindow != 0 || processors[index].BoundaryQpc < endQpc);
                    if (restart) ClearPairsCore();
                }
                else if (measuring)
                {
                    if (slotsPerWindow != 0 && eventQpc >= BoundaryOf(lastCrossingSlot + 1))
                    {
                        AdvanceLiveFloorCore(index, eventQpc);
                        lastCrossingSlot = SlotOf(eventQpc);
                    }

                    // Unattributed execution is omitted; it is not a gap in context-switch continuity.
                    if (!previous.IsIdle && previous.Process is not null && previous.Thread is not null)
                    {
                        bool closed;
                        try
                        {
                            closed = CloseRunCore(in previous, index, eventQpc);
                        }
                        catch { ResetContinuityCore(); throw; }
                        if (!closed)
                        {
                            // The failing switch is the first boundary of the fresh window.
                            capacityResetCount = checked(capacityResetCount + 1);
                            ResetContinuityCore();
                        }
                    }
                }
            }

            var running = ResolveRunningStateCore(
                eventQpc,
                newProcessId,
                newProcessName,
                newThreadId);
            ref var processor = ref processors[index];
            processor.Run = running;
            processor.BoundaryQpc = eventQpc;
            processor.BoundaryTime = eventTime;
            if (!processor.Known)
            {
                processor.Known = true;
                // S: the switch that gives the last expected processor a known run.
                if (++knownProcessorCount == processors.Length) StartMeasuringCore(eventQpc, eventTime);
            }
            else if (restart)
            {
                StartMeasuringCore(eventQpc, eventTime);
            }
        }
    }

    public CpuResidencyAggregationSnapshot Snapshot()
    {
        lock (sync)
        {
            if (!measuring) return IncompleteSnapshotCore();
            var lowest = processors[0];
            for (var index = 1; index < processors.Length; index++)
                if (processors[index].BoundaryQpc < lowest.BoundaryQpc) lowest = processors[index];
            if (slotsPerWindow == 0)
            {
                if (lowest.BoundaryQpc < endQpc) return IncompleteSnapshotCore(lowest);
                var finiteRows = new List<CpuExecutionTimeAggregate>(pairs.Count);
                foreach (var pair in pairs.Values)
                    if (pair.Duration != 0 || pair.Switches != 0) finiteRows.Add(Row(pair, pair.Duration, pair.Switches));
                return CompleteSnapshotCore(startQpc, endQpc, finiteRows);
            }

            // Continuous: the last n complete slots [k_g - n, k_g), k_g = slot(lowest boundary).
            var through = SlotOf(lowest.BoundaryQpc);
            if (through < slotsPerWindow) return IncompleteSnapshotCore(lowest);
            var first = through - slotsPerWindow;
            var rows = new List<CpuExecutionTimeAggregate>(pairs.Count);
            foreach (var pair in pairs.Values)
            {
                long duration = 0;
                var switches = 0;
                var ring = pair.Ring!;
                for (var slot = first; slot < through; slot++)
                {
                    ref var cell = ref ring[(int)(slot % ringSlots)];
                    if (cell.Stamp != slot + 1) continue;
                    duration = checked(duration + cell.Duration);
                    switches = checked(switches + cell.Switches);
                }

                if (duration != 0 || switches != 0) rows.Add(Row(pair, duration, switches));
            }

            return CompleteSnapshotCore((long)BoundaryOf(first), (long)BoundaryOf(through), rows);
        }
    }

    /// <summary>Begins a window: 0 slots = finite, 1..2047 = continuous. Releases all window storage.</summary>
    public void BeginWindow(long floorQpc, int slotsPerWindow)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(floorQpc);
        ArgumentOutOfRangeException.ThrowIfNegative(slotsPerWindow);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(slotsPerWindow, MaximumSlotsPerWindow);
        lock (sync)
        {
            ResetContinuityCore();
            // As native: the floor never moves below an event already observed.
            this.floorQpc = Math.Max(floorQpc, Math.Max(0, latestEventQpc));
            this.slotsPerWindow = slotsPerWindow;
            ringSlots = slotsPerWindow == 0 ? 0 : checked(slotsPerWindow * 2 + 2);
        }
    }

    internal int SlotsPerWindow
    {
        get { lock (sync) return slotsPerWindow; }
    }

    // Per-event replays read this instead of the full diagnostics.
    internal long CapacityResetCount
    {
        get { lock (sync) return capacityResetCount; }
    }

    // A published/paused window has no reader of these totals. Keep the run's
    // process/thread identities; window storage is 0 between windows.
    public void ReleaseWindowStorage()
    {
        lock (sync) ResetContinuityCore();
    }

    public void Clear()
    {
        lock (sync)
        {
            inventoryVersion = checked(inventoryVersion + 1);
            currentProcesses.Clear(); currentProcesses.TrimExcess();
            currentThreads.Clear(); currentThreads.TrimExcess();
            ResetContinuityCore();
            // A cleared owner is a fresh one: finite window, floor 0.
            latestEventQpc = -1;
            floorQpc = 0;
            slotsPerWindow = 0;
            ringSlots = 0;
            nextProcessInstanceId = 0;
            nextThreadInstanceId = 0;
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
            foreach (var processor in processors)
            {
                if (!processor.Known) continue;
                if (processor.Run.Process is not null)
                {
                    retainedProcesses.Add(processor.Run.Process.Identity.InstanceId);
                }

                if (processor.Run.Thread is not null)
                {
                    retainedThreads.Add(processor.Run.Thread.InstanceId);
                }
            }

            foreach (var pair in pairs.Values)
            {
                retainedProcesses.Add(pair.Process.Identity.InstanceId);
                retainedThreads.Add(pair.Thread.InstanceId);
            }

            return new CpuResidencyAggregationDiagnostics(
                qpcFrequency,
                windowQpc,
                maximumActivePairs,
                pairs.Count,
                slotsPerWindow,
                ringSlots,
                PairRecordBytes,
                WindowStorageBytes(peakPairCount, ringSlots),
                WindowStorageBytes(maximumActivePairs, ringSlots),
                currentProcesses.Count,
                currentThreads.Count,
                retainedProcesses.Count,
                retainedThreads.Count,
                continuityEpoch,
                capacityResetCount)
            { LastCapacityReset = lastCapacityReset };
        }
    }

    /// <summary>Chunk bytes the native window allocator holds for <paramref name="records"/> pair records
    /// (records never move; freed records are reused, so chunks follow the peak since the last clear).
    /// The native chunk index lists are not modelled.</summary>
    internal static long WindowStorageBytes(long records, int ringSlots)
    {
        if (records <= 0) return 0;
        var bytes = checked(DivideRoundUp(records, RecordsPerChunk) * RecordsPerChunk * PairRecordBytes);
        if (ringSlots == 0) return bytes;
        var ringBytes = checked((long)ringSlots * RingCellBytes);
        var ringsPerChunk = Math.Max(1, RingChunkBytes / ringBytes);
        return checked(bytes + DivideRoundUp(records, ringsPerChunk) * ringsPerChunk * ringBytes);
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

    private void StartMeasuringCore(long eventQpc, DateTimeOffset eventTime)
    {
        measuring = true;
        startQpc = eventQpc;
        startTime = eventTime;
        endQpc = eventQpc > long.MaxValue - windowQpc ? long.MaxValue : eventQpc + windowQpc;
        // Every boundary is at most S here, so F = max(0, slot(m) - n) = 0.
        liveFloorSlot = 0;
        lastCrossingSlot = slotsPerWindow == 0 ? 0 : SlotOf(eventQpc);
    }

    // Finite: the run's overlap with [S, E) and one switch if S <= start < E.
    // Continuous: its overlap with each slot at or above the cached live floor F, and one switch
    // in slot(start) if start >= S. False = capacity reset (LastCapacityReset already captured).
    private bool CloseRunCore(in RunningState previous, int processorIndex, long eventQpc)
    {
        var runStart = previous.StartQpc;
        var from = Math.Max(runStart, startQpc);
        if (slotsPerWindow == 0)
        {
            var duration = Math.Max(0, Math.Min(eventQpc, endQpc) - from);
            var switches = runStart >= startQpc && runStart < endQpc ? 1 : 0;
            if (duration == 0 && switches == 0) return true;
            var totals = FindOrAddPairCore(in previous, processorIndex, eventQpc);
            if (totals is null) return false;
            totals.Duration = checked(totals.Duration + duration);
            totals.Switches = checked(totals.Switches + switches);
            return true;
        }

        var firstSlot = eventQpc > from ? SlotOf(from) : long.MaxValue;
        var lastSlot = eventQpc > from ? SlotOf(eventQpc - 1) : -1;
        var switchSlot = runStart >= startQpc ? SlotOf(runStart) : -1;
        var highest = HighestWrite(firstSlot, lastSlot, switchSlot);
        if (highest < 0) return true;
        if (highest >= liveFloorSlot + ringSlots)
        {
            AdvanceLiveFloorCore(processorIndex, eventQpc);
            highest = HighestWrite(firstSlot, lastSlot, switchSlot);
            if (highest < 0) return true;
            // One processor lags the others by more than about W.
            if (highest >= liveFloorSlot + ringSlots)
                return RejectCapacityCore("ring-span", eventQpc, highest - liveFloorSlot + 1);
        }

        var pair = FindOrAddPairCore(in previous, processorIndex, eventQpc);
        if (pair is null) return false;
        var ring = pair.Ring!;
        for (var slot = Math.Max(firstSlot, liveFloorSlot); slot <= lastSlot; slot++)
        {
            var slotStart = (long)BoundaryOf(slot);
            var nextBoundary = BoundaryOf(slot + 1);
            var slotEnd = nextBoundary < eventQpc ? (long)nextBoundary : eventQpc;
            var overlap = slotEnd - Math.Max(from, slotStart);
            if (overlap <= 0) continue;
            ref var cell = ref RingCellCore(ring, slot);
            cell.Duration = checked(cell.Duration + overlap);
        }

        if (switchSlot >= liveFloorSlot)
        {
            ref var cell = ref RingCellCore(ring, switchSlot);
            cell.Switches = checked(cell.Switches + 1);
        }

        pair.LastWrittenSlot = Math.Max(pair.LastWrittenSlot, highest);
        return true;
    }

    // The highest slot a close writes after clipping below the cached F; -1 = nothing to write.
    private long HighestWrite(long firstSlot, long lastSlot, long switchSlot)
    {
        var highest = -1L;
        if (lastSlot >= Math.Max(firstSlot, liveFloorSlot)) highest = lastSlot;
        if (switchSlot >= liveFloorSlot) highest = Math.Max(highest, switchSlot);
        return highest;
    }

    private ref RingCell RingCellCore(RingCell[] ring, long slot)
    {
        ref var cell = ref ring[(int)(slot % ringSlots)];
        // Any other stamp in this cell is a slot below F (R = 2n + 2 > live span).
        if (cell.Stamp != slot + 1) cell = new RingCell { Stamp = slot + 1 };
        return ref cell;
    }

    private PairState? FindOrAddPairCore(in RunningState previous, int processorIndex, long eventQpc)
    {
        var logicalProcessorId = logicalProcessorIds[processorIndex];
        var key = new AggregateKey(previous.Thread!.InstanceId, logicalProcessorId);
        if (pairs.TryGetValue(key, out var pair)) return pair;
        if (pairs.Count >= maximumActivePairs)
        {
            RejectCapacityCore("active-pair-limit", eventQpc, 0);
            return null;
        }

        pair = new PairState(previous.Process!, previous.Thread!, logicalProcessorId,
            ringSlots == 0 ? null : new RingCell[ringSlots]);
        pairs.Add(key, pair);
        peakPairCount = Math.Max(peakPairCount, pairs.Count);
        return pair;
    }

    private bool RejectCapacityCore(string reason, long eventQpc, long requiredSlotSpan)
    {
        // Keep one bounded failure snapshot before ResetContinuityCore clears
        // the pairs. Post-reset counts cannot identify which limit rejected the run.
        lastCapacityReset = new(reason, eventQpc,
            measuring ? startQpc : (long?)null,
            pairs.Count, maximumActivePairs, requiredSlotSpan, ringSlots,
            currentProcesses.Count, currentThreads.Count);
        return false;
    }

    // F = max(0, slot(m) - n), m = the lowest boundary with this processor's prospective boundary.
    // When F advances, pairs whose last written slot is below it are dropped.
    private void AdvanceLiveFloorCore(int processorIndex, long prospectiveBoundaryQpc)
    {
        var lowest = prospectiveBoundaryQpc;
        for (var index = 0; index < processors.Length; index++)
            if (index != processorIndex && processors[index].BoundaryQpc < lowest) lowest = processors[index].BoundaryQpc;
        var floor = Math.Max(0, SlotOf(lowest) - slotsPerWindow);
        if (floor <= liveFloorSlot) return;
        liveFloorSlot = floor;
        // Dictionary removal during value enumeration is supported.
        foreach (var pair in pairs)
            if (pair.Value.LastWrittenSlot < floor) pairs.Remove(pair.Key);
    }

    // b(k) = S + floor(k * W / n).
    private Int128 BoundaryOf(long slot)
        => startQpc + (Int128)slot * windowQpc / slotsPerWindow;

    // slot(x) = floor(((x - S + 1) * n - 1) / W), the largest k with b(k) <= x; -1 before S.
    private long SlotOf(long qpc)
        => qpc < startQpc ? -1 : checked((long)((((Int128)qpc - startQpc + 1) * slotsPerWindow - 1) / windowQpc));

    private void ResetContinuityCore()
    {
        Array.Clear(processors);
        knownProcessorCount = 0;
        ClearPairsCore();
        measuring = false;
        startQpc = endQpc = liveFloorSlot = lastCrossingSlot = 0;
        startTime = default;
        continuityEpoch = checked(continuityEpoch + 1);
    }

    // Clearing every pair frees every window chunk.
    private void ClearPairsCore()
    {
        pairs.Clear();
        pairs.TrimExcess();
        peakPairCount = 0;
    }

    private CpuExecutionTimeAggregate Row(PairState pair, long duration, int switches)
    {
        var identity = pair.Process.Identity;
        return new(identity.InstanceId, identity.ProcessId, identity.ProcessStartKey, identity.ProcessName,
            pair.Thread.InstanceId, pair.Thread.ThreadId, pair.LogicalProcessorId, duration, switches);
    }

    private CpuResidencyAggregationSnapshot CompleteSnapshotCore(long windowStartQpc, long throughQpc,
        List<CpuExecutionTimeAggregate> rows)
    {
        rows.Sort(static (left, right) =>
        {
            var order = left.ProcessInstanceId.CompareTo(right.ProcessInstanceId);
            if (order == 0) order = left.ThreadInstanceId.CompareTo(right.ThreadInstanceId);
            return order != 0 ? order : left.LogicalProcessorId.CompareTo(right.LogicalProcessorId);
        });
        // Civil through = time(S) + floor((through - S) * 10^7 / frequency) ticks.
        var through = startTime.AddTicks(checked((long)((Int128)(throughQpc - startQpc) * TimeSpan.TicksPerSecond / qpcFrequency)));
        return new(qpcFrequency, windowStartQpc, throughQpc, through, true, rows.ToArray()) { HasBoundaries = true };
    }

    private CpuResidencyAggregationSnapshot IncompleteSnapshotCore(ProcessorState lowest)
        => new(qpcFrequency, startQpc, lowest.BoundaryQpc, lowest.BoundaryTime, false, []) { HasBoundaries = true };

    private CpuResidencyAggregationSnapshot IncompleteSnapshotCore()
        => new(qpcFrequency, 0, 0, null, false, []);

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

    // One expected processor: its current run and its boundary (QPC and civil time of its last switch).
    private struct ProcessorState
    {
        internal bool Known;
        internal RunningState Run;
        internal long BoundaryQpc;
        internal DateTimeOffset BoundaryTime;
    }

    // Thread instance IDs are global within this buffer and already identify the owning process instance.
    private readonly record struct AggregateKey(long ThreadInstanceId, int LogicalProcessorId);

    // Native ring cell {duration i64, switches i32, stamp u32}; stamp = slot + 1, 0 = empty.
    private struct RingCell
    {
        internal long Duration, Stamp;
        internal int Switches;
    }

    // Finite windows use Duration/Switches; continuous windows use Ring and LastWrittenSlot.
    private sealed class PairState(ProcessState process, ThreadState thread, int logicalProcessorId, RingCell[]? ring)
    {
        internal ProcessState Process { get; } = process;
        internal ThreadState Thread { get; } = thread;
        internal int LogicalProcessorId { get; } = logicalProcessorId;
        internal RingCell[]? Ring { get; } = ring;
        internal long Duration, LastWrittenSlot = -1;
        internal int Switches;
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

    private sealed record ThreadState(long InstanceId, int ThreadId, ProcessState Process);
}
