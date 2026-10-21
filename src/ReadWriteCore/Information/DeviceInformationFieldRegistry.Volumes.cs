namespace ResourceManager.ReadWrite.Information;

public static partial class DeviceInformationFieldRegistry
{
    public static readonly DeviceInformationDefinition VolumeAllSizeActual =
        new("volume_all_size_actual", "volume", "all", "unsigned-integer", "B", "volume.space.size_actual", "windows.volume-space");
    public static readonly DeviceInformationDefinition VolumeAllFreeBytesActual =
        new("volume_all_free_bytes_actual", "volume", "all", "unsigned-integer", "B", "volume.space.free_bytes_actual", "windows.volume-space");
    public static readonly DeviceInformationDefinition VolumeAllPoolUnavailableBytesActual =
        new("volume_all_pool_unavailable_bytes_actual", "volume", "all", "unsigned-integer", "B", "volume.space.pool_unavailable_bytes_actual", "windows.volume-space");
    public static readonly DeviceInformationDefinition VolumeAllCallerTotalBytesActual =
        new("volume_all_caller_total_bytes_actual", "volume", "all", "unsigned-integer", "B", "volume.space.caller_total_bytes_actual", "windows.volume-space");
    public static readonly DeviceInformationDefinition VolumeAllUserAvailableBytesActual =
        new("volume_all_user_available_bytes_actual", "volume", "all", "unsigned-integer", "B", "volume.space.caller_available_bytes_actual", "windows.volume-space");
    public static readonly DeviceInformationDefinition VolumeAllCallerPoolUnavailableBytesActual =
        new("volume_all_caller_pool_unavailable_bytes_actual", "volume", "all", "unsigned-integer", "B", "volume.space.caller_pool_unavailable_bytes_actual", "windows.volume-space");
    public static readonly DeviceInformationDefinition VolumeAllUsedBytesActual =
        new("volume_all_used_bytes_actual", "volume", "all", "unsigned-integer", "B", "volume.space.used_bytes_actual", "windows.volume-space");
    public static readonly DeviceInformationDefinition VolumeAllReservedBytesActual =
        new("volume_all_reserved_bytes_actual", "volume", "all", "unsigned-integer", "B", "volume.space.reserved_bytes_actual", "windows.volume-space");
    public static readonly DeviceInformationDefinition VolumeAllStorageReserveBytesActual =
        new("volume_all_storage_reserve_bytes_actual", "volume", "all", "unsigned-integer", "B", "volume.space.storage_reserve_bytes_actual", "windows.volume-space");
    public static readonly DeviceInformationDefinition VolumeAllAvailableCommittedBytesActual =
        new("volume_all_available_committed_bytes_actual", "volume", "all", "unsigned-integer", "B", "volume.space.available_committed_bytes_actual", "windows.volume-space");
    public static readonly DeviceInformationDefinition VolumeAllPoolAvailableBytesActual =
        new("volume_all_pool_available_bytes_actual", "volume", "all", "unsigned-integer", "B", "volume.space.pool_available_bytes_actual", "windows.volume-space");
    public static readonly DeviceInformationDefinition VolumeAllSectorsPerAllocationUnitActual =
        new("volume_all_sectors_per_allocation_unit_actual", "volume", "all", "unsigned-integer", "count", "volume.space.sectors_per_allocation_unit_actual", "windows.volume-space");
    public static readonly DeviceInformationDefinition VolumeAllBytesPerSectorActual =
        new("volume_all_bytes_per_sector_actual", "volume", "all", "unsigned-integer", "B", "volume.space.bytes_per_sector_actual", "windows.volume-space");
    public static readonly DeviceInformationDefinition VolumeAllAllocationUnitBytesActual =
        new("volume_all_allocation_unit_bytes_actual", "volume", "all", "unsigned-integer", "B", "volume.space.allocation_unit_bytes_actual", "windows.volume-space");
    public static readonly DeviceInformationDefinition VolumeAllSpaceQueryCallerSid =
        new("volume_all_space_query_caller_sid", "volume", "all", "text", "sid", "volume.space.space_query_caller_sid", "windows.volume-space");
}
