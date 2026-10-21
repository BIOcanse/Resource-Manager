namespace ResourceManager.ReadWrite.Information;

public static partial class DeviceInformationFieldRegistry
{
    public static readonly DeviceInformationDefinition CpuAllCpuidVendor =
        new("cpu_all_cpuid_vendor", "cpu", "all", "text", "text", "cpu.cpuid_vendor", "x86.cpuid");
    public static readonly DeviceInformationDefinition CpuAllBrand =
        new("cpu_all_brand", "cpu", "all", "text", "text", "cpu.cpuid_brand", "x86.cpuid");
    public static readonly DeviceInformationDefinition CpuAllCpuidFamily =
        new("cpu_all_cpuid_family", "cpu", "all", "unsigned-integer", "count", "cpu.cpuid_display_family", "x86.cpuid.1");
    public static readonly DeviceInformationDefinition CpuAllCpuidModel =
        new("cpu_all_cpuid_model", "cpu", "all", "unsigned-integer", "count", "cpu.cpuid_display_model", "x86.cpuid.1");
    public static readonly DeviceInformationDefinition CpuAllCpuidStepping =
        new("cpu_all_cpuid_stepping", "cpu", "all", "unsigned-integer", "count", "cpu.cpuid_stepping", "x86.cpuid.1");
    public static readonly DeviceInformationDefinition CpuAllInstructionFeature =
        new("cpu_all_instruction_feature", "cpu", "all", "text-list", "text[]", "cpu.hardware_instruction_features_decoded_subset", "x86.cpuid.decoded-subset");
    public static readonly DeviceInformationDefinition CpuAllCpuidSignature =
        new("cpu_all_cpuid_signature", "cpu", "all", "unsigned-integer", "register32", "cpu.cpuid_signature", "x86.cpuid.1");
    public static readonly DeviceInformationDefinition CpuAllInitialApicId =
        new("cpu_all_initial_apic_id", "cpu", "all", "unsigned-integer", "id8", "cpu.initial_apic_id_8bit", "x86.cpuid.1");
    public static readonly DeviceInformationDefinition CpuAllCpuidIdentityRegisters =
        new("cpu_all_cpuid_identity_registers", "cpu", "all", "cpuid-registers", "register32[]", "cpu.cpuid_captured_identity_registers", "x86.cpuid");
    public static readonly DeviceInformationDefinition CpuIntelHardwareCoreType =
        new("cpu_intel_hardware_core_type", "cpu", "intel", "unsigned-integer", "enum8", "cpu.intel_microarchitecture_core_type", "intel.cpuid.1a");
    public static readonly DeviceInformationDefinition CpuIntelNativeModelId =
        new("cpu_intel_native_model_id", "cpu", "intel", "unsigned-integer", "model24", "cpu.intel_core_native_model_id", "intel.cpuid.1a");
    public static readonly DeviceInformationDefinition CpuIntelX2ApicId =
        new("cpu_intel_x2apic_id", "cpu", "intel", "unsigned-integer", "id32", "cpu.intel_x2apic_id", "intel.cpuid.topology");
    public static readonly DeviceInformationDefinition CpuAmdExtendedApicId =
        new("cpu_amd_extended_apic_id", "cpu", "amd", "unsigned-integer", "id32", "cpu.amd_extended_apic_id", "amd.cpuid.8000001e");
    public static readonly DeviceInformationDefinition CpuAllOsUsableFeature =
        new("cpu_all_os_usable_feature", "cpu", "all", "text-list", "text[]", "cpu.os_reported_instruction_features_subset", "windows.processor-features");
    public static readonly DeviceInformationDefinition CpuAllSupportedXcr0StateMask =
        new("cpu_all_supported_xcr0_state_mask", "cpu", "all", "unsigned-integer", "bitmask64", "cpu.supported_xcr0_state_mask", "x86.cpuid.d.0");
    public static readonly DeviceInformationDefinition CpuIntelSupportedXssStateMask =
        new("cpu_intel_supported_xss_state_mask", "cpu", "intel", "unsigned-integer", "bitmask64", "cpu.intel_supported_xss_state_mask", "intel.cpuid.d.1");
    public static readonly DeviceInformationDefinition CpuIntelAvx10Version =
        new("cpu_intel_avx10_version", "cpu", "intel", "unsigned-integer", "version8", "cpu.intel_avx10_version", "intel.cpuid.24.0");
    public static readonly DeviceInformationDefinition CpuAmdSupportedXssStateMask =
        new("cpu_amd_supported_xss_state_mask", "cpu", "amd", "unsigned-integer", "bitmask32", "cpu.amd_supported_xss_state_mask", "amd.cpuid.d.1");
    public static readonly DeviceInformationDefinition CpuAllOsEnabledXStateMask =
        new("cpu_all_os_enabled_xstate_mask", "cpu", "all", "unsigned-integer", "bitmask64", "cpu.os_enabled_xstate_mask", "windows.xstate");
}
