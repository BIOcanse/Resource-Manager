namespace ResourceManager.ReadWrite.Information;

public static partial class DeviceInformationFieldRegistry
{
    public static readonly DeviceInformationDefinition DriverAllPackageIdentityDeclared =
        new("driver_all_package_identity_declared", "driver_package", "all", "text", "text",
            "driver.dism_driver_package_published_name_exact_declared_identity", "windows.dism.driver-packages", ReadVolatility.EventDriven("Windows DISM driver package added or removed"));

    public static readonly DeviceInformationDefinition OsAllFeatureStateActual =
        new("os_all_feature_state_actual", "os_feature", "all", "unsigned-integer", "enum",
            "os.dism_feature_state_raw_numeric_enum_per_online_image_feature", "windows.dism.features", ReadVolatility.EventDriven("Windows optional feature servicing completed"));

    public static readonly DeviceInformationDefinition OsAllPackageStateActual =
        new("os_all_package_state_actual", "os_package", "all", "unsigned-integer", "enum",
            "os.dism_package_state_raw_numeric_enum_per_online_image_package", "windows.dism.packages", ReadVolatility.EventDriven("Windows package servicing completed"));
}
