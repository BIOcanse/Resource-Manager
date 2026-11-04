namespace ResourceManager.ReadWrite.Information;

public static partial class DeviceInformationFieldRegistry
{
    public static readonly DeviceInformationDefinition OsAllUptimeActual =
        new("os_all_uptime_actual", "operating_system", "all", "unsigned64", "s",
            "os.machine_uptime_tick_count64_floor_seconds_not_exact_boot_or_session_age", "windows.tick-count64", ReadVolatility.Sampled);
    public static readonly DeviceInformationDefinition OsAllBuildDeclared =
        new("os_all_build_declared", "operating_system", "all", "text", "text",
            "os.win32_operatingsystem_decimal_buildnumber_not_ubr_or_marketing_version", "windows.wmi.operating-system", ReadVolatility.Fixed);
    public static readonly DeviceInformationDefinition OsAllBootTimeActual =
        new("os_all_boot_time_actual", "operating_system", "all", "text", "UTC",
            "os.win32_operatingsystem_lastbootuptime_reported_restart_utc_dmtf_microseconds_not_resume_or_login", "windows.wmi.operating-system", ReadVolatility.Fixed);
    public static readonly DeviceInformationDefinition MachineAllAcPowerActual =
        new("machine_all_ac_power_actual", "machine", "all", "boolean", "state",
            "machine.system_ac_line_online", "windows.get-system-power-status", ReadVolatility.EventDriven("Windows power setting AC/DC source changed"));
}
