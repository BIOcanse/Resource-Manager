namespace ResourceManager.ReadWrite.Information;

public static partial class DeviceInformationFieldRegistry
{
    public static readonly DeviceInformationDefinition StorageAllOsDiskNumberReported =
        new("storage_all_os_disk_number_reported", "storage", "all", "unsigned-integer", "count", "storage.layout.os_disk_number_reported", "windows.disk-layout");
    public static readonly DeviceInformationDefinition StorageAllPartitionStyleReported =
        new("storage_all_partition_style_reported", "storage", "all", "unsigned-integer", "enum32", "storage.layout.partition_style_reported", "windows.disk-layout");
    public static readonly DeviceInformationDefinition StorageAllGptDiskIdReported =
        new("storage_all_gpt_disk_id_reported", "storage", "all", "text", "uuid", "storage.layout.gpt_disk_id_reported", "windows.disk-layout");
    public static readonly DeviceInformationDefinition StorageAllGptStartingUsableOffsetReportedBytes =
        new("storage_all_gpt_starting_usable_offset_reported_bytes", "storage", "all", "unsigned-integer", "B", "storage.layout.gpt_starting_usable_offset_reported_bytes", "windows.disk-layout");
    public static readonly DeviceInformationDefinition StorageAllGptUsableLengthReportedBytes =
        new("storage_all_gpt_usable_length_reported_bytes", "storage", "all", "unsigned-integer", "B", "storage.layout.gpt_usable_length_reported_bytes", "windows.disk-layout");
    public static readonly DeviceInformationDefinition StorageAllGptMaximumPartitionCountReported =
        new("storage_all_gpt_maximum_partition_count_reported", "storage", "all", "unsigned-integer", "count", "storage.layout.gpt_maximum_partition_count_reported", "windows.disk-layout");
    public static readonly DeviceInformationDefinition StorageAllMbrDiskSignatureReported =
        new("storage_all_mbr_disk_signature_reported", "storage", "all", "text", "hex32", "storage.layout.mbr_disk_signature_reported", "windows.disk-layout");
    public static readonly DeviceInformationDefinition StorageAllMbrDiskChecksumReported =
        new("storage_all_mbr_disk_checksum_reported", "storage", "all", "text", "hex32", "storage.layout.mbr_disk_checksum_reported", "windows.disk-layout");
    public static readonly DeviceInformationDefinition StorageAllPartitionLayoutReported =
        new("storage_all_partition_layout_reported", "storage", "all", "partition-layout", "B", "storage.layout.partition_layout_reported", "windows.disk-layout");
    public static readonly DeviceInformationDefinition StorageAllVolumeRelationActual =
        new("storage_all_volume_relation_actual", "storage", "all", "volume-relations", "id", "storage.layout.volume_relation_actual", "windows.disk-layout");
    public static readonly DeviceInformationDefinition StorageAllPartitionRelationActual =
        new("storage_all_partition_relation_actual", "storage", "all", "partition-volume-relations", "id", "storage.layout.partition_relation_actual", "windows.disk-layout");
}
