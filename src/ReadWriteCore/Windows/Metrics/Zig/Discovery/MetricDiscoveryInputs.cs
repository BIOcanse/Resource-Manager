using ResourceManager.ReadWrite.Native.MetricOwner;

namespace ResourceManager.ReadWrite.Windows.Metrics.Zig.Discovery;

/// <summary>One native-selected inventory acquisition, entirely in its held operation workspace.</summary>
public sealed unsafe class MetricDiscoveryInventoryInputView
{
    private readonly MetricDiscoveryRoundFacts owner;
    private MetricDiscoveryInventoryInput input;
    private uint used;
    internal MetricDiscoveryInventoryInputView(MetricDiscoveryRoundFacts owner, MetricDiscoveryInventoryInput input)
    { this.owner = owner; this.input = input; }
    public ulong SourceId { get { Open(); return input.SourceId; } }
    public ulong Observation { get { Open(); return input.Observation; } }
    public ulong Incarnation { get { Open(); return input.Incarnation; } }
    public ulong Capability { get { Open(); return input.Capability; } }
    public ulong CatalogGeneration { get { Open(); return input.CatalogGeneration; } }
    public ulong QueryToken { get { Open(); return input.QueryToken; } }
    public uint Capacity { get { Open(); return input.RowCapacity; } }
    public uint TextCapacity { get { Open(); return input.TextCapacity; } }
    public void SetRow(int row, ulong rawLuid, ReadOnlySpan<char> exactRawKey)
    {
        Row(row); input.Luids[row] = rawLuid;
        input.Keys[row] = MetricDiscoveryBorrowed.WriteUtf8(exactRawKey, new Span<byte>(input.Text.Bytes, checked((int)input.TextCapacity)), ref used);
    }
    public void SetRowUtf8(int row, ulong rawLuid, ReadOnlySpan<byte> exactRawKey)
    {
        Row(row);
        if ((ulong)used + (uint)exactRawKey.Length > input.TextCapacity)
            throw new MetricOwnerException(new() { Code = MetricOwnerCode.PayloadCapacity, Family = MetricOwnerFamily.Payload,
                Required = (ulong)used + (uint)exactRawKey.Length, Limit = input.TextCapacity });
        exactRawKey.CopyTo(new Span<byte>(input.Text.Bytes + used, exactRawKey.Length));
        input.Luids[row] = rawLuid; input.Keys[row] = new() { Offset = used, Length = checked((uint)exactRawKey.Length) };
        used = checked(used + (uint)exactRawKey.Length);
    }
    public void Complete(ulong rawDirectoryGeneration, ulong rawTopologyFingerprint, long observedMs,
        uint count, uint observedCount, uint skippedCount, uint overflowCount, uint rawStatus, uint reason = 0)
    {
        Open();
        if (count > input.RowCapacity) throw new MetricOwnerException(new() { Code = MetricOwnerCode.CapacityExceeded,
            Family = MetricOwnerFamily.Binding, Required = count, Limit = input.RowCapacity });
        ref var header = ref *input.Header;
        header.Generation = rawDirectoryGeneration; header.TopologyFingerprint = rawTopologyFingerprint; header.ObservedMs = observedMs;
        header.Count = count; header.ObservedCount = observedCount; header.SkippedCount = skippedCount; header.OverflowCount = overflowCount;
        header.Status = rawStatus; header.Reason = reason; header.TextLength = used;
    }
    internal void Close() { input = default; used = 0; }
    private void Open() => owner.Sampling();
    private void Row(int row)
    {
        Open();
        if ((uint)row >= input.RowCapacity) throw new MetricOwnerException(new() { Code = MetricOwnerCode.CapacityExceeded,
            Family = MetricOwnerFamily.Binding, Required = checked((ulong)Math.Max(row, 0) + 1), Limit = input.RowCapacity });
    }
}
