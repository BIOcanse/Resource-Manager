namespace ResourceManager.ReadWrite.Information;

public static partial class DeviceInformationFieldRegistry
{
    public static readonly DeviceInformationDefinition ProcessAllImageSignatureReported =
        new("process_all_image_signature_reported", "process", "all", "unsigned-integer", "enum",
            "process.image_file_winverifytrust_generic_verify_v2_raw_status_u32_no_network_revocation", "windows.wintrust.process-image-file", ReadVolatility.EventDriven("Process image file content changed"));

    public static readonly DeviceInformationDefinition ProcessAllLoadedModuleActual =
        new("process_all_loaded_module_actual", "process_module", "all", "text", "text",
            "process.loaded_module_full_path_reported_for_module_base", "windows.psapi.enumprocessmodulesex", ReadVolatility.EventDriven("Windows kernel process image load or unload"));

    public static readonly DeviceInformationDefinition ProcessAllThreadStateActual =
        new("process_all_thread_state_actual", "process_thread", "all", "object", "enum",
            "process.thread_state_and_wait_reason_raw_numeric_codes", "windows.ntquerysysteminformation.systemprocessinformation", ReadVolatility.Sampled);
}
