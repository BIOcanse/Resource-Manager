using ResourceManager.ReadWrite.Windows.Devices.Models;

namespace ResourceManager.ReadWrite.Information;

public static partial class WindowsInformationFieldProjection
{
    private sealed record PartitionAlignmentValue(uint PartitionNumber,
        string? LogicalStartRemainderBytes, string? LogicalLengthRemainderBytes,
        string? PhysicalStartRemainderBytes, string? PhysicalLengthRemainderBytes,
        bool? LogicalStartAligned, bool? LogicalLengthAligned, bool? PhysicalStartAligned, bool? PhysicalLengthAligned);

    private static IReadOnlyList<PartitionAlignmentValue>? PartitionAlignments(DeviceTopologyDiskLayout? layout,
        DiskAccessAlignmentInformation? alignment)
    {
        if (layout is null || alignment is null) return null;
        var logical = alignment.LogicalSectorSizeBytes;
        var physical = alignment.PhysicalSectorSizeBytes;
        var offset = alignment.PhysicalSectorOffsetBytes;
        var physicalKnown = logical > 0 && physical >= logical && physical % logical == 0
            && offset < physical && offset % logical == 0;
        var values = new List<PartitionAlignmentValue>(layout.Partitions.Count);
        foreach (var partition in layout.Partitions.OrderBy(static item => item.Number))
        {
            ulong? logicalStart = logical > 0 ? partition.StartingOffsetBytes % logical : null;
            ulong? logicalLength = logical > 0 ? partition.LengthBytes % logical : null;
            // LBA0 is already offset into a physical sector. Add that offset, not the distance to the next boundary.
            ulong? physicalStart = physicalKnown ? (partition.StartingOffsetBytes % physical + offset) % physical : null;
            ulong? physicalLength = physicalKnown ? partition.LengthBytes % physical : null;
            values.Add(new(partition.Number, Exact(logicalStart), Exact(logicalLength), Exact(physicalStart), Exact(physicalLength),
                Aligned(logicalStart), Aligned(logicalLength), Aligned(physicalStart), Aligned(physicalLength)));
        }
        return values;
    }

    private static string? Exact(ulong? remainder) => remainder is { } value ? Bytes(value) : null;
    private static bool? Aligned(ulong? remainder) => remainder is { } value ? value == 0 : null;
}
