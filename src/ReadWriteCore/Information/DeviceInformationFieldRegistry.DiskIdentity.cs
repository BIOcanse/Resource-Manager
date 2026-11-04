namespace ResourceManager.ReadWrite.Information;

public static partial class DeviceInformationFieldRegistry
{
    public static readonly DeviceInformationDefinition StorageAllPhysicalDiskRelationActual =
        new("storage_all_physical_disk_relation_actual", "storage", "all", "text", "id", "storage.identity.physical_disk_relation_actual", "windows.storage-device-identity", ReadVolatility.EventDriven("Windows storage layout or volume configuration changed"));
}
