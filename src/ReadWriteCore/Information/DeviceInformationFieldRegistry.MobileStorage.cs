namespace ResourceManager.ReadWrite.Information;

public static partial class DeviceInformationFieldRegistry
{
    public static readonly DeviceInformationDefinition MobileAllStorageFreeBytesReported =
        new("mobile_all_storage_free_bytes_reported", "mobile_device", "all", "array", "storage[]",
            "mobile.topology.smart-device-storage-name-and-free-bytes-reported-as-decimal-string-or-null",
            "windows.device-topology.smart-device-storage", ReadVolatility.Sampled);
}
