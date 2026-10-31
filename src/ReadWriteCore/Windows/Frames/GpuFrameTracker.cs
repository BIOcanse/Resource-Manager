namespace ResourceManager.ReadWrite.Windows.Frames;

/// <summary>Bounds supplied by the host's frame_pipeline freedom point. Packet and unfinished frame
/// bounds are global, including other processes needed to preserve engine queue ordering.</summary>
internal sealed record GpuFrameTrackerLimits(int Objects, int Packets, int Frames);

/// <summary>
/// DxgKrnl software-scheduled GPU trace. Device/Context rundown owns process attribution, never the
/// DMA interrupt's header PID. DMA_Start enqueues on an adapter/node; only its front executes. A
/// process's busy time is the union of its executing nodes. The present QueuePacket_Stop closes that
/// process's accumulation and writes it to the original Present record, following PresentMon's GPU
/// boundary. Work crossing a boundary is split there. HAGS queues are explicitly unavailable until
/// their proxy execution shape is implemented. Caller serializes all entry points with the ledger.
/// </summary>
internal sealed class GpuFrameTracker(
    Func<int, long?> readBirth, GpuFrameTrackerLimits limits, Func<int, bool> requested) : IGpuFrameBoundary
{
    private readonly Dictionary<ulong, Device> devices = [];
    private readonly Dictionary<ulong, Context> contexts = [];
    private readonly Dictionary<(ulong Adapter, uint Node), Queue<Packet>> nodes = [];
    private readonly Dictionary<(ulong Context, uint Sequence), GpuFrameOwner> boundaries = [];
    private readonly Dictionary<(int Pid, long Birth), Accumulator> processes = [];
    private readonly HashSet<(int Pid, long Birth)> hardwareScheduled = [];
    private int packetCount;
    private string? failure;

    internal string? Failure => failure;
    internal int ObjectCount => devices.Count + contexts.Count + nodes.Count + processes.Count;
    internal int PacketCount => packetCount;
    internal int PendingFrameCount => boundaries.Count;

    public string UnavailableReason(int processId, long processStartKey)
        => !requested(processId) ? FrameTimingReasons.GpuTierNotRequested
            : failure ?? (hardwareScheduled.Contains((processId, processStartKey))
                ? FrameTimingReasons.GpuHardwareScheduledQueue : FrameTimingReasons.GpuWorkNotObserved);

    internal void Refuse(string reason)
    {
        failure = reason;
        devices.Clear(); contexts.Clear(); nodes.Clear(); boundaries.Clear(); processes.Clear();
        hardwareScheduled.Clear(); packetCount = 0;
    }

    private bool Room() => failure is null && (ObjectCount < limits.Objects || CapacityFailure());
    private bool CapacityFailure() { Refuse(FrameTimingReasons.GpuTrackerCapacity); return false; }

    internal void DeviceStarted(ulong handle, ulong adapter, int processId)
    {
        if (handle == 0 || adapter == 0 || failure is not null) return;
        var birth = processId > 0 ? readBirth(processId) : null;
        if (!devices.ContainsKey(handle) && !Room()) return;
        devices[handle] = new(adapter, processId, birth ?? 0);
    }

    internal void DeviceStopped(ulong handle)
    {
        foreach (var context in contexts.Where(pair => pair.Value.Device == handle).Select(static pair => pair.Key).ToArray())
            ContextStopped(context);
        devices.Remove(handle);
    }

    internal void ProcessStopped(int processId, long ticks)
    {
        // Release the dead instance without allowing a later reused PID to inherit either mappings or work.
        foreach (var key in boundaries.Where(pair => pair.Value.Stream.ProcessId == processId).Select(static pair => pair.Key).ToArray())
            boundaries.Remove(key);
        var deadContexts = contexts.Where(pair => pair.Value.Pid == processId).Select(static pair => pair.Key).ToHashSet();
        foreach (var (node, queue) in nodes.ToArray())
        {
            var frontRemoved = queue.TryPeek(out var front) && deadContexts.Contains(front.Context);
            if (frontRemoved) front!.Trace?.Stop(ticks);
            var kept = queue.Where(packet => !deadContexts.Contains(packet.Context)).ToArray();
            packetCount -= queue.Count - kept.Length;
            if (kept.Length == 0) nodes.Remove(node);
            else
            {
                nodes[node] = new(kept);
                if (frontRemoved) kept[0].Trace?.Start(ticks);
            }
        }
        foreach (var key in deadContexts) contexts.Remove(key);
        foreach (var key in processes.Keys.Where(key => key.Pid == processId).ToArray()) processes.Remove(key);
        hardwareScheduled.RemoveWhere(key => key.Pid == processId);
        foreach (var key in devices.Where(pair => pair.Value.Pid == processId).Select(static pair => pair.Key).ToArray()) devices.Remove(key);
    }

    internal void ContextStarted(ulong handle, ulong device, uint node, int processId)
    {
        if (failure is not null || handle == 0 || !devices.TryGetValue(device, out var owner)) return;
        var pid = processId > 0 ? processId : owner.Pid;
        var birth = pid == owner.Pid ? owner.Birth : readBirth(pid) ?? 0;
        if (!contexts.ContainsKey(handle) && !Room()) return;
        contexts[handle] = new(device, owner.Adapter, node, pid, birth);
    }

    internal void ContextStopped(ulong handle)
    {
        // A destroyed context with outstanding work cannot yield trustworthy durations.
        if (nodes.Values.Any(queue => queue.Any(packet => packet.Context == handle)))
        { Refuse(FrameTimingReasons.GpuPacketSequenceIncomplete); return; }
        contexts.Remove(handle);
        foreach (var key in boundaries.Keys.Where(key => key.Context == handle).ToArray()) boundaries.Remove(key);
        foreach (var key in nodes.Where(static pair => pair.Value.Count == 0).Select(static pair => pair.Key).ToArray()) nodes.Remove(key);
    }

    internal void HardwareQueue(ulong context)
    {
        if (contexts.TryGetValue(context, out var owner))
        {
            if (hardwareScheduled.Count >= limits.Objects) { CapacityFailure(); return; }
            hardwareScheduled.Add((owner.Pid, owner.Birth));
        }
        else Refuse(FrameTimingReasons.GpuHardwareScheduledQueue);
    }

    internal void QueueStarted(ulong context, uint sequence, int processId, GpuFrameOwner? present)
    {
        if (failure is not null) return;
        if (!contexts.TryGetValue(context, out var owner)) return;
        // DCStart may run on the tracing process. Bind an owner missing from rundown using the
        // original queue submission's PID (which is trustworthy), never the DMA header.
        if (owner.Birth == 0 && processId > 0 && readBirth(processId) is > 0 and var birth)
            contexts[context] = owner = owner with { Pid = processId, Birth = birth };
        if (present is not { } frame || !requested(owner.Pid) || owner.Birth != frame.Stream.ProcessStartKey
            || owner.Pid != frame.Stream.ProcessId) return;
        if (boundaries.Count >= limits.Frames) { CapacityFailure(); return; }
        if (!boundaries.TryAdd((context, sequence), frame)) Refuse(FrameTimingReasons.GpuPacketSequenceIncomplete);
    }

    internal void QueueStopped(ulong context, uint sequence, long ticks)
    {
        if (failure is not null || !boundaries.Remove((context, sequence), out var frame)) return;
        if (hardwareScheduled.Contains((frame.Stream.ProcessId, frame.Stream.ProcessStartKey))) return;
        if (processes.TryGetValue((frame.Stream.ProcessId, frame.Stream.ProcessStartKey), out var trace)
            && trace.Take(ticks) is { } record) frame.Complete(record);
    }

    internal void DmaStarted(ulong context, uint sequence, long ticks)
    {
        if (failure is not null || sequence == 0 || !contexts.TryGetValue(context, out var owner)) return;
        if (hardwareScheduled.Contains((owner.Pid, owner.Birth))) return;
        var node = (owner.Adapter, owner.Node);
        if (!nodes.TryGetValue(node, out var queue))
        {
            if (!Room()) return;
            nodes[node] = queue = new();
        }
        if (packetCount >= limits.Packets) { CapacityFailure(); return; }
        if (queue.Any(packet => packet.Context == context && packet.Sequence == sequence))
        { Refuse(FrameTimingReasons.GpuPacketSequenceIncomplete); return; }
        Accumulator? trace = null;
        if (owner.Birth > 0 && requested(owner.Pid))
        {
            if (!processes.TryGetValue((owner.Pid, owner.Birth), out trace))
            {
                if (!Room()) return;
                processes[(owner.Pid, owner.Birth)] = trace = new();
            }
        }
        queue.Enqueue(new(context, sequence, trace)); packetCount++;
        if (queue.Count == 1) trace?.Start(ticks);
    }

    internal void DmaCompleted(ulong context, uint sequence, long ticks)
    {
        if (failure is not null || sequence == 0 || !contexts.TryGetValue(context, out var owner)
            || !nodes.TryGetValue((owner.Adapter, owner.Node), out var queue) || queue.Count == 0) return;
        var front = queue.Peek();
        if (front.Context != context || front.Sequence != sequence)
        { Refuse(FrameTimingReasons.GpuPacketSequenceIncomplete); return; }
        queue.Dequeue(); packetCount--;
        if (front.Trace is { } trace && !trace.Stop(ticks))
        { Refuse(FrameTimingReasons.GpuPacketSequenceIncomplete); return; }
        if (queue.TryPeek(out var next)) next.Trace?.Start(ticks);
    }

    private sealed record Device(ulong Adapter, int Pid, long Birth);
    private sealed record Context(ulong Device, ulong Adapter, uint Node, int Pid, long Birth);
    private sealed record Packet(ulong Context, uint Sequence, Accumulator? Trace);

    private sealed class Accumulator
    {
        private int running;
        private long since, first, ready, busy;
        internal void Start(long ticks)
        {
            if (running++ == 0) since = ticks;
            if (first == 0) first = ticks;
        }
        internal bool Stop(long ticks)
        {
            if (running == 0 || ticks < since) return false;
            if (--running == 0) busy += ticks - since;
            ready = ticks;
            return true;
        }
        internal PresentGpuRecord? Take(long ticks)
        {
            if (first == 0 || ticks < since) return null;
            var record = new PresentGpuRecord(first, running > 0 ? ticks : ready,
                busy + (running > 0 ? ticks - since : 0), null);
            first = running > 0 ? ticks : 0; since = running > 0 ? ticks : 0;
            ready = 0; busy = 0;
            return record;
        }
    }
}
