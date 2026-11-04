using System.Globalization;
using ResourceManager.ReadWrite.Windows.Devices.Models;
using F = ResourceManager.ReadWrite.Information.DeviceInformationFieldRegistry;

namespace ResourceManager.ReadWrite.Information;

public static partial class WindowsInformationFieldProjection
{
    public static IReadOnlyList<DeviceInformationField> VolumeSpaceFields(DeviceTopologyVolumeSpaceInformation volume)
        => [
            VolumeBytes(F.VolumeAllSizeActual, volume.TotalBytes),
            VolumeBytes(F.VolumeAllFreeBytesActual, volume.AvailableBytes),
            VolumeBytes(F.VolumeAllPoolUnavailableBytesActual, volume.PoolUnavailableBytes),
            VolumeBytes(F.VolumeAllCallerTotalBytesActual, volume.CallerTotalBytes),
            VolumeBytes(F.VolumeAllUserAvailableBytesActual, volume.CallerAvailableBytes),
            VolumeBytes(F.VolumeAllCallerPoolUnavailableBytesActual, volume.CallerPoolUnavailableBytes),
            VolumeBytes(F.VolumeAllUsedBytesActual, volume.UsedBytes),
            VolumeBytes(F.VolumeAllReservedBytesActual, volume.ReservedBytes),
            VolumeBytes(F.VolumeAllStorageReserveBytesActual, volume.StorageReserveBytes),
            VolumeBytes(F.VolumeAllAvailableCommittedBytesActual, volume.AvailableCommittedBytes),
            VolumeBytes(F.VolumeAllPoolAvailableBytesActual, volume.PoolAvailableBytes),
            Integer(F.VolumeAllSectorsPerAllocationUnitActual, volume.SectorsPerAllocationUnit),
            Integer(F.VolumeAllBytesPerSectorActual, volume.BytesPerSector),
            Integer(F.VolumeAllAllocationUnitBytesActual, volume.AllocationUnitBytes),
            Text(F.VolumeAllSpaceQueryCallerSid, volume.QueryCallerSid),
            Value(F.VolumeAllDiskExtentsReported, volume.DiskExtents?.Select(static extent => new VolumeDiskExtentValue(
                extent.DiskNumberReported, extent.StartingOffsetBytes.ToString(CultureInfo.InvariantCulture),
                extent.ExtentLengthBytes.ToString(CultureInfo.InvariantCulture))).ToArray()),
            Value(F.VolumeAllMountPathsReported, volume.MountPaths),
            Text(F.VolumeAllFilesystemNameReported, volume.FileSystem?.Name),
            Value(F.VolumeAllLabelConfigured, volume.FileSystem?.Label),
            Value(F.VolumeAllFilesystemSerialReported, volume.FileSystem?.SerialReported.ToString("X8", CultureInfo.InvariantCulture)),
            Integer(F.VolumeAllMaximumComponentLengthReported, volume.FileSystem?.MaximumComponentLengthReported),
            Integer(F.VolumeAllFilesystemFlagsReported, volume.FileSystem?.FlagsReported)];

    private sealed record VolumeDiskExtentValue(uint DiskNumberReported, string StartingOffsetBytes, string ExtentLengthBytes);

    private static DeviceInformationField VolumeBytes(DeviceInformationDefinition definition, UInt128? value)
        => Value(definition, value?.ToString(CultureInfo.InvariantCulture));
}
