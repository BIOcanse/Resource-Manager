namespace ResourceManager.ReadWrite.Information;
public static partial class DeviceInformationFieldRegistry
{
    public const string MftSourceId = "windows.ntfs.mft.disk-usage";
    public static readonly DeviceInformationDefinition DirectoryAllFileCountAggregated = new("directory_all_file_count_aggregated", "directory", "all", "unsigned-integer", "count", "directory.namespace_entry_count_unnamed_data", MftSourceId, ReadVolatility.EventDriven("NTFS file allocation or namespace changed"));
    public static readonly DeviceInformationDefinition DirectoryAllLogicalSizeAggregated = new("directory_all_logical_size_aggregated", "directory", "all", "unsigned-integer", "B", "directory.namespace_entry_logical_bytes_unnamed_data", MftSourceId, ReadVolatility.EventDriven("NTFS file allocation or namespace changed"));
    public static readonly DeviceInformationDefinition FileAllLogicalSizeActual = new("file_all_logical_size_actual", "file", "all", "unsigned-integer", "B", "file.unnamed_data_logical_bytes", MftSourceId, ReadVolatility.EventDriven("NTFS file allocation or namespace changed"));
    public static readonly DeviceInformationDefinition DirectoryAllUniqueAllocatedSizeAggregated = new("directory_all_unique_allocated_size_aggregated", "directory", "all", "unsigned-integer", "B", "directory.unique_file_object_unnamed_data_allocated_bytes", MftSourceId, ReadVolatility.EventDriven("NTFS file allocation or namespace changed"));
    public static readonly DeviceInformationDefinition FileAllAllocatedSizeActual = new("file_all_allocated_size_actual", "file", "all", "unsigned-integer", "B", "file.unnamed_data_allocated_bytes", MftSourceId, ReadVolatility.EventDriven("NTFS file allocation or namespace changed"));
}
