using static ResourceManager.ReadWrite.Windows.Cpu.WindowsCpuOsTopologyReader;
using ResourceManager.App.Domain.CpuTopology;
using NativeMethods = ResourceManager.ReadWrite.Windows.Cpu.WindowsCpuTopologyNativeMethods;

namespace ResourceManager.App.Infrastructure.CpuTopology;

public sealed partial class WindowsCpuTopologyReader
{
    private IReadOnlyList<PhysicalCoreRecord> ReadPhysicalCoreRecords(
        ICollection<string> notes, out IReadOnlyList<CpuOsPhysicalCore>? reportedCores)
    {
        reportedCores = null;
        try
        {
            var records = QueryLogicalProcessorRecords(NativeMethods.RelationProcessorCore)
                .Select((record, index) => PhysicalCoreRecord.From(record, index))
                .Where(static record => record.LogicalProcessors.Count > 0)
                .ToArray();
            reportedCores = records.Select(core => new CpuOsPhysicalCore($"core:{core.Index}",
                (byte)core.EfficiencyClass, core.SmtRelationshipReported,
                core.LogicalProcessors.Select(static logical => logical.GlobalId).ToArray())).ToArray();
            if (records.Length > 0)
            {
                return records;
            }
        }
        catch (Exception ex) when (IsExpectedNativeException(ex))
        {
            notes.Add($"读取物理核心拓扑失败，使用逻辑处理器 fallback：{ex.Message}");
        }

        return Enumerable.Range(0, Environment.ProcessorCount)
            .Select(static index => new PhysicalCoreRecord(
                index,
                0,
                [new LogicalProcessorRecord(index, 0, index)]))
            .ToArray();
    }

    private IReadOnlyList<CpuSetRecord> ReadCpuSetRecords(ICollection<string> notes)
    {
        try
        {
            var records = QueryCpuSetRecords()
                .Where(static record => record.LogicalProcessorIndex >= 0)
                .ToArray();
            if (records.Length > 0)
            {
                notes.Add("已读取 Windows CPU Sets，用于核心性能分和 LLC/CCD 辅助识别。");
            }

            return records;
        }
        catch (Exception ex) when (IsExpectedNativeException(ex))
        {
            notes.Add($"读取 Windows CPU Sets 失败：{ex.Message}");
            return [];
        }
    }
}
