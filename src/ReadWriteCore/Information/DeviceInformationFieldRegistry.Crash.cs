namespace ResourceManager.ReadWrite.Information;

public static partial class DeviceInformationFieldRegistry
{
    public static readonly DeviceInformationDefinition CrashAllBugcheckCodeReported =
        new("crash_all_bugcheck_code_reported", "crash", "all", "unsigned64", "code", "crash.engine_reported_bugcheck_code", "windows.dbgeng");
    public static readonly DeviceInformationDefinition CrashAllBugcheckParametersReported =
        new("crash_all_bugcheck_parameters_reported", "crash", "all", "array", "raw", "crash.engine_reported_four_bugcheck_parameters_decimal_u64", "windows.dbgeng");
    public static readonly DeviceInformationDefinition CrashAllSuspectedModuleInterpreted =
        new("crash_all_suspected_module_interpreted", "crash", "all", "text", "text", "crash.first_resolved_stack_module_suspect_not_root_cause", "windows.dbgeng");
    public static readonly DeviceInformationDefinition CrashAllExceptionContextInterpreted =
        new("crash_all_exception_context_interpreted", "crash", "all", "object", "raw", "crash.engine_exception_event_and_available_amd64_thread_context", "windows.dbgeng");
    public static readonly DeviceInformationDefinition CrashAllSymbolizedStackInterpreted =
        new("crash_all_symbolized_stack_interpreted", "crash", "all", "array", "frames", "crash.engine_unwound_stack_nullable_symbol_names_not_causality", "windows.dbgeng");
}
