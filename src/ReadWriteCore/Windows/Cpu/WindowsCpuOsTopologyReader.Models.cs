namespace ResourceManager.ReadWrite.Windows.Cpu;

public sealed partial class WindowsCpuOsTopologyReader
{
    private sealed record LogicalProcessorInfoRecord(
        int Relationship,
        IntPtr Pointer,
        int Size);

    internal sealed record LogicalProcessorRelationshipRecord(
        int EfficiencyClass,
        IReadOnlyList<int> LogicalProcessorIds)
    {
        internal bool SmtRelationshipReported { get; init; }
    }

    internal sealed record CacheRelationshipRecord(
        int Level,
        int CacheSizeBytes,
        IReadOnlyList<int> LogicalProcessorIds)
    {
        internal ushort LineSizeBytes { get; init; }
        internal byte Associativity { get; init; }
        internal int CacheType { get; init; }
    }

    public sealed record CpuSetRecord(
        uint Id,
        int Group,
        int LogicalProcessorIndex,
        int CoreIndex,
        int LastLevelCacheIndex,
        int NumaNodeIndex,
        int EfficiencyClass,
        int SchedulingClass)
    {
        public int GlobalLogicalProcessorId => Group * 64 + LogicalProcessorIndex;
    }

    public sealed record ProcessorTimes(
        long IdleTime,
        long KernelTime,
        long UserTime);
}
