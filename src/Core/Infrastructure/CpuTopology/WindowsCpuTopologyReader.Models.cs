namespace ResourceManager.App.Infrastructure.CpuTopology;

public sealed partial class WindowsCpuTopologyReader
{
    private sealed record LogicalProcessorInfoRecord(
        int Relationship,
        IntPtr Pointer,
        int Size);

    private sealed record LogicalProcessorRelationshipRecord(
        int EfficiencyClass,
        IReadOnlyList<int> LogicalProcessorIds);

    private sealed record CacheRelationshipRecord(
        int Level,
        int CacheSizeBytes,
        IReadOnlyList<int> LogicalProcessorIds);

    private sealed record PhysicalCoreRecord(
        int Index,
        int EfficiencyClass,
        IReadOnlyList<LogicalProcessorRecord> LogicalProcessors)
    {
        public static PhysicalCoreRecord From(
            LogicalProcessorRelationshipRecord record,
            int index)
        {
            return new PhysicalCoreRecord(
                index,
                record.EfficiencyClass,
                record.LogicalProcessorIds
                    .Distinct()
                    .Order()
                    .Select(static logicalId => new LogicalProcessorRecord(
                        logicalId,
                        logicalId / 64,
                        logicalId % 64))
                    .ToArray());
        }
    }

    private sealed record LogicalProcessorRecord(
        int GlobalId,
        int Group,
        int GroupRelativeIndex);

    private sealed record CpuSetRecord(
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

    private sealed record CcdRecord(
        string Id,
        int Index,
        IReadOnlyList<int> LogicalProcessorIds,
        string Source);

    private sealed record ProcessorTimes(
        long IdleTime,
        long KernelTime,
        long UserTime);
}
