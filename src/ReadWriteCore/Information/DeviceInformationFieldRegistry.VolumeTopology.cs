namespace ResourceManager.ReadWrite.Information;

public static partial class DeviceInformationFieldRegistry
{
    public static readonly DeviceInformationDefinition VolumeAllDiskExtentsReported =
        new("volume_all_disk_extents_reported", "volume", "all", "disk-extents", "B", "volume.topology.disk_extents_reported", "windows.volume-metadata");
    public static readonly DeviceInformationDefinition VolumeAllMountPathsReported =
        new("volume_all_mount_paths_reported", "volume", "all", "text-list", "path", "volume.topology.mount_paths_reported", "windows.volume-metadata");
    public static readonly DeviceInformationDefinition VolumeAllFilesystemNameReported =
        new("volume_all_filesystem_name_reported", "volume", "all", "text", "text", "volume.topology.filesystem_name_reported", "windows.volume-metadata");
    public static readonly DeviceInformationDefinition VolumeAllLabelConfigured =
        new("volume_all_label_configured", "volume", "all", "text", "text", "volume.topology.label_configured", "windows.volume-metadata");
    public static readonly DeviceInformationDefinition VolumeAllFilesystemSerialReported =
        new("volume_all_filesystem_serial_reported", "volume", "all", "text", "hex32", "volume.topology.filesystem_serial_reported", "windows.volume-metadata");
    public static readonly DeviceInformationDefinition VolumeAllMaximumComponentLengthReported =
        new("volume_all_maximum_component_length_reported", "volume", "all", "unsigned-integer", "count", "volume.topology.maximum_component_length_reported", "windows.volume-metadata");
    public static readonly DeviceInformationDefinition VolumeAllFilesystemFlagsReported =
        new("volume_all_filesystem_flags_reported", "volume", "all", "unsigned-integer", "bitmask32", "volume.topology.filesystem_flags_reported", "windows.volume-metadata");
}
