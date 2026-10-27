namespace ResourceManager.ReadWrite.Information;

public static partial class DeviceInformationFieldRegistry
{
    public static readonly DeviceInformationDefinition CpuAllPhysicalAddressWidthDeclaredBits =
        new("cpu_all_physical_address_width_declared_bits", "cpu", "all", "unsigned-integer", "bit", "cpu.physical_address_width_declared", "x86.cpuid.80000008", ReadVolatility.Fixed);
    public static readonly DeviceInformationDefinition CpuAllLinearAddressWidthDeclaredBits =
        new("cpu_all_linear_address_width_declared_bits", "cpu", "all", "unsigned-integer", "bit", "cpu.linear_address_width_declared", "x86.cpuid.80000008", ReadVolatility.Fixed);
    public static readonly DeviceInformationDefinition CpuAmdNestedGuestPhysicalAddressWidthBits =
        new("cpu_amd_nested_guest_physical_address_width_bits", "cpu", "amd", "unsigned-integer", "bit", "cpu.amd_nested_guest_physical_address_width", "amd.cpuid.80000008", ReadVolatility.Fixed);
    public static readonly DeviceInformationDefinition CpuIntelVmxSupported =
        new("cpu_intel_vmx_supported", "cpu", "intel", "boolean", "boolean", "cpu.intel_vmx_hardware_support", "intel.cpuid.1", ReadVolatility.Fixed);
    public static readonly DeviceInformationDefinition CpuIntelSmxSupported =
        new("cpu_intel_smx_supported", "cpu", "intel", "boolean", "boolean", "cpu.intel_smx_hardware_support", "intel.cpuid.1", ReadVolatility.Fixed);
    public static readonly DeviceInformationDefinition CpuAmdSvmSupported =
        new("cpu_amd_svm_supported", "cpu", "amd", "boolean", "boolean", "cpu.amd_svm_hardware_support", "amd.cpuid.80000001", ReadVolatility.Fixed);
    public static readonly DeviceInformationDefinition CpuAmdSvmRevision =
        new("cpu_amd_svm_revision", "cpu", "amd", "unsigned-integer", "version8", "cpu.amd_svm_revision", "amd.cpuid.8000000a", ReadVolatility.Fixed);
    public static readonly DeviceInformationDefinition CpuAmdSvmAsidCountDeclared =
        new("cpu_amd_svm_asid_count_declared", "cpu", "amd", "unsigned-integer", "count", "cpu.amd_svm_asid_count_declared", "amd.cpuid.8000000a", ReadVolatility.Fixed);
    public static readonly DeviceInformationDefinition CpuAmdSvmFeature =
        new("cpu_amd_svm_feature", "cpu", "amd", "text-list", "text[]", "cpu.amd_svm_features_decoded_subset", "amd.cpuid.8000000a", ReadVolatility.Fixed);
}
