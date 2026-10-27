namespace ResourceManager.ReadWrite.Information;

public static partial class DeviceInformationFieldRegistry
{
    public static readonly DeviceInformationDefinition StorageAllBusTypeDeclared =
        new("storage_all_bus_type_declared", "storage", "all", "unsigned-integer", "enum", "storage.os_bus_type_reported", "windows.storage-device-descriptor", ReadVolatility.Fixed);
}
