using ResourceManager.ReadWrite.Windows.Cpu.Models;
using NativeMethods = ResourceManager.ReadWrite.Windows.Cpu.WindowsCpuTopologyNativeMethods;

namespace ResourceManager.ReadWrite.Windows.Cpu;

public sealed partial class WindowsCpuOsTopologyReader
{
    public static IReadOnlyList<CpuOsDie>? ReadDieInformation(IReadOnlyList<CpuOsPhysicalCore>? cores,
        IReadOnlyList<CpuOsPackage>? packages, ICollection<string> notes)
    {
        try { return CreateDieInformation(QueryLogicalProcessorRecords(NativeMethods.RelationProcessorDie), cores, packages); }
        catch (Exception exception) when (IsExpectedNativeException(exception))
        {
            notes.Add($"读取 Windows CPU die 关系失败：{exception.Message}");
            return null;
        }
    }

    public static IReadOnlyList<CpuOsDie> ParseDieInformation(byte[] data,
        IReadOnlyList<CpuOsPhysicalCore>? cores = null, IReadOnlyList<CpuOsPackage>? packages = null)
        => WithPinnedTopologyBuffer(data, (pointer, length) => CreateDieInformation(
            ValidateProcessorRelationships(ParseLogicalProcessorBuffer(pointer, length,
                NativeMethods.RelationProcessorDie, ParseProcessorRelationship)), cores, packages));

    private static IReadOnlyList<CpuOsDie> CreateDieInformation(IReadOnlyList<LogicalProcessorRelationshipRecord> records,
        IReadOnlyList<CpuOsPhysicalCore>? cores, IReadOnlyList<CpuOsPackage>? packages)
    {
        var dieByProcessor = new Dictionary<int, int>();
        for (var index = 0; index < records.Count; index++)
            foreach (var id in records[index].LogicalProcessorIds) dieByProcessor.Add(id, index);
        if (cores is not null)
        {
            var seen = new HashSet<int>();
            foreach (var core in cores)
            {
                int? die = null;
                foreach (var id in core.LogicalProcessorIds)
                {
                    if (!seen.Add(id) || !dieByProcessor.TryGetValue(id, out var member) || (die.HasValue && die.Value != member))
                        throw new InvalidOperationException("CPU core and die memberships are inconsistent.");
                    die = member;
                }
            }
            if (seen.Count != dieByProcessor.Count) throw new InvalidOperationException("CPU core and die address sets are inconsistent.");
        }
        var packageByProcessor = new Dictionary<int, string>();
        if (packages is not null)
        {
            foreach (var package in packages)
                foreach (var id in package.LogicalProcessorIds)
                    if (!packageByProcessor.TryAdd(id, package.DeviceIdentity) || !dieByProcessor.ContainsKey(id))
                        throw new InvalidOperationException("CPU package and die address sets are inconsistent.");
            if (packageByProcessor.Count != dieByProcessor.Count)
                throw new InvalidOperationException("CPU package and die address sets are inconsistent.");
        }
        var result = new List<CpuOsDie>();
        foreach (var record in records)
        {
            var ids = record.LogicalProcessorIds.Order().ToArray();
            string? parent = null;
            if (packages is not null)
                foreach (var id in ids)
                {
                    var current = packageByProcessor[id];
                    if (parent is not null && parent != current) throw new InvalidOperationException("CPU die crosses package boundaries.");
                    parent = current;
                }
            result.Add(new($"windows-cpu-die|{string.Join(',', ids.Select(static id => $"{id / 64}:{id % 64}"))}",
                Array.AsReadOnly(ids), parent));
        }
        return result.OrderBy(static die => die.DeviceIdentity, StringComparer.Ordinal).ToArray();
    }
}
