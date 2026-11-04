using static ResourceManager.ReadWrite.Windows.Cpu.WindowsCpuOsTopologyReader;

namespace ResourceManager.App.Infrastructure.CpuTopology;

public sealed partial class WindowsCpuTopologyReader
{
    private sealed record PhysicalCoreRecord(
        int Index,
        int EfficiencyClass,
        IReadOnlyList<LogicalProcessorRecord> LogicalProcessors)
    {
        internal bool SmtRelationshipReported { get; init; }

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
                    .ToArray()) { SmtRelationshipReported = record.SmtRelationshipReported };
        }
    }

    private sealed record LogicalProcessorRecord(
        int GlobalId,
        int Group,
        int GroupRelativeIndex);

    private sealed record CcdRecord(
        string Id,
        int Index,
        IReadOnlyList<int> LogicalProcessorIds,
        string Source);

}
