namespace ResourceManager.ReadWrite.Information;

public static partial class DeviceInformationFieldRegistry
{
    public static readonly DeviceInformationDefinition SoftwareAllNameDeclared =
        new("software_all_name_declared", "installed_software", "all", "text", "text", "software.name_declared", "windows.uninstall-registry", ReadVolatility.EventDriven("Installed software registration changed"));
    public static readonly DeviceInformationDefinition SoftwareAllVersionDeclared =
        new("software_all_version_declared", "installed_software", "all", "text", "text", "software.version_declared", "windows.uninstall-registry", ReadVolatility.EventDriven("Installed software registration changed"));
    public static readonly DeviceInformationDefinition SoftwareAllInstallPathDeclared =
        new("software_all_install_path_declared", "installed_software", "all", "text", "text", "software.normalized_registry_install_location_declared", "windows.uninstall-registry", ReadVolatility.EventDriven("Installed software registration changed"));
    public static readonly DeviceInformationDefinition SoftwareAllReportedSize =
        new("software_all_reported_size", "installed_software", "all", "unsigned-integer", "B", "software.uninstall_estimated_size_kb_times_1024_reported", "windows.uninstall-registry", ReadVolatility.EventDriven("Installed software registration changed"));
}

