namespace ResourceManager.ReadWrite.Information;

public static partial class DeviceInformationFieldRegistry
{
    public static readonly DeviceInformationDefinition CpuAmdCcdId =
        new("cpu_amd_ccd_id", "cpu", "amd", "text", "text",
            "cpu.amd_ccd_hardware_apic_domain", "amd.cpuid.80000026");
    public static readonly DeviceInformationDefinition CpuAmdCcxId =
        new("cpu_amd_ccx_id", "cpu", "amd", "text", "text",
            "cpu.amd_ccx_hardware_apic_domain", "amd.cpuid.80000026");
    public static readonly DeviceInformationDefinition CpuAmdCcdCoreMembership =
        new("cpu_amd_ccd_core_membership", "cpu", "amd", "array", "core-membership[]",
            "cpu.amd_ccd_core_membership", "amd.cpuid.80000026");
}
