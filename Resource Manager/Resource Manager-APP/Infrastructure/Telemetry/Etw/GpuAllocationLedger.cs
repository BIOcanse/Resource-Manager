namespace ResourceManager.App.Infrastructure.Telemetry.Etw;

public sealed record GpuAllocationAmount(double PrivateBytes, double SharedBytes)
{
    public double TotalBytes => PrivateBytes + SharedBytes;
}

public sealed record GpuAllocationReading(
    IReadOnlyDictionary<ulong, IReadOnlyDictionary<int, GpuAllocationAmount>> Adapters)
{
    public IReadOnlyDictionary<ulong, double> KernelMetadataBytes { get; init; } = new Dictionary<ulong, double>();
}

// All mutation is serialized by the collector. Handles identify a live allocation, not a permanent object.
public sealed class GpuAllocationLedger(int maximumAllocations = 262144, int maximumReferences = 1048576)
{
    private sealed class Allocation
    {
        public ulong? Bytes;
        public bool KernelMetadata;
        public readonly Dictionary<ulong, (int Pid, long ObservedFileTime)> References = [];
    }
    private readonly Dictionary<(ulong Adapter, ulong Global), Allocation> allocations = [];
    private readonly Dictionary<ulong, (ulong Adapter, ulong Global)> referenceKeys = [];
    private readonly Dictionary<ulong, ulong> adapterLuids = [];

    public void SetAdapter(ulong adapter, ulong luid) => adapterLuids[adapter] = luid;

    public void SetAllocation(ulong adapter, ulong global, ulong bytes, bool kernelMetadata = false)
    {
        var allocation = GetAllocation((adapter, global));
        allocation.Bytes = bytes;
        allocation.KernelMetadata = kernelMetadata;
    }

    public void Open(ulong adapter, ulong global, ulong reference, int pid, long observedFileTime)
    {
        var key = (adapter, global);
        if (referenceKeys.TryGetValue(reference, out var prior) && prior != key) Close(reference);
        if (!referenceKeys.ContainsKey(reference) && referenceKeys.Count >= maximumReferences)
            throw new InvalidDataException("GPU allocation reference limit exceeded.");
        referenceKeys[reference] = key;
        GetAllocation(key).References[reference] = (pid, observedFileTime);
    }

    public void Close(ulong reference)
    {
        if (referenceKeys.Remove(reference, out var key) && allocations.TryGetValue(key, out var allocation))
            allocation.References.Remove(reference);
    }

    public void Delete(ulong adapter, ulong global)
    {
        if (allocations.Remove((adapter, global), out var allocation))
            foreach (var reference in allocation.References.Keys) referenceKeys.Remove(reference);
    }

    public GpuAllocationReading Read(IReadOnlyDictionary<int, long> currentProcesses)
    {
        var output = new Dictionary<ulong, Dictionary<int, GpuAllocationAmount>>();
        var kernel = new Dictionary<ulong, double>();
        foreach (var (key, allocation) in allocations)
        {
            if (!adapterLuids.TryGetValue(key.Adapter, out var luid))
                throw new InvalidDataException("GPU allocation adapter has no LUID mapping.");
            if (allocation.Bytes is not ulong bytes)
                throw new InvalidDataException("GPU reference has no allocation size.");
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
        return allocation;
    }
}
