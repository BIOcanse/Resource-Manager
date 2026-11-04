namespace ResourceManager.ReadWrite.Information;

public static partial class DeviceInformationFieldRegistry
{
    public static readonly DeviceInformationDefinition MobileAllBatteryPercentReported =
        new("mobile_all_battery_percent_reported", "mobile_device", "all", "number", "%",
            "mobile.wpd.device-root.power-level-reported-uint32-0-through-100", "windows.wpd.device-power-level", ReadVolatility.Sampled);

    public static readonly DeviceInformationDefinition MobileAllDeviceIdDeclared =
        new("mobile_all_device_id_declared", "mobile_device", "all", "text", "text",
            "mobile.wpd.device-root.persistent-unique-id-declared-lp-wstr", "windows.wpd.object-persistent-unique-id", ReadVolatility.Fixed);

    public static readonly DeviceInformationDefinition MobileAllFirmwareVersionDeclared =
        new("mobile_all_firmware_version_declared", "mobile_device", "all", "text", "text",
            "mobile.wpd.device-root.firmware-version-declared-lp-wstr", "windows.wpd.device-firmware-version", ReadVolatility.EventDriven("Portable device firmware updated"));

    public static readonly DeviceInformationDefinition MobileAllStorageCapacityReported =
        new("mobile_all_storage_capacity_reported", "mobile_storage", "all", "unsigned64", "B",
            "mobile.wpd.storage-functional-object.total-capacity-reported-vt-ui8-bytes", "windows.wpd.storage-capacity", ReadVolatility.EventDriven("Portable device storage configuration changed"));
}
