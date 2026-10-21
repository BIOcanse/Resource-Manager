using ResourceManager.ReadWrite.Windows.Cpu.Models;
using NativeMethods = ResourceManager.ReadWrite.Windows.Cpu.WindowsCpuTopologyNativeMethods;

namespace ResourceManager.ReadWrite.Windows.Cpu;

public sealed partial class WindowsCpuOsTopologyReader
{
    public static IReadOnlyList<CpuOsPackage>? ReadPackageInformation(
        IReadOnlyList<CpuOsPhysicalCore>? cores, ICollection<string> notes)
    {
        try
        {
            var records = QueryLogicalProcessorRecords(NativeMethods.RelationProcessorPackage);
            return CreatePackageInformation(records, cores);
        }
        catch (Exception exception) when (IsExpectedNativeException(exception))
        {
            notes.Add($"读取 Windows CPU 封装关系失败：{exception.Message}");
            return null;
        }
    }

    public static IReadOnlyList<CpuOsPackage> ParsePackageInformation(
        byte[] data, IReadOnlyList<CpuOsPhysicalCore>? cores = null)
        => WithPinnedTopologyBuffer(data, (pointer, length) => CreatePackageInformation(
            ValidateProcessorRelationships(ParseLogicalProcessorBuffer(pointer, length,
                NativeMethods.RelationProcessorPackage, ParseProcessorRelationship)), cores));

    private static IReadOnlyList<CpuOsPackage> CreatePackageInformation(
        IReadOnlyList<LogicalProcessorRelationshipRecord> records, IReadOnlyList<CpuOsPhysicalCore>? cores)
    {
        var packageByProcessor = new Dictionary<int, int>();
        for (var index = 0; index < records.Count; index++)
            foreach (var id in records[index].LogicalProcessorIds)
                packageByProcessor.Add(id, index);
        if (cores is not null)
        {
            var coreProcessors = new HashSet<int>();
            foreach (var core in cores)
            {
                int? package = null;
                foreach (var id in core.LogicalProcessorIds)
                {
                    if (!coreProcessors.Add(id) || !packageByProcessor.TryGetValue(id, out var membership)
                        || (package.HasValue && package.Value != membership))
                        throw new InvalidOperationException("CPU core and package memberships are inconsistent.");
                    package = membership;
                }
            }
            if (coreProcessors.Count != packageByProcessor.Count)
                throw new InvalidOperationException("CPU core and package address sets are inconsistent.");
        }
        return records.Select(static record =>
        {
            var ids = record.LogicalProcessorIds.Order().ToArray();
            var addresses = ids.Select(static id => $"{id / 64}:{id % 64}");
            return new CpuOsPackage($"windows-cpu-package|{string.Join(',', addresses)}", Array.AsReadOnly(ids));
        }).OrderBy(static package => package.DeviceIdentity, StringComparer.Ordinal).ToArray();
    }
}
