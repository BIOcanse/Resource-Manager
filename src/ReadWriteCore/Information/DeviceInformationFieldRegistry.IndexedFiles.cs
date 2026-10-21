namespace ResourceManager.ReadWrite.Information;
public static partial class DeviceInformationFieldRegistry
{
    public static readonly DeviceInformationDefinition FileAllIndexedModifiedTimeActual = new("file_all_indexed_modified_time_actual", "indexed_file", "all", "text", "UTC", "file.last_write_time_stored_in_index_not_live_metadata", "windows.sqlite.file-index");
    public static readonly DeviceInformationDefinition FileAllIndexedPathActual = new("file_all_indexed_path_actual", "indexed_file", "all", "text", "text", "file.path_stored_in_index_for_original_software_root", "windows.sqlite.file-index");
    public static readonly DeviceInformationDefinition FileAllIndexedSizeActual = new("file_all_indexed_size_actual", "indexed_file", "all", "unsigned-integer", "B", "file.logical_size_stored_in_index_not_live_metadata_or_allocation", "windows.sqlite.file-index");
}
