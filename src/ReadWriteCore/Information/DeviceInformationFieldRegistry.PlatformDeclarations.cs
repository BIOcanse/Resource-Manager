namespace ResourceManager.ReadWrite.Information;

public static partial class DeviceInformationFieldRegistry
{
    public static readonly DeviceInformationDefinition OsAllSessionIdActual =
        new("os_all_session_id_actual", "operating_system", "all", "unsigned64", "session-id",
            "os.local_wts_reported_sessionid_per_user_login_bound_to_native_logontime_not_console_or_calling_process", "windows.wts.sessions", ReadVolatility.Fixed);
    public static readonly DeviceInformationDefinition OsAllActivationStateReported =
        new("os_all_activation_state_reported", "operating_system", "all", "unsigned-integer", "enum32",
            "os.softwarelicensingproduct_license_status_per_windows_product_row_not_aggregate_or_legal_validation", "windows.wmi.licensing", ReadVolatility.EventDriven("SoftwareLicensingProduct license status changed"));
    public static readonly DeviceInformationDefinition EnvironmentAllVariableConfigured =
        new("environment_all_variable_configured", "environment_variable", "all", "text", "text",
            "environment.registry_machine_or_explicit_user_unexpanded_configured_string_not_inherited_environment", "windows.registry.environment", ReadVolatility.EventDriven("Machine or explicit-user environment registry key changed"));
    public static readonly DeviceInformationDefinition StartupAllEntryConfigured =
        new("startup_all_entry_configured", "startup_entry", "all", "text", "text",
            "startup.run_runonce_startup_folder_and_boot_logon_task_configured_payload_not_execution", "windows.startup.configuration", ReadVolatility.EventDriven("Startup registry key, folder or boot/logon task registration changed"));
    public static readonly DeviceInformationDefinition SoftwareAllPackageFamilyDeclared =
        new("software_all_package_family_declared", "msix_package", "all", "text", "text",
            "software.packagemanager_registered_package_family_per_explicit_user_and_fullname_not_running_process", "windows.packagemanager", ReadVolatility.EventDriven("PackageManager package registered or unregistered"));
}
