namespace ResourceManager.ReadWrite.Information;

public static partial class DeviceInformationFieldRegistry
{
    public static readonly DeviceInformationDefinition StorageAllOsLogicalSectorSizeReportedBytes =
        new("storage_all_os_logical_sector_size_reported_bytes", "storage", "all", "unsigned-integer", "B", "storage.alignment.os_logical_sector_size_reported_bytes", "windows.storage-access-alignment");
    public static readonly DeviceInformationDefinition StorageAllOsPhysicalSectorSizeReportedBytes =
        new("storage_all_os_physical_sector_size_reported_bytes", "storage", "all", "unsigned-integer", "B", "storage.alignment.os_physical_sector_size_reported_bytes", "windows.storage-access-alignment");
    public static readonly DeviceInformationDefinition StorageAllOsPhysicalSectorOffsetReportedBytes =
        new("storage_all_os_physical_sector_offset_reported_bytes", "storage", "all", "unsigned-integer", "B", "storage.alignment.os_physical_sector_offset_reported_bytes", "windows.storage-access-alignment");
    public static readonly DeviceInformationDefinition StorageAllOsCacheLineSizeReportedBytes =
        new("storage_all_os_cache_line_size_reported_bytes", "storage", "all", "unsigned-integer", "B", "storage.alignment.os_cache_line_size_reported_bytes", "windows.storage-access-alignment");
    public static readonly DeviceInformationDefinition StorageAllOsCacheAlignmentOffsetReportedBytes =
        new("storage_all_os_cache_alignment_offset_reported_bytes", "storage", "all", "unsigned-integer", "B", "storage.alignment.os_cache_alignment_offset_reported_bytes", "windows.storage-access-alignment");
    public static readonly DeviceInformationDefinition StorageAllPartitionAlignmentActual =
        new("storage_all_partition_alignment_actual", "storage", "all", "partition-alignment", "B", "storage.alignment.partition_alignment_actual", "windows.storage-access-alignment");
}
