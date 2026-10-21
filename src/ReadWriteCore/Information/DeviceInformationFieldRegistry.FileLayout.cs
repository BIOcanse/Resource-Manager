namespace ResourceManager.ReadWrite.Information;

public static partial class DeviceInformationFieldRegistry
{
    public const string FileLayoutSourceId = "windows.fsctl.file-layout";

    public static readonly DeviceInformationDefinition FileAllExtentCountActual =
        new("file_all_extent_count_actual", "file", "all", "unsigned-integer", "count",
            "file.fsctl_get_retrieval_pointers_extent_count; each returned run including LCN=-1 contributes one; cluster runs are not bytes; resident data has no extent locations",
            FileLayoutSourceId);

    public static readonly DeviceInformationDefinition VolumeAllFragmentationMapActual =
        new("volume_all_fragmentation_map_actual", "volume", "all", "object", "map",
            "volume.fsctl_get_volume_bitmap_full_cluster_allocation_map; LSB-first bit 1=allocated 0=free; includes BytesPerCluster and explicit paged LCN coverage/timestamps; sequential pages are not one atomic snapshot",
            FileLayoutSourceId);
}
