namespace ResourceManager.ReadWrite.Information;

public static partial class DeviceInformationFieldRegistry
{
    public static readonly DeviceInformationDefinition StorageAllAtaSmartAttributeNormalized =
        new("storage_all_ata_smart_attribute_normalized", "storage", "all", "object", "attribute[]",
            "storage.ata_smart_attribute_normalized", "windows.ata-smart");
    public static readonly DeviceInformationDefinition StorageAllAtaSmartAttributeRaw =
        new("storage_all_ata_smart_attribute_raw", "storage", "all", "object", "attribute[]",
            "storage.ata_smart_attribute_raw", "windows.ata-smart");
    public static readonly DeviceInformationDefinition StorageAllAtaSmartAttributeThreshold =
        new("storage_all_ata_smart_attribute_threshold", "storage", "all", "object", "attribute[]",
            "storage.ata_smart_attribute_threshold", "windows.ata-smart");
}
