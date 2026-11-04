using ResourceManager.ReadWrite.Native.MetricOwner;

namespace ResourceManager.ReadWrite.Windows.Metrics.Zig.Discovery;

/// <summary>Raw PDH vector columns in this operation; no aggregation or classification.</summary>
public sealed unsafe class MetricDiscoveryPdhInputView
{
    private readonly MetricDiscoveryRoundFacts owner;
    private MetricDiscoveryGpuHeader* header;
    private readonly uint capacity;
    internal MetricDiscoveryPdhInputView(MetricDiscoveryRoundFacts owner, MetricDiscoveryGpuHeader* header, uint capacity)
    { this.owner = owner; this.header = header; this.capacity = capacity; }
    public uint Capacity { get { owner.Sampling(); return capacity; } }
    public ulong SourceId { get { owner.Sampling(); return header->Input.SourceId; } }
    public ulong Observation { get { owner.Sampling(); return header->Input.Observation; } }
    public void SetRow(int row, ulong rawLuid, uint rawPhysical, uint rawEngine, uint rawClass, double rawPercent)
    {
        owner.Sampling(); Row(row);
        header->Input.Luids[row] = rawLuid; header->Input.Physical[row] = rawPhysical;
        header->Input.Engines[row] = rawEngine; header->Input.Classes[row] = rawClass; header->Input.Usage[row] = rawPercent;
    }
    public void Complete(uint count, uint rawStatus, long observedMs, uint reason = 0, ulong rawValidMask = 0)
    {
        owner.Sampling(); Count(count);
        header->Input.Count = count; header->Input.Status = rawStatus; header->Input.ObservedMs = observedMs;
        header->Input.Reason = reason; header->Input.ValidMask = rawValidMask;
    }
    internal void Close() => header = null;
    private void Row(int row) { ArgumentOutOfRangeException.ThrowIfNegative(row); Count(checked((uint)row + 1)); }
    private void Count(uint count)
    {
        if (count > capacity) throw new MetricOwnerException(new() { Code = MetricOwnerCode.CapacityExceeded,
            Family = MetricOwnerFamily.Binding, Required = count, Limit = capacity });
    }
}

/// <summary>Original notebook sensor operands; native chooses legacy target rows.</summary>
public sealed unsafe class MetricDiscoveryNotebookInputView
{
    private readonly MetricDiscoveryRoundFacts owner;
    private MetricDiscoverySensorHeader* header;
    private readonly uint capacity;
    internal MetricDiscoveryNotebookInputView(MetricDiscoveryRoundFacts owner, MetricDiscoverySensorHeader* header, uint capacity)
    { this.owner = owner; this.header = header; this.capacity = capacity; }
    public uint Capacity { get { owner.Sampling(); return capacity; } }
    public ulong SourceId { get { owner.Sampling(); return header->Input.SourceId; } }
    public ulong Observation { get { owner.Sampling(); return header->Observation; } }
    public void SetCpu(double rawRpm, double rawPercent, uint rawValid)
    { owner.Sampling(); header->Input.CpuRpm = rawRpm; header->Input.CpuPercent = rawPercent; header->Input.CpuValid = rawValid; }
    public void SetRow(int row, uint rawIndex, double rawRpm, double rawPercent, uint rawValid)
    {
        owner.Sampling(); ArgumentOutOfRangeException.ThrowIfNegative(row); Count(checked((uint)row + 1));
        header->Input.Indices[row] = rawIndex; header->Input.Rpm[row] = rawRpm; header->Input.Percent[row] = rawPercent; header->Input.Valid[row] = rawValid;
    }
    public void Complete(uint count, uint rawStatus, long observedMs, uint reason = 0)
    { owner.Sampling(); Count(count); header->Input.Count = count; header->Input.Status = rawStatus; header->Input.ObservedMs = observedMs; header->Input.Reason = reason; }
    internal void Close() => header = null;
    private void Count(uint count)
    {
        if (count > capacity) throw new MetricOwnerException(new() { Code = MetricOwnerCode.CapacityExceeded,
            Family = MetricOwnerFamily.Binding, Required = count, Limit = capacity });
    }
}
