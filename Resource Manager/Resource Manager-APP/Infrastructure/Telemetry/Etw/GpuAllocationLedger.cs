namespace ResourceManager.App.Infrastructure.Telemetry.Etw;

public sealed record GpuAllocationAmount(double PrivateBytes, double SharedBytes)
{
    public double TotalBytes => PrivateBytes + SharedBytes;
}

public sealed record GpuAllocationReading(
    IReadOnlyDictionary<ulong, IReadOnlyDictionary<int, GpuAllocationAmount>> Adapters)
{
    public IReadOnlyDictionary<ulong, double> KernelMetadataBytes { get; init; } = new Dictionary<ulong, double>();
    public ulong Generation { get; init; }
    public long ObservedAtUtcTicks { get; init; }
}

// All mutation is serialized by the collector. Handles identify a live allocation, not a permanent object.
public sealed class GpuAllocationLedger(int maximumAllocations = 262144, int maximumReferences = 1048576)
{
    private sealed class Allocation
    {
        public ulong? Bytes;
        public bool KernelMetadata;
        public readonly Dictionary<ulong, (int Pid, long ObservedFileTime)> References = [];
        public readonly Dictionary<(uint Segment, ulong Placement), List<(ulong Start, ulong End)>> Resident = [];
        public readonly HashSet<(uint Segment, ulong Placement)> TransferredSources = [];
    }
    private readonly Dictionary<(ulong Adapter, ulong Global), Allocation> allocations = [];
    private readonly Dictionary<ulong, (ulong Adapter, ulong Global)> referenceKeys = [];
    private readonly Dictionary<ulong, ulong> adapterLuids = [];
    private readonly Dictionary<ulong, (ulong Adapter, ulong Global)> globalKeys = [];
    private readonly Dictionary<(ulong Adapter, uint Segment), bool> dedicatedSegments = [];
    private readonly Dictionary<ulong, (int Pid, uint Segment, ulong Bytes, ulong Placement)> pendingReferences = [];
    private readonly Dictionary<ulong, HashSet<(uint Segment, ulong Placement)>> pendingGlobals = [];

    public void SetSegment(ulong adapter, uint segment, uint flags)
    {
        // Aperture, AGP and the implicit system-memory segment are not dedicated VRAM.
        dedicatedSegments[(adapter, segment)] = segment != 0 && (flags & 0x1003u) == 0;
        if ((flags & 0x20u) != 0)
            throw new InvalidDataException("Pitch-aligned GPU segments require an explicit resident charge.");
    }

    public void SetAdapter(ulong adapter, ulong luid) => adapterLuids[adapter] = luid;

    public void SetAllocation(ulong adapter, ulong global, ulong bytes, bool kernelMetadata = false)
    {
        var allocation = GetAllocation((adapter, global));
        allocation.Bytes = bytes;
        allocation.KernelMetadata = kernelMetadata;
        if (pendingGlobals.Remove(global, out var placements))
            foreach (var placement in placements) SetCommittedGlobal(global, placement.Segment, placement.Placement);
    }

    public void SetCommittedGlobal(ulong global, uint segment, ulong placement = 0)
    {
        if (!globalKeys.TryGetValue(global, out var key) || allocations[key].Bytes is not ulong bytes)
        {
            if (pendingGlobals.Count >= maximumAllocations) throw new InvalidDataException("GPU pending allocation limit exceeded.");
            if (!pendingGlobals.TryGetValue(global, out var placements)) pendingGlobals.Add(global, placements = []);
            if (placements.Count >= 4096) throw new InvalidDataException("GPU pending placement limit exceeded.");
            placements.Add((segment, placement));
            return;
        }
        AddResidentRange(key.Adapter, global, segment, 0, bytes, placement);
    }

    public void SetCommittedReference(ulong reference, int pid, uint segment, ulong bytes, ulong placement = 0)
    {
        if (!referenceKeys.TryGetValue(reference, out var key))
        {
            if (pendingReferences.Count >= maximumReferences) throw new InvalidDataException("GPU pending reference limit exceeded.");
            pendingReferences[reference] = (pid, segment, bytes, placement);
            return;
        }
        AddResidentRange(key.Adapter, key.Global, segment, 0, bytes, placement);
    }

    public void AddResidentRange(ulong adapter, ulong global, uint segment, ulong offset, ulong bytes, ulong placement = 0)
    {
        if (segment == 0 || bytes == 0) return;
        var allocation = GetAllocation((adapter, global));
        var end = checked(offset + bytes);
        var location = (segment, placement);
        if (!allocation.Resident.TryGetValue(location, out var ranges))
        {
            if (allocation.Resident.Count >= 4096) throw new InvalidDataException("GPU physical placement limit exceeded.");
            allocation.Resident.Add(location, ranges = []);
        }
        var start = offset;
        for (var i = ranges.Count - 1; i >= 0; i--)
        {
            var range = ranges[i];
            if (range.End < start || range.Start > end) continue;
            start = Math.Min(start, range.Start);
            end = Math.Max(end, range.End);
            ranges.RemoveAt(i);
        }
        if (ranges.Count >= 4096) throw new InvalidDataException("GPU physical range limit exceeded.");
        ranges.Add((start, end));
    }

    public void DiscardResident(ulong adapter, ulong global, uint segment, ulong? placement = null)
    {
        if (pendingGlobals.TryGetValue(global, out var pending))
        {
            pending.RemoveWhere(item => item.Segment == segment && (!placement.HasValue || item.Placement == placement));
            if (pending.Count == 0) pendingGlobals.Remove(global);
        }
        if (!allocations.TryGetValue((adapter, global), out var allocation)) return;
        foreach (var key in allocation.Resident.Keys.Where(key => key.Segment == segment
            && (!placement.HasValue || key.Placement == placement)).ToArray()) allocation.Resident.Remove(key);
        allocation.TransferredSources.RemoveWhere(key => key.Segment == segment
            && (!placement.HasValue || key.Placement == placement));
    }

    public void RecordTransferredSource(ulong adapter, ulong global, uint segment, ulong placement, ulong bytes)
    {
        if (segment == 0) return;
        var allocation = GetAllocation((adapter, global));
        if (allocation.Bytes != bytes)
            throw new NotSupportedException("Partial GPU transfers require range-specific eviction evidence.");
        allocation.TransferredSources.Add((segment, placement));
    }

    public void CompleteEviction(ulong global)
    {
        if (!globalKeys.TryGetValue(global, out var key)) return;
        var allocation = allocations[key];
        // Eviction alone can leave backing resident. A preceding completed whole
        // transfer identifies the old backing that the eviction actually retires.
        foreach (var source in allocation.TransferredSources) allocation.Resident.Remove(source);
        allocation.TransferredSources.Clear();
    }

    public void Open(ulong adapter, ulong global, ulong reference, int pid, long observedFileTime)
    {
        var key = (adapter, global);
        if (referenceKeys.TryGetValue(reference, out var prior) && prior != key) Close(reference);
        if (!referenceKeys.ContainsKey(reference) && referenceKeys.Count >= maximumReferences)
            throw new InvalidDataException("GPU allocation reference limit exceeded.");
        referenceKeys[reference] = key;
        GetAllocation(key).References[reference] = (pid, observedFileTime);
        if (pendingReferences.Remove(reference, out var pending))
        {
            if (pending.Pid != pid) throw new InvalidDataException("GPU committed reference owner changed during rundown.");
            AddResidentRange(adapter, global, pending.Segment, 0, pending.Bytes, pending.Placement);
        }
    }

    public void Close(ulong reference)
    {
        pendingReferences.Remove(reference);
        if (referenceKeys.Remove(reference, out var key) && allocations.TryGetValue(key, out var allocation))
            allocation.References.Remove(reference);
    }

    public void Delete(ulong adapter, ulong global)
    {
        pendingGlobals.Remove(global);
        globalKeys.Remove(global);
        if (allocations.Remove((adapter, global), out var allocation))
            foreach (var reference in allocation.References.Keys) referenceKeys.Remove(reference);
    }

    public GpuAllocationReading Read(IReadOnlyDictionary<int, long> currentProcesses)
    {
        var output = new Dictionary<ulong, Dictionary<int, GpuAllocationAmount>>();
        var kernel = new Dictionary<ulong, double>();
        if (pendingGlobals.Count != 0 || pendingReferences.Values.Any(static value => value.Pid != 4))
            throw new InvalidDataException("GPU resident allocation ownership is incomplete.");
        foreach (var (key, allocation) in allocations)
        {
            if (!adapterLuids.TryGetValue(key.Adapter, out var luid))
                throw new InvalidDataException("GPU allocation adapter has no LUID mapping.");
            if (allocation.Bytes is null)
                throw new InvalidDataException("GPU reference has no allocation size.");
            double bytes = 0;
            foreach (var (location, ranges) in allocation.Resident)
            {
                if (!dedicatedSegments.TryGetValue((key.Adapter, location.Segment), out var dedicated))
                    throw new InvalidDataException("GPU resident segment has no classification.");
                if (dedicated) foreach (var range in ranges) bytes += range.End - range.Start;
            }
            if (!output.TryGetValue(luid, out var processes)) output.Add(luid, processes = []);
            if (allocation.KernelMetadata)
            {
                kernel[luid] = kernel.GetValueOrDefault(luid) + bytes;
                continue;
            }
            var owners = new Dictionary<int, long>();
            foreach (var reference in allocation.References.Values)
                owners[reference.Pid] = Math.Max(owners.GetValueOrDefault(reference.Pid), reference.ObservedFileTime);
            if (owners.Count == 0) continue;
            var share = (double)bytes / owners.Count;
            foreach (var (pid, observedAt) in owners)
            {
                // Hidden/unreadable users remain in the divisor; an old PID cannot be assigned to a new process.
                if (!currentProcesses.TryGetValue(pid, out var birth) || birth <= 0 || birth > observedAt) continue;
                var prior = processes.GetValueOrDefault(pid) ?? new GpuAllocationAmount(0, 0);
                processes[pid] = owners.Count == 1
                    ? prior with { PrivateBytes = prior.PrivateBytes + share }
                    : prior with { SharedBytes = prior.SharedBytes + share };
            }
        }
        return new GpuAllocationReading(output.ToDictionary(item => item.Key,
            item => (IReadOnlyDictionary<int, GpuAllocationAmount>)item.Value)) { KernelMetadataBytes = kernel };
    }

    private Allocation GetAllocation((ulong Adapter, ulong Global) key)
    {
        if (allocations.TryGetValue(key, out var allocation)) return allocation;
        if (allocations.Count >= maximumAllocations) throw new InvalidDataException("GPU allocation limit exceeded.");
        allocation = new Allocation();
        allocations.Add(key, allocation);
        globalKeys[key.Global] = key;
        return allocation;
    }
}
