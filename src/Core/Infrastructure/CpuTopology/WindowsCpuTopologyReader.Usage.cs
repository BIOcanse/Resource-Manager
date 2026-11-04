namespace ResourceManager.App.Infrastructure.CpuTopology;

public sealed partial class WindowsCpuTopologyReader
{
    public void ResetUsageBaseline() => osReader.ResetUsageBaseline();

    internal IReadOnlyList<double?> ReadLogicalProcessorUsage(int logicalProcessorCount, ICollection<string> notes)
        => osReader.ReadLogicalProcessorUsage(logicalProcessorCount, notes);

    private static double? TryGetUsage(IReadOnlyList<double?> usageValues, int logicalProcessorId)
        => logicalProcessorId >= 0 && logicalProcessorId < usageValues.Count ? usageValues[logicalProcessorId] : null;
}
