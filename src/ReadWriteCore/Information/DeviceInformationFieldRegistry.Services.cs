namespace ResourceManager.ReadWrite.Information;

public static partial class DeviceInformationFieldRegistry
{
    public static readonly DeviceInformationDefinition ServiceAllStateActual =
        new("service_all_state_actual", "service", "all", "unsigned-integer", "enum32", "service.scm_current_state_code:1=stopped,2=start_pending,3=stop_pending,4=running,5=continue_pending,6=pause_pending,7=paused", "windows.scm", ReadVolatility.EventDriven("Windows SCM service status changed"));
    public static readonly DeviceInformationDefinition ServiceAllProcessPidActual =
        new("service_all_process_pid_actual", "service", "all", "unsigned-integer", "pid32", "service.scm_reported_pid:0=no_associated_process;not_process_instance_identity;query_failure=null", "windows.scm", ReadVolatility.EventDriven("Windows SCM service status changed"));
    public static readonly DeviceInformationDefinition ServiceAllStartTypeConfigured =
        new("service_all_start_type_configured", "service", "all", "unsigned-integer", "enum32", "service.scm_configured_start_type_code:0=boot,1=system,2=automatic,3=demand,4=disabled", "windows.scm.query-config", ReadVolatility.EventDriven("Windows SCM service configuration changed"));
}

