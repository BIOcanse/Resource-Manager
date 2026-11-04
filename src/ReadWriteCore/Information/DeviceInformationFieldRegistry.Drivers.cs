namespace ResourceManager.ReadWrite.Information;

public static partial class DeviceInformationFieldRegistry
{
    public static readonly DeviceInformationDefinition DriverAllProviderDeclared = new(
        "driver_all_provider_declared", "driver", "all", "text", "text",
        "driver.current_device_bound_inf_provider_declared", "windows.cfgmgr32.driver-properties", ReadVolatility.EventDriven("Windows device-bound driver package changed"));
    public static readonly DeviceInformationDefinition DriverAllDateDeclared = new(
        "driver_all_date_declared", "driver", "all", "text", "ISO8601-date",
        "driver.current_device_bound_inf_driverver_date_declared_not_install_time", "windows.cfgmgr32.driver-properties", ReadVolatility.EventDriven("Windows device-bound driver package changed"));
    public static readonly DeviceInformationDefinition DriverAllVersionDeclared = new(
        "driver_all_version_declared", "driver", "all", "text", "text",
        "driver.current_device_bound_driver_version_declared", "windows.cfgmgr32.driver-properties", ReadVolatility.EventDriven("Windows device-bound driver package changed"));
}
