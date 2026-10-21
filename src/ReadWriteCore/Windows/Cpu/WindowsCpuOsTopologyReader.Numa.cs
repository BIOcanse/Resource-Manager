using System.Runtime.InteropServices;
using ResourceManager.ReadWrite.Windows.Cpu.Models;
using NativeMethods = ResourceManager.ReadWrite.Windows.Cpu.WindowsCpuTopologyNativeMethods;

namespace ResourceManager.ReadWrite.Windows.Cpu;

public sealed partial class WindowsCpuOsTopologyReader
{
    public static IReadOnlyList<CpuOsNumaNode>? ReadNumaNodes() => ReadNumaNodes(new List<string>());

    public static IReadOnlyList<CpuOsNumaNode>? ReadNumaNodes(ICollection<string> notes)
    {
        try
        {
            var query = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 20348)
                ? NativeMethods.RelationNumaNodeEx : NativeMethods.RelationNumaNode;
            return ValidateNumaRelationships(QueryLogicalProcessorInformation(query, ParseNumaRelationship,
                responseRelationship: NativeMethods.RelationNumaNode));
        }
        catch (Exception exception) when (IsExpectedNativeException(exception))
        {
            notes.Add($"读取 Windows NUMA 关系失败：{exception.Message}");
            return null;
        }
    }

    private static CpuOsNumaNode ParseNumaRelationship(LogicalProcessorInfoRecord record)
    {
        if (record.Size < 32) throw new InvalidOperationException("Truncated NUMA relationship.");
        var groupCount = unchecked((ushort)Marshal.ReadInt16(record.Pointer, 30));
        if (groupCount == 0 && record.Size == 48) groupCount = 1; // Old x64 single GroupMask layout.
        if ((groupCount == 0 && record.Size != 32) || record.Size < 32 + groupCount * 16)
            throw new InvalidOperationException("Truncated NUMA group masks.");
        return new(unchecked((uint)Marshal.ReadInt32(record.Pointer, 8)),
            ReadGroupAffinities(record.Pointer, 32, groupCount, allowEmptyMask: true));
    }

    private static IReadOnlyList<CpuOsNumaNode> ValidateNumaRelationships(IReadOnlyList<CpuOsNumaNode> records)
    {
        var nodes = new HashSet<uint>();
        var processors = new HashSet<int>();
        foreach (var record in records)
        {
            if (!nodes.Add(record.NodeNumber)) throw new InvalidOperationException("Repeated NUMA node.");
            foreach (var processor in record.LogicalProcessorIds)
                if (!processors.Add(processor) || processors.Count > MaximumProcessorAddresses)
                    throw new InvalidOperationException("Overlapping or oversized NUMA processor relationships.");
        }
        return records;
    }

    public static IReadOnlyList<CpuOsNumaNode> ParseNumaInformation(byte[] data)
        => WithPinnedTopologyBuffer(data, (pointer, length) => ValidateNumaRelationships(
            ParseLogicalProcessorBuffer(pointer, length, NativeMethods.RelationNumaNode, ParseNumaRelationship)));
}
