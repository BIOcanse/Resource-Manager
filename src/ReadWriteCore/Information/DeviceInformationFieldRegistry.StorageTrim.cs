namespace ResourceManager.ReadWrite.Information;

public static partial class DeviceInformationFieldRegistry
{
    public static readonly DeviceInformationDefinition StorageAllTrimCapability = new(
        "storage_all_trim_capability", "storage", "all", "boolean", "boolean",
        "storage.windows_storage_stack_trim_enabled_reported_not_protocol_maximum", "windows.storage-device-trim-property", ReadVolatility.EventDriven("Windows storage stack TRIM configuration changed"));
}
