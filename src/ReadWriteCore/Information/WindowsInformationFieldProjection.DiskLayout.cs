using System.Globalization;
using ResourceManager.ReadWrite.Windows.Devices.Models;
using F = ResourceManager.ReadWrite.Information.DeviceInformationFieldRegistry;

namespace ResourceManager.ReadWrite.Information;

public static partial class WindowsInformationFieldProjection
{
    internal const int MaximumDiskRelationExtents = 65536;
    internal const int MaximumPartitionRelationComparisons = 1048576;

    public static IReadOnlyList<DeviceInformationField> DiskLayoutFields(DeviceTopologyDiskLayoutInformation disk,
        IReadOnlyList<DeviceTopologyDiskLayoutInformation> disks, IReadOnlyList<DeviceTopologyVolumeSpaceInformation>? volumes,
        IReadOnlyDictionary<(StorageIdentifyProtocol Protocol, string Serial), DeviceTopologyStorageInformation[]>? physicalDiskCandidates)
    {
        var layout = disk.Layout;
        var mbr = layout?.Style == DiskPartitionStyle.Mbr ? layout.Mbr : null;
        var gpt = layout?.Style == DiskPartitionStyle.Gpt ? layout.Gpt : null;
        var extents = DiskVolumeExtents(disk, disks, volumes);
        return [
            Integer(F.StorageAllOsDiskNumberReported, disk.OsDiskNumberReported),
            Integer(F.StorageAllBusTypeDeclared, disk.BusTypeReported),
            Boolean(F.StorageAllTrimCapability, disk.TrimEnabledReported),
            Integer(F.StorageAllPartitionStyleReported, layout is null ? null : (uint?)layout.Style),
            Value(F.StorageAllGptDiskIdReported, gpt?.DiskId.ToString("D")),
            Integer(F.StorageAllGptStartingUsableOffsetReportedBytes, gpt?.StartingUsableOffsetBytes),
            Integer(F.StorageAllGptUsableLengthReportedBytes, gpt?.UsableLengthBytes),
            Integer(F.StorageAllGptMaximumPartitionCountReported, gpt?.MaximumPartitionCount),
            Value(F.StorageAllMbrDiskSignatureReported, mbr?.Signature.ToString("X8", CultureInfo.InvariantCulture)),
            Value(F.StorageAllMbrDiskChecksumReported, mbr?.Checksum.ToString("X8", CultureInfo.InvariantCulture)),
            Value(F.StorageAllPartitionLayoutReported, layout?.Partitions.Select(static partition => new PartitionValue(
                partition.Number, Bytes(partition.StartingOffsetBytes), Bytes(partition.LengthBytes), partition.RewriteReported, partition.IsServiceReported,
                partition.Mbr, partition.Gpt is null ? null : new PartitionGptValue(partition.Gpt.Type, partition.Gpt.PartitionId,
                    Bytes(partition.Gpt.Attributes), partition.Gpt.Name))).ToArray()),
            Value(F.StorageAllVolumeRelationActual, extents?.Select(static item => new DiskVolumeExtentValue(item.VolumeIdentity,
                Bytes(item.StartingOffsetBytes), Bytes(item.LengthBytes))).ToArray()),
            Value(F.StorageAllPartitionRelationActual, PartitionVolumeRelations(layout, extents)),
            Integer(F.StorageAllOsLogicalSectorSizeReportedBytes, disk.AccessAlignment?.LogicalSectorSizeBytes),
            Integer(F.StorageAllOsPhysicalSectorSizeReportedBytes, disk.AccessAlignment?.PhysicalSectorSizeBytes),
            Integer(F.StorageAllOsPhysicalSectorOffsetReportedBytes, disk.AccessAlignment?.PhysicalSectorOffsetBytes),
            Integer(F.StorageAllOsCacheLineSizeReportedBytes, disk.AccessAlignment?.CacheLineSizeBytes),
            Integer(F.StorageAllOsCacheAlignmentOffsetReportedBytes, disk.AccessAlignment?.CacheAlignmentOffsetBytes),
            Value(F.StorageAllPartitionAlignmentActual, PartitionAlignments(layout, disk.AccessAlignment)),
            Value(F.StorageAllPhysicalDiskRelationActual, PhysicalDiskRelation(disk, physicalDiskCandidates))];
    }

    private static string Bytes(ulong number) => number.ToString(CultureInfo.InvariantCulture);
    private sealed record PartitionValue(uint Number, string StartingOffsetBytes, string LengthBytes, bool RewriteReported,
        bool IsServiceReported, DiskPartitionMbr? Mbr, PartitionGptValue? Gpt);
    private sealed record PartitionGptValue(Guid Type, Guid PartitionId, string Attributes, string Name);
    private sealed record DiskVolumeExtent(string VolumeIdentity, ulong StartingOffsetBytes, ulong LengthBytes);
    private sealed record DiskVolumeExtentValue(string VolumeIdentity, string StartingOffsetBytes, string LengthBytes);
    private sealed record PartitionVolumeRelationValue(uint PartitionNumber, string VolumeIdentity, string OverlapStartingOffsetBytes,
        string OverlapLengthBytes, bool RangesEqual);

    private static IReadOnlyList<DiskVolumeExtent>? DiskVolumeExtents(DeviceTopologyDiskLayoutInformation disk,
        IReadOnlyList<DeviceTopologyDiskLayoutInformation> disks, IReadOnlyList<DeviceTopologyVolumeSpaceInformation>? volumes)
    {
        if (disk.OsDiskNumberReported is not { } number || volumes is null
            || disks.Count(item => item.OsDiskNumberReported == number) != 1) return null;
        var result = new List<DiskVolumeExtent>();
        var examined = 0;
        foreach (var volume in volumes.OrderBy(static item => item.DeviceIdentity, StringComparer.Ordinal))
        {
            if (volume.DiskExtents is null || volume.DiskExtents.Count > MaximumDiskRelationExtents - examined) return null;
            examined += volume.DiskExtents.Count;
            foreach (var extent in volume.DiskExtents.Where(item => item.DiskNumberReported == number)
                .OrderBy(static item => item.StartingOffsetBytes).ThenBy(static item => item.ExtentLengthBytes))
                result.Add(new(volume.DeviceIdentity, extent.StartingOffsetBytes, extent.ExtentLengthBytes));
        }
        return result;
    }

    private static IReadOnlyList<PartitionVolumeRelationValue>? PartitionVolumeRelations(DeviceTopologyDiskLayout? layout,
        IReadOnlyList<DiskVolumeExtent>? extents)
    {
        if (layout is null || extents is null || (long)layout.Partitions.Count * extents.Count > MaximumPartitionRelationComparisons) return null;
        var result = new List<PartitionVolumeRelationValue>();
        foreach (var partition in layout.Partitions.OrderBy(static item => item.Number))
        {
            foreach (var extent in extents)
            {
                var start = Math.Max(partition.StartingOffsetBytes, extent.StartingOffsetBytes);
                // Checked arithmetic also rejects malformed persisted typed data; it is never clipped into a plausible relation.
                if (partition.LengthBytes > ulong.MaxValue - partition.StartingOffsetBytes
                    || extent.LengthBytes > ulong.MaxValue - extent.StartingOffsetBytes) return null;
                var end = Math.Min(partition.StartingOffsetBytes + partition.LengthBytes, extent.StartingOffsetBytes + extent.LengthBytes);
                if (end <= start) continue;
                result.Add(new(partition.Number, extent.VolumeIdentity, Bytes(start), Bytes(end - start),
                    partition.StartingOffsetBytes == extent.StartingOffsetBytes && partition.LengthBytes == extent.LengthBytes));
            }
        }
        return result;
    }
}
